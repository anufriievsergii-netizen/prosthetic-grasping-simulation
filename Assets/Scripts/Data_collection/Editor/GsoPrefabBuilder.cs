using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Perception.GroundTruth.LabelManagement;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the prefabs of the objects imported from Google Scanned Objects by
/// python_scripts/GSO/import_gso.py, with the same structure as the YCB prefabs:
///  $name$                   (Labeling, GraspObjectInfo)
///  |- textured              (the mesh, with a MeshCollider)
///  |- |- large_diameter[left]   (grasp parallelepipeds: inactive, renderer disabled, BoxCollider)
///  |- |- tripod[nops]
/// The grasp parallelepipeds are placed onto the mesh surface according to the
/// grasp_spec.json of each object. They are a starting point: check them in the
/// prefab and adjust them by hand if needed (rebuilding the prefab overwrites them).
/// </summary>
public static class GsoPrefabBuilder
{
    const string MODELS_DIR = "Assets/3Dmodels/GSO";
    const string PREFABS_DIR = "Assets/Prefabs/GSO";
    // The transparent material of the YCB grasp parallelepipeds
    const string GRASP_MATERIAL_GUID = "6884d5a71463b004e84c57d4f8a92829";
    // How far outside the object surface the center of the grasp parallelepiped is.
    // The visibility check of WristCameraMovement casts a line towards the center,
    // which must hit the parallelepiped before the object: with the center inside
    // the object, the line is blocked by the surrounding surface (e.g. the rim of a
    // cap) for most of the camera positions
    const float CENTER_OUTSIDE_SURFACE = 0.005f;
    // For top and bottom grasps, if the approach line reaches the object more than
    // this below its outermost part, the outermost part is used (e.g. a mug, where
    // the line would reach its bottom instead of its rim)
    const float MAX_DEPTH_TOP_GRASP = 0.01f;

    [Serializable]
    class GraspEntry
    {
        public string name;
        public string approach = "top";
        public float height = 0.5f;
        public float[] offset;
        public float[] size;
    }

    [Serializable]
    class GraspSpec
    {
        public string name;
        public string category;
        public float yaw;
        public string farthest_to;
        public GraspEntry[] grasps;
    }

    // Default parallelepiped size (x: along the palm width, y: across it, z: along
    // the approach direction), similar to the ones of the YCB prefabs
    static readonly Dictionary<string, Vector3> defaultSizes = new Dictionary<string, Vector3>()
    {
        {"tripod", new Vector3(0.015f, 0.015f, 0.02f)},
        {"tip_pinch", new Vector3(0.015f, 0.015f, 0.02f)},
        {"prismatic_2fingers", new Vector3(0.015f, 0.01f, 0.02f)},
        {"prismatic_4fingers", new Vector3(0.06f, 0.02f, 0.02f)},
        {"sphere_4fingers", new Vector3(0.04f, 0.04f, 0.03f)},
        {"power_sphere", new Vector3(0.05f, 0.05f, 0.03f)},
        {"precision_sphere", new Vector3(0.04f, 0.04f, 0.03f)},
        {"power_disk", new Vector3(0.03f, 0.03f, 0.02f)},
        {"large_diameter", new Vector3(0.06f, 0.02f, 0.03f)},
        {"medium_wrap", new Vector3(0.05f, 0.02f, 0.03f)},
        {"small_diameter", new Vector3(0.04f, 0.02f, 0.03f)},
        {"adducted_thumb", new Vector3(0.03f, 0.01f, 0.02f)},
    };

    // Where the camera comes from, in the frame of the upright object
    static readonly Dictionary<string, Vector3> approachDirections = new Dictionary<string, Vector3>()
    {
        {"top", Vector3.up},
        {"bottom", Vector3.down},
        {"left", Vector3.back},
        {"right", Vector3.forward},
        {"front", Vector3.left},
        {"back", Vector3.right},
    };

