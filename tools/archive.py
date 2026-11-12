"""Archive a render's frames on the S3 server (MinIO) as WebDataset tar shards. Used by render.py.

Layout in the bucket (default s3://grasp-renders/<run_id>/):
    frames/00000.tar, 00001.tar, ...   ~--shard-gb each; a sequence is never split across shards
    frames.json                        written last: shards (bytes, sha256), sequences, generation.json

Inside a shard, one WebDataset sample per sequence, keyed by its index in generation order (the same
index as record/reference.json):
    00012.json      sequence_id, labels [object, grasp type, preshape], part, sequence_parameters
    00012.00.png ... 00012.89.png   the RGB frames, by step

Shards are deterministic (sorted members, fixed mtime/owner), so an interrupted upload resumes: a shard
already on the server with the same sha256 is skipped. The archive is immutable: a shard or frames.json
that exists with different content is an error, never overwritten. Integrity: each PUT is signed with
the shard's sha256, which the server checks against the received body; a HEAD afterwards checks size
and the stored sha256.

Credentials: MINIO_ACCESS_KEY / MINIO_SECRET_KEY from the environment, or from an env file (default
<repo>/.env, not tracked). Endpoint: --s3-endpoint, or S3_ENDPOINT from the environment or
the env file (no default). Standard library only.
"""
import datetime as dt
import hashlib
import hmac
import http.client
import json
import os
import shutil
import tarfile
import urllib.parse
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

DEFAULT_BUCKET = "grasp-renders"
EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()
FORMAT = "webdataset-v1"


# ---------------------------------------------------------------- minimal S3 client (SigV4)

def _hmac(key: bytes, msg: str) -> bytes:
    return hmac.new(key, msg.encode(), hashlib.sha256).digest()


class S3:
    def __init__(self, endpoint: str, access_key: str, secret_key: str, region: str = "us-east-1"):
        u = urllib.parse.urlsplit(endpoint)
        if u.scheme not in ("http", "https") or not u.netloc:
            raise ValueError(f"bad S3 endpoint {endpoint!r}")
        self.endpoint, self.https, self.host = endpoint, u.scheme == "https", u.netloc
        self.access_key, self.secret_key, self.region = access_key, secret_key, region

    def request(self, method: str, bucket: str, key: str = "", body=None, payload_sha256: str = EMPTY_SHA256,
                length: int | None = None, headers: dict | None = None):
        path = "/" + bucket + ("/" + urllib.parse.quote(key, safe="/-_.~") if key else "")
        amz_date = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        h = {"host": self.host, "x-amz-date": amz_date, "x-amz-content-sha256": payload_sha256}
        h.update({k.lower(): str(v).strip() for k, v in (headers or {}).items()})
        signed = ";".join(sorted(h))
        canonical = "\n".join([method, path, "", *(f"{k}:{h[k]}" for k in sorted(h)), "", signed, payload_sha256])
        scope = f"{amz_date[:8]}/{self.region}/s3/aws4_request"
        to_sign = "\n".join(["AWS4-HMAC-SHA256", amz_date, scope, hashlib.sha256(canonical.encode()).hexdigest()])
        k = _hmac(_hmac(_hmac(_hmac(("AWS4" + self.secret_key).encode(), amz_date[:8]), self.region), "s3"),
                  "aws4_request")
        h["authorization"] = (f"AWS4-HMAC-SHA256 Credential={self.access_key}/{scope}, SignedHeaders={signed}, "
                              f"Signature={hmac.new(k, to_sign.encode(), hashlib.sha256).hexdigest()}")
        if length is not None:
            h["content-length"] = str(length)
        cls = http.client.HTTPSConnection if self.https else http.client.HTTPConnection
        conn = cls(self.host, timeout=600, blocksize=1 << 20)
        try:
            conn.request(method, path, body=body, headers=h)
            r = conn.getresponse()
            return r.status, {k.lower(): v for k, v in r.getheaders()}, r.read()
        finally:
            conn.close()

    def head(self, bucket: str, key: str = "") -> dict | None:
        status, headers, _ = self.request("HEAD", bucket, key)
        if status == 404:
            return None
        if status != 200:
            raise RuntimeError(f"HEAD {bucket}/{key}: HTTP {status}")
        return headers

    def get(self, bucket: str, key: str) -> bytes | None:
        status, _, data = self.request("GET", bucket, key)
        if status == 404:
            return None
        if status != 200:
            raise RuntimeError(f"GET {bucket}/{key}: HTTP {status} {data[:300]!r}")
        return data

    def put_bytes(self, bucket: str, key: str, data: bytes, content_type: str = "application/octet-stream"):
        sha = hashlib.sha256(data).hexdigest()
        status, _, resp = self.request("PUT", bucket, key, body=data, payload_sha256=sha, length=len(data),
                                       headers={"content-type": content_type, "x-amz-meta-sha256": sha})
        if status != 200:
            raise RuntimeError(f"PUT {bucket}/{key}: HTTP {status} {resp[:300]!r}")

    def put_file(self, bucket: str, key: str, path: Path, sha256: str):
        size = path.stat().st_size
        with open(path, "rb") as f:
            status, _, resp = self.request("PUT", bucket, key, body=f, payload_sha256=sha256, length=size,
                                           headers={"content-type": "application/x-tar", "x-amz-meta-sha256": sha256})
        if status != 200:
            raise RuntimeError(f"PUT {bucket}/{key}: HTTP {status} {resp[:300]!r}")


