using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Perception.Randomization.Randomizers.SampleRandomizers;
using UnityEngine.Perception.Randomization.Scenarios;
using UnityEngine.SceneManagement;

namespace ProstheticGrasping.EditorTools
{
    /// <summary>
    /// A small local HTTP API for driving this Editor from the command line, because
    /// com.unity.pipeline requires Unity 6000.0 and this project is on 2020.3.
    ///
    /// It listens on 127.0.0.1 only. The port and token are in Library/EditorBridge.json;
    /// send the token in an X-Bridge-Token header. Requests with an Origin header are
    /// rejected so that a web page cannot reach the API. Nothing here saves a scene.
    ///
    ///   GET  /ping                     answers even while the main thread is busy
    ///   GET  /status                   Editor, scene, Game view and run progress
    ///   GET  /console?limit=&amp;type=     latest console entries; type: error|warning|log|all
    ///   POST /playmode?action=         start or stop
    ///   POST /scene/open?path=         refuses if the open scene has unsaved changes
    ///   POST /gameview/resolution?width=&amp;height=
    ///   GET  /scenario                 current run-count values
    ///   POST /scenario?iterationCount=&amp;numIterationsPerGrasp=   not saved to the scene file
    ///   POST /screenshot?path=         absolute path to a new .png file
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorBridge
    {
        const string DescriptorPath = "Library/EditorBridge.json";
        const string PortKey = "EditorBridge.Port";
        const string TokenKey = "EditorBridge.Token";
        const string AnnouncedKey = "EditorBridge.Announced";
        const string TokenHeader = "X-Bridge-Token";
        const int MainThreadTimeoutMs = 30000;
        const int MaxConsoleMessageLength = 4000;
        const int MaxResolution = 7680;

        // Number of grasp type - object part pairs in Data_collection.
        // WristCameraMovement stops the run unless iterations == pairs * iterations per grasp.
        const int GraspPairCount = 31;

        // Bits of UnityEditor.ConsoleWindow.Mode.
        const int ErrorModes = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11)
            | (1 << 17) | (1 << 20) | (1 << 21) | (1 << 22);
        const int WarningModes = (1 << 7) | (1 << 9) | (1 << 12);

        static readonly ConcurrentQueue<Action> s_MainThreadQueue = new ConcurrentQueue<Action>();
        static HttpListener s_Listener;
        static string s_Token;
        static double s_NextStartAttempt;
        static volatile bool s_Running;

        static EditorBridge()
        {
            // Import workers are also Unity.exe with this project, started with -batchMode.
            if (Application.isBatchMode)
                return;

            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.update += OnEditorUpdate;
        }

        static void OnEditorUpdate()
        {
            if (s_Listener == null && EditorApplication.timeSinceStartup >= s_NextStartAttempt)
                TryStart();

            while (s_MainThreadQueue.TryDequeue(out var action))
                action();
        }

        static void TryStart()
        {
            s_Token = SessionState.GetString(TokenKey, string.Empty);
            if (s_Token.Length == 0)
            {
                s_Token = NewToken();
                SessionState.SetString(TokenKey, s_Token);
            }

            var port = SessionState.GetInt(PortKey, 0);
            if (port == 0)
                port = FindFreePort();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (Exception)
            {
                // The port is taken or not released yet after a reload: use another one.
                ((IDisposable)listener).Dispose();
                SessionState.SetInt(PortKey, FindFreePort());
                s_NextStartAttempt = EditorApplication.timeSinceStartup + 1.0;
                return;
            }

            SessionState.SetInt(PortKey, port);
            s_Listener = listener;
            s_Running = true;
            File.WriteAllText(DescriptorPath, Json(new Dictionary<string, object>
            {
                ["port"] = port,
                ["token"] = s_Token,
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
            }));

            new Thread(() => ListenLoop(listener)) { IsBackground = true, Name = "EditorBridge" }.Start();

            if (!SessionState.GetBool(AnnouncedKey, false))
            {
                SessionState.SetBool(AnnouncedKey, true);
                Debug.Log($"[EditorBridge] Listening on 127.0.0.1:{port}");
            }
        }