    [MenuItem("Tools/Grasp Objects/Build GSO Prefabs")]
    public static void BuildAll()
    {
        if (!Directory.Exists(MODELS_DIR))
        {
            EditorUtility.DisplayDialog("Build GSO Prefabs", "No object in " + MODELS_DIR +
                                        ": import them with python_scripts/GSO/import_gso.py", "OK");
            return;
        }
        AssetDatabase.Refresh();

        List<string> built = new List<string>();
        List<string> failed = new List<string>();
        string[] specPaths = Directory.GetDirectories(MODELS_DIR)
            .Select(dir => Path.Combine(dir, "grasp_spec.json").Replace('\\', '/'))
            .Where(File.Exists).ToArray();
        try
        {
            for (int i = 0; i < specPaths.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Build GSO Prefabs", specPaths[i], (float)i / specPaths.Length);
                try
                {
                    built.Add(Build(specPaths[i]));
                }
                catch (Exception e)
                {
                    failed.Add(specPaths[i] + ": " + e.Message);
                    Debug.LogException(e);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        Debug.Log("[GSO] Built " + built.Count + " prefabs in " + PREFABS_DIR +
                  (failed.Count > 0 ? ", failed: " + failed.Count + "\n" + string.Join("\n", failed) : ""));
    }

    /// <summary>
    /// Build the prefab of one object, given the path of its grasp_spec.json.
    /// Returns the prefab path
    /// </summary>
    public static string Build(string specPath)
    {
        GraspSpec spec = JsonUtility.FromJson<GraspSpec>(File.ReadAllText(specPath));
        string objectDir = Path.GetDirectoryName(specPath).Replace('\\', '/');
        string modelPath = objectDir + "/google_16k/textured.obj";
        UnityEngine.Object[] modelAssets = AssetDatabase.LoadAllAssetsAtPath(modelPath);
        Mesh mesh = modelAssets.OfType<Mesh>().FirstOrDefault();
        Material material = modelAssets.OfType<Material>().FirstOrDefault();
        if (mesh == null || material == null)
            throw new Exception("Mesh or material not found in " + modelPath);
        if (spec.grasps == null || spec.grasps.Length == 0)
            throw new Exception("No grasp in " + specPath);
        Material graspMaterial = AssetDatabase.LoadAssetAtPath<Material>(
            AssetDatabase.GUIDToAssetPath(GRASP_MATERIAL_GUID));

        // GSO models are z-up (as the YCB ones), the Unity frame is y-up
        Quaternion meshRotation = Quaternion.Euler(0f, spec.yaw, 0f) * Quaternion.Euler(-90f, 0f, 0f);
        Vector3[] vertices = mesh.vertices.Select(v => meshRotation * v).ToArray();
        if (!string.IsNullOrEmpty(spec.farthest_to))
        {
            // Rotate around the vertical axis such that the part sticking out the most
            // (e.g. the handle of a mug) faces the requested side
            Vector3 side;
            if (!approachDirections.TryGetValue(spec.farthest_to, out side) || side.y != 0f)
                throw new Exception("farthest_to must be one of left, right, front, back");
            Vector3 farthest = FarthestHorizontalDirection(vertices);
            Quaternion alignment = Quaternion.FromToRotation(farthest, side);
            meshRotation = alignment * meshRotation;
            vertices = vertices.Select(v => alignment * v).ToArray();
        }
        // Center the object bounding box on the prefab origin, as in the YCB prefabs
        Bounds bounds = new Bounds(vertices[0], Vector3.zero);
        foreach (Vector3 v in vertices)
            bounds.Encapsulate(v);
        Vector3 meshPosition = -bounds.center;
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] += meshPosition;
        bounds.center = Vector3.zero;

        // The objects are created in a preview scene, such that the open scene is not modified
        Scene previewScene = EditorSceneManager.NewPreviewScene();
        try
        {
            GameObject root = new GameObject(spec.name);
            SceneManager.MoveGameObjectToScene(root, previewScene);
            Labeling labeling = root.AddComponent<Labeling>();
            labeling.labels.Add(spec.name);
            GraspObjectInfo info = root.AddComponent<GraspObjectInfo>();
            info.category = spec.category;
            info.source = "GSO";
            info.sourceName = spec.name;

            GameObject textured = new GameObject("textured");
            textured.transform.SetParent(root.transform, false);
            textured.transform.localPosition = meshPosition;
            textured.transform.localRotation = meshRotation;
            textured.AddComponent<MeshFilter>().sharedMesh = mesh;
            textured.AddComponent<MeshRenderer>().sharedMaterial = material;
            textured.AddComponent<MeshCollider>().sharedMesh = mesh;

            foreach (GraspEntry grasp in spec.grasps)
            {
                AddGrasp(textured.transform, grasp, vertices, bounds, graspMaterial);
            }

            Directory.CreateDirectory(PREFABS_DIR);
            string prefabPath = PREFABS_DIR + "/" + spec.name + ".prefab";
            // Saving over an existing prefab keeps its GUID, hence the references to it
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            return prefabPath;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    static Vector3 FarthestHorizontalDirection(Vector3[] vertices)
    {
        // Measured from the vertex centroid, which (unlike the bounding box center) is
        // not moved much by a thin protruding part
        Vector3 centroid = Vector3.zero;
        foreach (Vector3 v in vertices)
            centroid += v;
        centroid /= vertices.Length;
        Vector3 farthest = Vector3.forward;
        float farthestDistance = 0f;
        foreach (Vector3 v in vertices)
        {
            Vector3 d = Vector3.ProjectOnPlane(v - centroid, Vector3.up);
            if (d.magnitude > farthestDistance)
            {
                farthestDistance = d.magnitude;
                farthest = d;
            }
        }
        return farthest.normalized;
    }

    static void AddGrasp(Transform textured, GraspEntry grasp, Vector3[] vertices, Bounds bounds, Material graspMaterial)
    {
        Vector3 outward;
        if (!approachDirections.TryGetValue(grasp.approach, out outward))
            throw new Exception("Grasp " + grasp.name + ": unknown approach " + grasp.approach);
        string graspType = GraspCatalog.ParseGraspName(grasp.name).graspType;
        if (!GraspCatalog.graspType2preshape.ContainsKey(graspType))
            throw new Exception("Grasp " + grasp.name + ": unknown grasp type " + graspType);

        // The parallelepiped z axis is the approach direction (towards the object),
        // its x axis is aligned with the palm width: vertical for side grasps (as in
        // the YCB prefabs), along the longest horizontal side of the object for top
        // and bottom grasps
        Vector3 forward = -outward;
        bool sideGrasp = outward.y == 0f;
        Vector3 right;
        if (sideGrasp)
            right = Vector3.down;
        else
            right = bounds.size.x >= bounds.size.z ? Vector3.right : Vector3.forward;
        Vector3 up = Vector3.Cross(forward, right);

        Vector3 size;
        if (grasp.size != null && grasp.size.Length == 3)
        {
            size = new Vector3(grasp.size[0], grasp.size[1], grasp.size[2]);
        }
        else
        {
            size = defaultSizes.ContainsKey(graspType) ? defaultSizes[graspType] : new Vector3(0.03f, 0.02f, 0.03f);
            // Not wider than the object
            size.x = Mathf.Min(size.x, 0.8f * Mathf.Abs(Vector3.Dot(bounds.size, right)));
            size.y = Mathf.Min(size.y, 0.8f * Mathf.Abs(Vector3.Dot(bounds.size, up)));
        }

        // The line along which the camera approaches: through the object center
        // for top and bottom grasps, at the requested height for side grasps
        Vector3 anchor = Vector3.zero;
        if (sideGrasp)
            anchor.y = bounds.min.y + Mathf.Clamp01(grasp.height) * bounds.size.y;
        if (grasp.offset != null && grasp.offset.Length == 3)
            anchor += new Vector3(grasp.offset[0], grasp.offset[1], grasp.offset[2]);

        // The surface reached by the camera: the outermost vertex within the
        // cross-section of the parallelepiped around that line
        float halfX = Mathf.Max(size.x / 2f, 0.005f);
        float halfY = Mathf.Max(size.y / 2f, 0.005f);
        float surface = float.NegativeInfinity;
        float outermost = float.NegativeInfinity;
        foreach (Vector3 v in vertices)
        {
            float distance = Vector3.Dot(v, outward);
            outermost = Mathf.Max(outermost, distance);
            Vector3 d = v - anchor;
            if (Mathf.Abs(Vector3.Dot(d, right)) <= halfX && Mathf.Abs(Vector3.Dot(d, up)) <= halfY)
                surface = Mathf.Max(surface, distance);
        }
        // Nothing there, or a hollow object seen from the top: use the outermost part
        // of the object instead
        if (float.IsNegativeInfinity(surface) || (!sideGrasp && outermost - surface > MAX_DEPTH_TOP_GRASP))
        {
            surface = outermost;
        }

        Vector3 anchorOnPlane = anchor - Vector3.Dot(anchor, outward) * outward;
        Vector3 center = anchorOnPlane + outward * (surface + CENTER_OUTSIDE_SURFACE);

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = grasp.name;
        // Positions and directions above are in the prefab root frame, which is
        // the parent of textured
        box.transform.SetParent(textured.parent, false);
        box.transform.localPosition = center;
        box.transform.localRotation = Quaternion.LookRotation(forward, up);
        box.transform.localScale = size;
        box.transform.SetParent(textured, true);
        Renderer renderer = box.GetComponent<Renderer>();
        renderer.sharedMaterial = graspMaterial;
        renderer.enabled = false;
        box.SetActive(false);
    }
}
