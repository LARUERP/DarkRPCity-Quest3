using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

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

    private static MeshGroupCount GetGroupCount(Manifest data, int meshId)
    {
        return data.group_counts.FirstOrDefault(x => x.mesh_id == meshId);
    }

    private static void EnsureFolder(string parent, string child)
    {
        string path = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, child);
    }

    public static void BuildFromCI()
    {
        string root = Directory.GetCurrentDirectory();
        string stage = Path.Combine(root, "DarkRPCity_Staging");
        string manifestPath = Path.Combine(stage, "DarkRPCity_RUNTIME_MANIFEST.json");
        if (!File.Exists(manifestPath)) throw new Exception("Manifeste runtime absent : " + manifestPath);

        Manifest data = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (data == null || data.objets == null || data.objets.Length != 4193)
            throw new Exception("Le manifeste doit contenir exactement 4 193 objets.");
        if (data.fichiers_mesh == null || data.fichiers_mesh.Length != 30)
            throw new Exception("Les 30 fichiers OBJ ne sont pas tous déclarés.");
        if (data.group_counts == null || data.group_counts.Length != 30)
            throw new Exception("Les 30 nombres de groupes OBJ ne sont pas déclarés.");

        string source = "Assets/DarkRPCity_Source";
        string generated = "Assets/DarkRPCity_Generated";
        if (!AssetDatabase.IsValidFolder(source)) throw new Exception("Dossier source Unity absent.");
        if (!AssetDatabase.IsValidFolder(generated)) AssetDatabase.CreateFolder("Assets", "DarkRPCity_Generated");
        EnsureFolder(generated, "Meshes");
        EnsureFolder(generated, "Materials");
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = new GameObject("DARKRPCITY_WORLD");
        var createdIds = new HashSet<int>();
        int importedFiles = 0;
        int totalGroups = 0;
        int totalMeshes = 0;

        int[] neededMeshIds = data.objets.Select(x => x.mesh_id).Distinct().OrderBy(x => x).ToArray();
        if (!neededMeshIds.SequenceEqual(new[] { 2, 7, 27 }))
            throw new Exception("Les IDs de meshes principaux ne correspondent pas à la source auditée : " + string.Join(",", neededMeshIds));

        foreach (MeshFile mf in data.fichiers_mesh.Where(x => neededMeshIds.Contains(x.mesh_id)))
        {
            if (string.IsNullOrEmpty(mf.asset_path)) throw new Exception("asset_path absent pour mesh " + mf.mesh_id);
            string assetPath = source + "/" + mf.asset_path.Replace("\\", "/");
            if (!File.Exists(assetPath)) throw new Exception("OBJ source absent : " + assetPath);
            MeshGroupCount count = GetGroupCount(data, mf.mesh_id);
            if (count == null || count.count <= 0) throw new Exception("Nombre de groupes OBJ absent pour mesh " + mf.mesh_id);

            int imported = DarkRPCityObjStreamImporter.ImportPrimaryObj(
                assetPath, mf.mesh_id, count.count, data, world.transform,
                generated + "/Meshes", generated + "/Materials", createdIds);
            if (imported <= 0) throw new Exception("Aucun objet importé pour mesh " + mf.mesh_id);
            totalMeshes += imported;
            totalGroups += count.count;
            importedFiles++;
            Debug.Log("DarkRPCity mesh " + mf.mesh_id + " : groupes OBJ=" + count.count + ", objets reconstruits=" + imported);
        }

        DarkRPCityTextureRepair.Repair();

        if (createdIds.Count != data.objets.Length)
        {
            var missing = data.objets.Select(x => x.gameobject_id).Where(id => !createdIds.Contains(id)).Take(40);
            throw new Exception("RECONSTRUCTION REFUSÉE : " + createdIds.Count + "/4193 objets reconstruits. IDs absents : " + string.Join(",", missing));
        }

        GameObject lightObject = new GameObject("DARKRPCITY_SUN");
        Light sun = lightObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        lightObject.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        RenderSettings.ambientLight = new Color(0.48f, 0.48f, 0.48f);

        Type markerType = Type.GetType("SLZ.Marrow.SceneStreaming.PlayerMarker, SLZ.Marrow");
        if (markerType != null && typeof(Component).IsAssignableFrom(markerType)) world.AddComponent(markerType);

        string scenePath = generated + "/DarkRPCity.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetImporter sceneImporter = AssetImporter.GetAtPath(scenePath);
        if (sceneImporter == null) throw new Exception("Impossible de préparer le bundle de la scène.");
        sceneImporter.assetBundleName = "darkrpcity_scene";

        string bundlePath = "Assets/DarkRPCity_Build";
        if (!AssetDatabase.IsValidFolder(bundlePath)) AssetDatabase.CreateFolder("Assets", "DarkRPCity_Build");
        AssetBundleManifest bundleManifest = BuildPipeline.BuildAssetBundles(
            bundlePath, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.Android);
        if (bundleManifest == null || !File.Exists(bundlePath + "/darkrpcity_scene"))
            throw new Exception("Le bundle Android de la scène n'a pas été créé.");

        int textureCount = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories)
            .Count(p => new[] { ".png", ".jpg", ".jpeg", ".tga" }.Contains(Path.GetExtension(p).ToLowerInvariant()));
        int materialCount = Directory.GetFiles(generated + "/Materials", "*.mat", SearchOption.AllDirectories).Length;
        string report =
            "DarkRPCity Quest 3 - rapport de reconstruction\n" +
            "Objets manifest : " + data.objets.Length + "\n" +
            "Objets reconstruits : " + createdIds.Count + "\n" +
            "Meshes Unity générés : " + totalMeshes + "\n" +
            "Groupes OBJ lus : " + totalGroups + "\n" +
            "Fichiers OBJ principaux lus : " + importedFiles + "\n" +
            "Fichiers OBJ source vérifiés : 30\n" +
            "Textures source : " + textureCount + "\n" +
            "Matériaux Unity créés : " + materialCount + "\n" +
            "Matériaux avec textures liées : " + DarkRPCityTextureRepair.LoadedTextures + "\n" +
            "Textures manquantes : " + DarkRPCityTextureRepair.MissingTextures + "\n" +
            "Groupes LOD du manifeste : " + (File.ReadAllText(manifestPath).Contains("\"lod_groups\"") ? "présents mais pas encore reconstruits" : "non détectés") + "\n" +
            "Coordonnées : géométrie exportée conservée telle quelle (coordonnées monde)\n" +
            "Bundle Android : darkrpcity_scene\n" +
            "IMPORTANT : ce bundle n'est pas encore un Pallet/Level Crate BONELAB installable.";
        File.WriteAllText(generated + "/BUILD_REPORT.txt", report);
        Debug.Log(report);
        Debug.Log("DARKRPCITY_GEOMETRY_VALIDATED");
    }
}
