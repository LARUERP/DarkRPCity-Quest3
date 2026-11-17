using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class DarkRPCitySceneBuilder
{
    [Serializable] public class Manifest
    {
        public MeshFile[] fichiers_mesh;
        public CityObject[] objets;
        public MeshGroupCount[] group_counts;
    }

    [Serializable] public class MeshFile
    {
        public int mesh_id;
        public string fichier;
        public string asset_path;
    }

    [Serializable] public class MeshGroupCount
    {
        public int mesh_id;
        public int count;
    }

    [Serializable] public class MeshRange
    {
        public int debut;
        public int nombre;
    }

    [Serializable] public class CityObject
    {
        public int gameobject_id;
        public string nom;
        public int mesh_id;
        public float[] matrix;
        public MeshRange plage_mesh;
    }

    static readonly Regex GoRegex = new Regex(@"GO_(\d+)_", RegexOptions.Compiled);

    static int ReadGoId(MeshFilter filter)
    {
        string value = (filter.sharedMesh != null ? filter.sharedMesh.name : "") + " " +
                       filter.name + " " +
                       (filter.transform.parent != null ? filter.transform.parent.name : "");
        Match m = GoRegex.Match(value);
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    static MeshGroupCount GetGroupCount(Manifest data, int meshId)
    {
        return data.group_counts.FirstOrDefault(x => x.mesh_id == meshId);
    }

    static Transform CreateMatrixHolder(CityObject obj, Transform parent)
    {
        if (obj.matrix == null || obj.matrix.Length != 16)
            throw new Exception("Matrice absente pour GO_" + obj.gameobject_id);

        Matrix4x4 m = Matrix4x4.identity;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                m[r, c] = obj.matrix[r * 4 + c];

        Vector3 pos = new Vector3(m[0,3], m[1,3], m[2,3]);
        Vector3 right = new Vector3(m[0,0], m[1,0], m[2,0]);
        Vector3 up = new Vector3(m[0,1], m[1,1], m[2,1]);
        Vector3 forward = new Vector3(m[0,2], m[1,2], m[2,2]);

        GameObject holder = new GameObject("GO_" + obj.gameobject_id + "_" + obj.nom);
        holder.transform.SetParent(parent, false);
        holder.transform.localPosition = pos;
        holder.transform.localRotation =
            (forward.sqrMagnitude > 0.0001f && up.sqrMagnitude > 0.0001f)
            ? Quaternion.LookRotation(forward.normalized, up.normalized)
            : Quaternion.identity;

        Vector3 scale = new Vector3(right.magnitude, up.magnitude, forward.magnitude);
        if (scale.x < 0.0001f) scale.x = 1f;
        if (scale.y < 0.0001f) scale.y = 1f;
        if (scale.z < 0.0001f) scale.z = 1f;
        if (Vector3.Dot(Vector3.Cross(right, up), forward) < 0) scale.x = -scale.x;
        holder.transform.localScale = scale;
        return holder.transform;
    }

    public static void BuildFromCI()
    {
        string root = Directory.GetCurrentDirectory();
        string stage = Path.Combine(root, "DarkRPCity_Staging");
        string manifestPath = Path.Combine(stage, "DarkRPCity_RUNTIME_MANIFEST.json");
        if (!File.Exists(manifestPath))
            throw new Exception("Manifeste runtime absent : " + manifestPath);

        Manifest data = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (data == null || data.objets == null || data.objets.Length != 4193)
            throw new Exception("Le manifeste doit contenir exactement 4 193 objets.");
        if (data.fichiers_mesh == null || data.fichiers_mesh.Length != 30)
            throw new Exception("Les 30 fichiers OBJ ne sont pas tous déclarés.");

        string source = "Assets/DarkRPCity_Source";
        string generated = "Assets/DarkRPCity_Generated";
        if (!AssetDatabase.IsValidFolder(source)) AssetDatabase.CreateFolder("Assets", "DarkRPCity_Source");
        if (!AssetDatabase.IsValidFolder(generated)) AssetDatabase.CreateFolder("Assets", "DarkRPCity_Generated");

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var objectsById = data.objets.ToDictionary(x => x.gameobject_id, x => x);
        var rangesByMesh = new Dictionary<int, Dictionary<int, int>>();
        foreach (CityObject obj in data.objets)
        {
            if (obj.plage_mesh == null || obj.plage_mesh.nombre <= 0)
                throw new Exception("Plage de géométrie absente pour GO_" + obj.gameobject_id);

            if (!rangesByMesh.ContainsKey(obj.mesh_id))
                rangesByMesh[obj.mesh_id] = new Dictionary<int, int>();

            for (int i = obj.plage_mesh.debut; i < obj.plage_mesh.debut + obj.plage_mesh.nombre; i++)
                rangesByMesh[obj.mesh_id][i] = obj.gameobject_id;
        }

        var neededMeshIds = new HashSet<int>(data.objets.Select(x => x.mesh_id));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = new GameObject("DARKRPCITY_WORLD");
        var createdIds = new HashSet<int>();
        int importedFiles = 0;
        int mappedFilters = 0;
        int namedMappings = 0;
        int indexMappings = 0;
        int unmappedFilters = 0;

        foreach (MeshFile mf in data.fichiers_mesh.Where(x => neededMeshIds.Contains(x.mesh_id)))
        {
            if (string.IsNullOrEmpty(mf.asset_path))
                throw new Exception("asset_path absent pour le mesh " + mf.mesh_id);

            string assetPath = "Assets/DarkRPCity_Source/" + mf.asset_path.Replace("\\", "/");
            if (!File.Exists(assetPath))
                throw new Exception("OBJ copié introuvable : " + assetPath);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer != null)
            {
                importer.importMaterials = true;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.SaveAndReimport();
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                throw new Exception("Unity n'a pas importé le modèle : " + assetPath);

            GameObject instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
                throw new Exception("Impossible d'instancier : " + assetPath);
            instance.transform.SetParent(world.transform, false);

            MeshFilter[] filters = instance.GetComponentsInChildren<MeshFilter>(true)
                .Where(f => f.sharedMesh != null).ToArray();

            MeshGroupCount groupCount = GetGroupCount(data, mf.mesh_id);
            if (groupCount == null || groupCount.count <= 0)
                throw new Exception("Nombre de groupes OBJ absent pour mesh " + mf.mesh_id);

            Dictionary<int, int> rangeMap = rangesByMesh.ContainsKey(mf.mesh_id)
                ? rangesByMesh[mf.mesh_id]
                : new Dictionary<int, int>();

            int fileMapped = 0;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                int id = ReadGoId(filter);

                if (id >= 0 && objectsById.ContainsKey(id))
                    namedMappings++;
                else
                {
                    id = -1;
                    if (filters.Length == groupCount.count && rangeMap.TryGetValue(i, out int rangeId))
                    {
                        id = rangeId;
                        indexMappings++;
                    }
                }

                if (id < 0 || !objectsById.ContainsKey(id))
                {
                    unmappedFilters++;
                    continue;
                }

                CityObject obj = objectsById[id];
                Transform part = filter.transform;
                Vector3 meshCenter = part.TransformPoint(filter.sharedMesh.bounds.center);
                Vector3 translation = new Vector3(obj.matrix[3], obj.matrix[7], obj.matrix[11]);

                // Les exports de cette carte semblent déjà contenir des coordonnées monde.
                // On n'applique la matrice que si le centre du mesh est plus proche de l'origine
                // que de la translation Unity : cela évite une double translation.
                bool bakedWorldCoordinates =
                    (meshCenter - translation).sqrMagnitude < meshCenter.sqrMagnitude;

                if (bakedWorldCoordinates)
                    part.SetParent(world.transform, true);
                else
                {
                    Transform holder = CreateMatrixHolder(obj, world.transform);
                    part.SetParent(holder, false);
                }

                GameObjectUtility.SetStaticEditorFlags(
                    part.gameObject,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);

                Renderer renderer = part.GetComponent<Renderer>();
                if (renderer != null)
                    renderer.receiveShadows = true;

                createdIds.Add(id);
                fileMapped++;
                mappedFilters++;
            }

            Debug.Log("DarkRPCity mesh " + mf.mesh_id +
                      " : MeshFilters=" + filters.Length +
                      ", groupes OBJ=" + groupCount.count +
                      ", objets rattachés=" + fileMapped);

            importedFiles++;
        }

        if (createdIds.Count != data.objets.Length)
        {
            var missing = data.objets.Select(x => x.gameobject_id)
                .Where(id => !createdIds.Contains(id)).Take(40);
            throw new Exception(
                "RECONSTRUCTION REFUSÉE : " + createdIds.Count + "/4193 objets rattachés. " +
                "MeshFilters non associés=" + unmappedFilters +
                ". Premiers IDs absents : " + string.Join(",", missing));
        }

        GameObject lightObject = new GameObject("DARKRPCITY_SUN");
        Light sun = lightObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        lightObject.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        RenderSettings.ambientLight = new Color(0.48f, 0.48f, 0.48f);

        // Ajout facultatif du PlayerMarker si le package Marrow est présent dans le projet.
        Type markerType = Type.GetType("SLZ.Marrow.SceneStreaming.PlayerMarker, SLZ.Marrow");
        if (markerType != null && typeof(Component).IsAssignableFrom(markerType))
            world.AddComponent(markerType);

        string scenePath = generated + "/DarkRPCity.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetImporter sceneImporter = AssetImporter.GetAtPath(scenePath);
        if (sceneImporter == null)
            throw new Exception("Impossible de préparer le bundle de la scène.");
        sceneImporter.assetBundleName = "darkrpcity_scene";

        string bundlePath = "Assets/DarkRPCity_Build";
        if (!AssetDatabase.IsValidFolder(bundlePath))
            AssetDatabase.CreateFolder("Assets", "DarkRPCity_Build");

        AssetBundleManifest bundleManifest = BuildPipeline.BuildAssetBundles(
            bundlePath,
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.Android);

        if (bundleManifest == null || !File.Exists(bundlePath + "/darkrpcity_scene"))
            throw new Exception("Le bundle Android de la scène n'a pas été créé.");

        int textureCount = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories)
            .Count(p => new[] { ".png", ".jpg", ".jpeg", ".tga" }
            .Contains(Path.GetExtension(p).ToLowerInvariant()));

        string report =
            "DarkRPCity Quest 3 - rapport de reconstruction\n" +
            "Objets manifest : " + data.objets.Length + "\n" +
            "Objets avec géométrie rattachée : " + createdIds.Count + "\n" +
            "MeshFilters rattachés : " + mappedFilters + "\n" +
            "Correspondances par nom GO : " + namedMappings + "\n" +
            "Correspondances par plage OBJ : " + indexMappings + "\n" +
            "MeshFilters non associés : " + unmappedFilters + "\n" +
            "Fichiers OBJ principaux importés : " + importedFiles + "\n" +
            "Fichiers OBJ sources vérifiés : 30\n" +
            "Textures sources : " + textureCount + "\n" +
            "Groupes LOD du manifeste : " + (File.ReadAllText(manifestPath).Contains("\"lod_groups\"") ? "présents" : "non détectés") + "\n" +
            "Bundle Android : darkrpcity_scene\n" +
            "Note : un AssetBundle n'est pas à lui seul un Pallet/Level Crate BONELAB.";

        File.WriteAllText(generated + "/BUILD_REPORT.txt", report);
        Debug.Log(report);
        Debug.Log("DARKRPCITY_GEOMETRY_VALIDATED");
    }
}