def read_env_file(path: Path) -> dict:
    env = {}
    if path.is_file():
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                env[k.strip()] = v.strip().strip('"').strip("'")
    return env


def connect(endpoint: str | None, env_file: Path) -> S3:
    env = {**read_env_file(env_file), **os.environ}
    key, secret = env.get("MINIO_ACCESS_KEY"), env.get("MINIO_SECRET_KEY")
    if not key or not secret:
        raise RuntimeError(f"MINIO_ACCESS_KEY / MINIO_SECRET_KEY not set (environment or {env_file})")
    endpoint = endpoint or env.get("S3_ENDPOINT")
    if not endpoint:
        raise RuntimeError(f"S3 endpoint not set: --s3-endpoint or S3_ENDPOINT (environment or {env_file})")
    return S3(endpoint, key, secret)


def check_bucket(s3: S3, bucket: str):
    """Fail early (before a render) when the server, the credentials or the bucket do not work."""
    try:
        status, _, _ = s3.request("HEAD", bucket)
    except OSError as e:
        raise RuntimeError(f"cannot reach {s3.endpoint}: {e}") from e
    if status == 404:
        raise RuntimeError(f"bucket {bucket} does not exist on {s3.endpoint}")
    if status != 200:
        raise RuntimeError(f"bucket {bucket} on {s3.endpoint}: HTTP {status} (credentials?)")


# ---------------------------------------------------------------- shards

class _HashingWriter:
    def __init__(self, f):
        self.f, self.sha, self.n = f, hashlib.sha256(), 0

    def write(self, b):
        self.sha.update(b)
        self.n += len(b)
        return self.f.write(b)

    def tell(self):
        return self.n

    def flush(self):
        self.f.flush()


def _add(tar: tarfile.TarFile, name: str, data: bytes | None = None, src: Path | None = None):
    info = tarfile.TarInfo(name)
    info.size = len(data) if data is not None else src.stat().st_size
    info.mtime, info.mode, info.uid, info.gid, info.uname, info.gname = 0, 0o644, 0, 0, "", ""
    if data is not None:
        import io
        tar.addfile(info, io.BytesIO(data))
    else:
        with open(src, "rb") as f:
            tar.addfile(info, f)


def plan_shards(seqs: list[dict], rgb: Path, shard_bytes: int) -> list[list[int]]:
    """Indices of seqs per shard, in generation order, closing a shard once it reaches shard_bytes."""
    shards, cur, size = [], [], 0
    for i, s in enumerate(seqs):
        cur.append(i)
        size += sum((rgb / Path(c["filename"]).name).stat().st_size for c in s["captures"])
        if size >= shard_bytes:
            shards.append(cur)
            cur, size = [], 0
    if cur:
        shards.append(cur)
    return shards


def write_shard(path: Path, seqs: list[dict], indices: list[int], rgb: Path) -> tuple[int, str]:
    with open(path, "wb") as raw:
        w = _HashingWriter(raw)
        with tarfile.open(fileobj=w, mode="w", format=tarfile.PAX_FORMAT) as tar:
            for i in indices:
                s = seqs[i]
                meta = {k: s[k] for k in ("sequence_id", "labels", "part", "sequence_parameters")}
                meta.update(index=i, frames=len(s["captures"]))
                _add(tar, f"{i:05d}.json", data=(json.dumps(meta, sort_keys=True) + "\n").encode())
                for c in s["captures"]:
                    _add(tar, f"{i:05d}.{c['step']:02d}.png", src=rgb / Path(c["filename"]).name)
    return w.n, w.sha.hexdigest()


# ---------------------------------------------------------------- upload