        static void Stop()
        {
            s_Running = false;
            var listener = s_Listener;
            s_Listener = null;
            if (listener == null)
                return;
            try
            {
                listener.Close();
            }
            catch (Exception)
            {
                // Already closed: there is nothing left to release.
            }
        }

        static void OnQuitting()
        {
            Stop();
            try
            {
                File.Delete(DescriptorPath);
            }
            catch (IOException)
            {
                // A stale descriptor only makes the next client fail to connect.
            }
        }

        static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        static int FindFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static void ListenLoop(HttpListener listener)
        {
            while (s_Running)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception)
                {
                    return; // The listener was closed.
                }
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        static void HandleRequest(HttpListenerContext context)
        {
            var status = 200;
            Dictionary<string, object> body;
            try
            {
                Authorize(context.Request);
                body = Route(context.Request);
                body["ok"] = true;
            }
            catch (BridgeException e)
            {
                status = e.Status;
                body = new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message };
            }
            catch (Exception e)
            {
                status = 500;
                body = new Dictionary<string, object> { ["ok"] = false, ["error"] = e.ToString() };
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(Json(body));
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client went away or the domain is reloading: nobody is left to tell.
            }
        }

        static void Authorize(HttpListenerRequest request)
        {
            if (request.Headers["Origin"] != null)
                throw new BridgeException(403, "Browser requests are not allowed.");
            if (!TokensMatch(request.Headers[TokenHeader], s_Token))
                throw new BridgeException(401, $"Missing or wrong {TokenHeader} header.");
        }

        static bool TokensMatch(string given, string expected)
        {
            if (given == null || given.Length != expected.Length)
                return false;
            var difference = 0;
            for (var i = 0; i < expected.Length; i++)
                difference |= given[i] ^ expected[i];
            return difference == 0;
        }

        static Dictionary<string, object> Route(HttpListenerRequest request)
        {
            var route = request.HttpMethod + " " + request.Url.AbsolutePath.TrimEnd('/');
            switch (route)
            {
                case "GET /ping":
                    return new Dictionary<string, object> { ["pong"] = true };
                case "GET /status":
                    return OnMainThread(GetStatus);
                case "GET /console":
                    var limit = OptionalInt(request, "limit", 50);
                    var type = request.QueryString["type"] ?? "all";
                    return OnMainThread(() => GetConsole(limit, type));
                case "POST /playmode":
                    var action = Required(request, "action");
                    return OnMainThread(() => SetPlayMode(action));
                case "POST /scene/open":
                    var scenePath = Required(request, "path");
                    return OnMainThread(() => OpenScene(scenePath));
                case "POST /gameview/resolution":
                    var width = RequiredInt(request, "width");
                    var height = RequiredInt(request, "height");
                    return OnMainThread(() => SetGameViewResolution(width, height));
                case "GET /scenario":
                    return OnMainThread(GetScenario);
                case "POST /scenario":
                    var iterationCount = RequiredInt(request, "iterationCount");
                    var perGrasp = RequiredInt(request, "numIterationsPerGrasp");
                    return OnMainThread(() => SetScenario(iterationCount, perGrasp));
                case "POST /screenshot":
                    var screenshotPath = Required(request, "path");
                    return OnMainThread(() => TakeScreenshot(screenshotPath));
                default:
                    throw new BridgeException(404, $"Unknown route: {route}");
            }
        }

        static T OnMainThread<T>(Func<T> work)
        {
            const int Queued = 0, Started = 1, Abandoned = 2;
            var state = Queued;
            var result = default(T);
            Exception failure = null;
            using (var done = new ManualResetEventSlim())
            {
                s_MainThreadQueue.Enqueue(() =>
                {
                    // A request that already timed out must not take effect later.
                    if (Interlocked.CompareExchange(ref state, Started, Queued) != Queued)
                        return;
                    try
                    {
                        result = work();
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                    done.Set();
                });

                if (!done.Wait(MainThreadTimeoutMs))
                {
                    if (Interlocked.CompareExchange(ref state, Abandoned, Queued) == Queued)
                        throw new BridgeException(503, "The Editor main thread is busy; the request was not run.");
                    done.Wait();
                }
            }

            if (failure != null)
                throw failure;
            return result;
        }

        static Dictionary<string, object> GetStatus()
        {
            var scene = SceneManager.GetActiveScene();
            var gameView = Handles.GetMainGameViewSize();
            var status = new Dictionary<string, object>
            {
                ["unityVersion"] = Application.unityVersion,
                ["project"] = Path.GetDirectoryName(Application.dataPath),
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["compileFailed"] = EditorUtility.scriptCompilationFailed,
                ["scene"] = scene.path,
                ["sceneDirty"] = scene.isDirty,
                ["gameViewWidth"] = (int)gameView.x,
                ["gameViewHeight"] = (int)gameView.y,
            };

            var scenario = UnityEngine.Object.FindObjectOfType<FixedLengthScenario>();
            if (EditorApplication.isPlaying && scenario != null)
            {
                status["currentIteration"] = scenario.currentIteration;
                status["currentIterationFrame"] = scenario.currentIterationFrame;
                status["iterationCount"] = scenario.constants.iterationCount;
            }
            return status;
        }

        static Dictionary<string, object> GetConsole(int limit, string type)
        {
            if (type != "all" && type != "error" && type != "warning" && type != "log")
                throw new BridgeException(400, "type must be error, warning, log or all.");

            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var entriesType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
            var entryType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntry");
            var entry = Activator.CreateInstance(entryType);
            var messageField = entryType.GetField("message", Any);
            var fileField = entryType.GetField("file", Any);
            var lineField = entryType.GetField("line", Any);
            var modeField = entryType.GetField("mode", Any);

            var entries = new List<object>();
            int errors = 0, warnings = 0, logs = 0;
            var total = (int)entriesType.GetMethod("StartGettingEntries", Any).Invoke(null, null);
            try
            {
                var getEntry = entriesType.GetMethod("GetEntryInternal", Any);
                for (var i = 0; i < total; i++)
                {
                    getEntry.Invoke(null, new[] { i, entry });
                    var mode = (int)modeField.GetValue(entry);
                    var entryKind = (mode & ErrorModes) != 0 ? "error" : (mode & WarningModes) != 0 ? "warning" : "log";
                    if (entryKind == "error") errors++;
                    else if (entryKind == "warning") warnings++;
                    else logs++;

                    if (type != "all" && type != entryKind)
                        continue;

                    var message = (string)messageField.GetValue(entry) ?? string.Empty;
                    if (message.Length > MaxConsoleMessageLength)
                        message = message.Substring(0, MaxConsoleMessageLength) + "...";
                    entries.Add(new Dictionary<string, object>
                    {
                        ["type"] = entryKind,
                        ["message"] = message,
                        ["file"] = fileField.GetValue(entry),
                        ["line"] = lineField.GetValue(entry),
                    });
                }
            }
            finally
            {
                entriesType.GetMethod("EndGettingEntries", Any).Invoke(null, null);
            }

            return new Dictionary<string, object>
            {
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["logs"] = logs,
                ["entries"] = entries.Skip(Math.Max(0, entries.Count - Math.Max(0, limit))).ToList(),
            };
        }

        static Dictionary<string, object> SetPlayMode(string action)
        {
            switch (action)
            {
                case "start":
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                        throw new BridgeException(409, "The Editor is compiling or importing.");
                    if (EditorUtility.scriptCompilationFailed)
                        throw new BridgeException(409, "Scripts have compile errors.");
                    EditorApplication.isPlaying = true;
                    break;
                case "stop":
                    EditorApplication.isPlaying = false;
                    break;
                default:
                    throw new BridgeException(400, "action must be start or stop.");
            }
            return new Dictionary<string, object> { ["requested"] = action };
        }

        static Dictionary<string, object> OpenScene(string path)
        {
            RequireEditMode();
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".unity", StringComparison.Ordinal)
                || AssetDatabase.AssetPathToGUID(path).Length == 0 || !File.Exists(path))
                throw new BridgeException(400, $"Not a scene in this project: {path}");

            // Opening another scene over unsaved changes would discard them or block on a dialog.
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new BridgeException(409, $"'{SceneManager.GetSceneAt(i).path}' has unsaved changes.");
            }

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new Dictionary<string, object> { ["scene"] = scene.path };
        }

        static Dictionary<string, object> SetGameViewResolution(int width, int height)
        {
            RequireEditMode();
            if (width < 1 || height < 1 || width > MaxResolution || height > MaxResolution)
                throw new BridgeException(400, $"width and height must be between 1 and {MaxResolution}.");

            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var editor = typeof(EditorWindow).Assembly;
            var gameViewType = editor.GetType("UnityEditor.GameView");
            var windows = Resources.FindObjectsOfTypeAll(gameViewType);
            if (windows.Length == 0)
                throw new BridgeException(409, "No Game view is open.");

            var sizesType = editor.GetType("UnityEditor.GameViewSizes");
            var sizeType = editor.GetType("UnityEditor.GameViewSize");
            var sizeKindType = editor.GetType("UnityEditor.GameViewSizeType");
            var fixedResolution = Enum.Parse(sizeKindType, "FixedResolution");
            var sizes = typeof(ScriptableSingleton<>).MakeGenericType(sizesType)
                .GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var group = sizesType.GetProperty("currentGroup", Any).GetValue(sizes);
            var groupType = group.GetType();
            var getSize = groupType.GetMethod("GetGameViewSize", Any);

            var index = FindFixedSize(group, groupType, getSize, sizeType, fixedResolution, width, height);
            if (index < 0)
            {
                var size = Activator.CreateInstance(sizeType, fixedResolution, width, height, $"{width}x{height}");
                groupType.GetMethod("AddCustomSize", Any).Invoke(group, new[] { size });
                index = FindFixedSize(group, groupType, getSize, sizeType, fixedResolution, width, height);
            }

            var window = (EditorWindow)windows[0];
            gameViewType.GetMethod("SizeSelectionCallback", Any).Invoke(window, new object[] { index, null });
            window.Repaint();

            var actual = Handles.GetMainGameViewSize();
            return new Dictionary<string, object>
            {
                ["gameViewWidth"] = (int)actual.x,
                ["gameViewHeight"] = (int)actual.y,
            };
        }

        static int FindFixedSize(object group, Type groupType, MethodInfo getSize, Type sizeType, object fixedResolution, int width, int height)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var total = (int)groupType.GetMethod("GetTotalCount", Any).Invoke(group, null);
            for (var i = 0; i < total; i++)
            {
                var size = getSize.Invoke(group, new object[] { i });
                if (Equals(sizeType.GetProperty("sizeType", Any).GetValue(size), fixedResolution)
                    && (int)sizeType.GetProperty("width", Any).GetValue(size) == width
                    && (int)sizeType.GetProperty("height", Any).GetValue(size) == height)
                    return i;
            }
            return -1;
        }

        static Dictionary<string, object> GetScenario()
        {
            var scenario = FindScenario();
            var scene = scenario.gameObject.scene;
            return new Dictionary<string, object>
            {
                ["iterationCount"] = scenario.constants.iterationCount,
                ["startIteration"] = scenario.constants.startIteration,
                ["framesPerIteration"] = scenario.constants.framesPerIteration,
                ["numIterationsPerGrasp"] = scenario.GetRandomizer<WristCameraMovement>().numIterationsPerGrasp,
                ["graspPairCount"] = GraspPairCount,
                ["sceneDirty"] = scene.isDirty,
            };
        }

        static Dictionary<string, object> SetScenario(int iterationCount, int perGrasp)
        {
            RequireEditMode();
            var scenario = FindScenario();
            var runs = iterationCount - scenario.constants.startIteration;
            if (perGrasp < 1 || runs != GraspPairCount * perGrasp)
            {
                throw new BridgeException(400,
                    $"iterationCount - startIteration ({runs}) must equal {GraspPairCount} * numIterationsPerGrasp ({perGrasp}).");
            }

            Undo.RecordObject(scenario, "Set run counts");
            scenario.constants.iterationCount = iterationCount;
            scenario.GetRandomizer<WristCameraMovement>().numIterationsPerGrasp = perGrasp;
            EditorUtility.SetDirty(scenario);
            EditorSceneManager.MarkSceneDirty(scenario.gameObject.scene);
            return GetScenario();
        }

        static FixedLengthScenario FindScenario()
        {
            var scenario = UnityEngine.Object.FindObjectOfType<FixedLengthScenario>();
            if (scenario == null)
                throw new BridgeException(409, "The open scene has no FixedLengthScenario. Open Data_collection first.");
            return scenario;
        }

        static Dictionary<string, object> TakeScreenshot(string path)
        {
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException(400, "path must be an absolute path to a .png file.");
            if (File.Exists(path))
                throw new BridgeException(409, $"File already exists: {path}");
            if (!Directory.Exists(Path.GetDirectoryName(path)))
                throw new BridgeException(400, $"Folder does not exist: {Path.GetDirectoryName(path)}");

            // ScreenCapture writes the Game view at the end of the frame, so only in Play mode.
            if (EditorApplication.isPlaying)
            {
                ScreenCapture.CaptureScreenshot(path);
                return new Dictionary<string, object> { ["path"] = path, ["pending"] = true };
            }

            var cameraObject = GameObject.Find("WristCamera");
            var camera = cameraObject != null ? cameraObject.GetComponent<Camera>() : Camera.main;
            if (camera == null)
                throw new BridgeException(409, "The open scene has no camera to render.");

            var size = Handles.GetMainGameViewSize();
            var width = (int)size.x;
            var height = (int)size.y;
            var target = RenderTexture.GetTemporary(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
            return new Dictionary<string, object> { ["path"] = path, ["pending"] = false, ["width"] = width, ["height"] = height };
        }

        static void RequireEditMode()
        {
            if (EditorApplication.isPlaying)
                throw new BridgeException(409, "Not allowed in Play mode.");
        }

        static string Required(HttpListenerRequest request, string name)
        {
            var value = request.QueryString[name];
            if (string.IsNullOrEmpty(value))
                throw new BridgeException(400, $"Missing parameter: {name}");
            return value;
        }

        static int RequiredInt(HttpListenerRequest request, string name)
        {
            if (!int.TryParse(Required(request, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new BridgeException(400, $"{name} must be an integer.");
            return value;
        }

        static int OptionalInt(HttpListenerRequest request, string name, int fallback)
        {
            return request.QueryString[name] == null ? fallback : RequiredInt(request, name);
        }

        static string Json(object value)
        {
            var builder = new StringBuilder();
            WriteJson(builder, value);
            return builder.ToString();
        }

        static void WriteJson(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    break;
                case string text:
                    WriteJsonString(builder, text);
                    break;
                case int number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    break;
                case Dictionary<string, object> map:
                    builder.Append('{');
                    var firstKey = true;
                    foreach (var pair in map)
                    {
                        if (!firstKey)
                            builder.Append(',');
                        firstKey = false;
                        WriteJsonString(builder, pair.Key);
                        builder.Append(':');
                        WriteJson(builder, pair.Value);
                    }
                    builder.Append('}');
                    break;
                case IEnumerable list:
                    builder.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem)
                            builder.Append(',');
                        firstItem = false;
                        WriteJson(builder, item);
                    }
                    builder.Append(']');
                    break;
                default:
                    WriteJsonString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        static void WriteJsonString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        sealed class BridgeException : Exception
        {
            public int Status { get; }

            public BridgeException(int status, string message) : base(message)
            {
                Status = status;
            }
        }
    }
}