def upload_run(out: Path, seqs: list[dict], rgb: Path, generation: dict, s3: S3, bucket: str,
               shard_gb: float = 1.0, log=print) -> dict:
    """Pack and upload the frames of a verified run; returns the archive summary for generation.json."""
    run_id = generation["run_id"]
    prefix = f"{run_id}/"
    existing = s3.get(bucket, prefix + "frames.json")
    if existing is not None:
        manifest = json.loads(existing)
        if [s["sequence_id"] for s in manifest["sequences"]] != [s["sequence_id"] for s in seqs]:
            raise RuntimeError(f"s3://{bucket}/{prefix}frames.json exists with other sequences; not overwriting")
        log(f"already archived: s3://{bucket}/{prefix} ({len(manifest['shards'])} shards)")
        return summary(manifest, s3, bucket)

    shards = plan_shards(seqs, rgb, int(shard_gb * 1e9))
    tmp = out / "_shards"
    tmp.mkdir(exist_ok=True)
    entries: list[dict | None] = [None] * len(shards)
    started = dt.datetime.now()

    def upload(k: int, path: Path, size: int, sha: str):
        key = f"{prefix}frames/{k:05d}.tar"
        head = s3.head(bucket, key)
        if head is not None and head.get("x-amz-meta-sha256") == sha and int(head["content-length"]) == size:
            log(f"  shard {k + 1}/{len(shards)} already on the server")
        elif head is not None:
            raise RuntimeError(f"s3://{bucket}/{key} exists with different content; not overwriting")
        else:
            t0 = dt.datetime.now()
            s3.put_file(bucket, key, path, sha)
            head = s3.head(bucket, key)
            if head is None or int(head["content-length"]) != size or head.get("x-amz-meta-sha256") != sha:
                raise RuntimeError(f"s3://{bucket}/{key}: size or sha256 differs after upload")
            secs = max((dt.datetime.now() - t0).total_seconds(), 1e-3)
            log(f"  shard {k + 1}/{len(shards)}: {size / 1e9:.2f} GB in {secs:.0f} s ({size / 1e6 / secs:.0f} MB/s)")
        path.unlink()

    # Pack shard k+1 while shard k uploads; at most 2 uploads in flight, so at most ~3 shards on disk.
    with ThreadPoolExecutor(max_workers=2) as pool:
        pending = []
        for k, indices in enumerate(shards):
            path = tmp / f"{k:05d}.tar"
            size, sha = write_shard(path, seqs, indices, rgb)
            entries[k] = {"name": f"frames/{k:05d}.tar", "bytes": size, "sha256": sha,
                          "sequences": [indices[0], indices[-1]], "count": len(indices)}
            pending.append(pool.submit(upload, k, path, size, sha))
            if len(pending) >= 2:
                pending.pop(0).result()
        for f in pending:
            f.result()
    shutil.rmtree(tmp, ignore_errors=True)

    manifest = {
        "format": FORMAT,
        "run_id": run_id,
        "created": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "endpoint": s3.endpoint,
        "bucket": bucket,
        "shards": entries,
        "sequences": [{"index": i, "sequence_id": s["sequence_id"], "labels": s["labels"], "part": s["part"],
                       "frames": len(s["captures"]), "shard": next(k for k, ix in enumerate(shards) if i in ix)}
                      for i, s in enumerate(seqs)],
        "generation": generation,
    }
    s3.put_bytes(bucket, prefix + "frames.json", (json.dumps(manifest, indent=1) + "\n").encode(),
                 "application/json")
    total = sum(e["bytes"] for e in entries)
    secs = (dt.datetime.now() - started).total_seconds()
    log(f"archived {len(seqs)} sequences in {len(entries)} shards, {total / 1e9:.1f} GB in {secs:.0f} s "
        f"-> s3://{bucket}/{prefix}")
    return summary(manifest, s3, bucket)


def summary(manifest: dict, s3: S3, bucket: str) -> dict:
    return {
        "status": "ok",
        "url": f"s3://{bucket}/{manifest['run_id']}/",
        "endpoint": s3.endpoint,
        "format": manifest["format"],
        "manifest": "frames.json",
        "manifest_sha256": hashlib.sha256((json.dumps(manifest, indent=1) + "\n").encode()).hexdigest(),
        "shards": len(manifest["shards"]),
        "sequences": len(manifest["sequences"]),
        "bytes": sum(e["bytes"] for e in manifest["shards"]),
        "uploaded": manifest["created"],
    }


def delete_frames(out: Path, frames_dir: Path, archive: dict | None):
    """Remove the Perception output folder once the frames are archived and record/ holds the JSON."""
    if not archive or archive.get("status") != "ok":
        raise RuntimeError("frames are not archived; not deleting them")
    if not (out / "record" / "perception.zip").is_file():
        raise RuntimeError(f"{out / 'record'} is missing; not deleting the frames")
    if frames_dir.parent != out:
        raise RuntimeError(f"unexpected frames folder {frames_dir}")
    shutil.rmtree(frames_dir)
