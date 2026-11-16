using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
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
    }
    [Serializable] public class MeshFile
    {
        public int mesh_id;
        public string fichier;
    }
    [Serializable] public class CityObject
    {
        public int gameobject_id;
        public string nom;
        public int mesh_id;
        public float[] matrix;
    }

    static string FindFile(string folder, string name)
    {
        return Directory.GetFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    public static void BuildFromCI()
    {
        string stage = Path.Combine(Directory.GetCurrentDirectory(), "DarkRPCity_Staging");
        string manifestPath = FindFile(stage, "DarkRPCity_RUNTIME_MANIFEST.json");
        if (manifestPath == null) throw new Exception("Manifest runtime absent.");

        Manifest data = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (data == null || data.objets == null || data.objets.Length == 0)
            throw new Exception("Manifest vide ou invalide.");

        string source = "Assets/DarkRPCity_Source";
        string generated = "Assets/DarkRPCity_Generated";
        if (!AssetDatabase.IsValidFolder(source)) AssetDatabase.CreateFolder("Assets", "DarkRPCity_Source");
        if (!AssetDatabase.IsValidFolder(generated)) AssetDatabase.CreateFolder("Assets", "DarkRPCity_Generated");

        // Import materials and textures beside the OBJ files.
        foreach (string f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".mtl" && ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;
            string dst = source + "/" + Path.GetFileName(f);
            if (!File.Exists(dst)) File.Copy(f, dst);
        }

        var needed = new HashSet<int>(data.objets.Select(x => x.mesh_id));
        var renderersById = new Dictionary<int, List<Transform>>();
        int imported = 0;

        foreach (var mf in data.fichiers_mesh)
        {
            if (!needed.Contains(mf.mesh_id)) continue;
            string src = FindFile(stage, mf.fichier);
            if (src == null) throw new Exception("OBJ absent : " + mf.fichier);

            string dst = source + "/" + mf.fichier;
            if (!File.Exists(dst)) File.Copy(src, dst);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
            if (prefab == null) throw new Exception("Unity n'a pas importé : " + mf.fichier);

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "SOURCE_" + mf.mesh_id;

            foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
            {
                Match m = Regex.Match(t.name, @"GO_(\d+)_");
                if (!m.Success) continue;
                int id = int.Parse(m.Groups[1].Value);
                if (!renderersById.ContainsKey(id)) renderersById[id] = new List<Transform>();
                if (!renderersById[id].Contains(t)) renderersById[id].Add(t);
            }
            imported++;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        GameObject world = new GameObject("DARKRPCITY_WORLD");
        GameObject sectorsRoot = new GameObject("SECTEURS_128M");
        sectorsRoot.transform.SetParent(world.transform, false);

        var sectors = new Dictionary<string, Transform>();
        int created = 0, missing = 0;
        var missingIds = new List<int>();

        foreach (CityObject o in data.objets)
        {
            if (o.matrix == null || o.matrix.Length != 16)
                throw new Exception("Matrice absente pour GO_" + o.gameobject_id);

            string key = "SECTEUR_" +
                Mathf.FloorToInt(o.matrix[3] / 128f) + "_" +
                Mathf.FloorToInt(o.matrix[11] / 128f);

            if (!sectors.ContainsKey(key))
            {
                GameObject s = new GameObject(key);
                s.transform.SetParent(sectorsRoot.transform, false);
                sectors[key] = s.transform;
            }

            GameObject holder = new GameObject("GO_" + o.gameobject_id + "_" + o.nom);
            holder.transform.SetParent(sectors[key], false);

            Matrix4x4 mat = new Matrix4x4();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    mat[r, c] = o.matrix[r * 4 + c];

            Vector3 pos = new Vector3(mat[0,3], mat[1,3], mat[2,3]);
            Vector3 right = new Vector3(mat[0,0], mat[1,0], mat[2,0]);
            Vector3 up = new Vector3(mat[0,1], mat[1,1], mat[2,1]);
            Vector3 forward = new Vector3(mat[0,2], mat[1,2], mat[2,2]);

            Vector3 scale = new Vector3(right.magnitude, up.magnitude, forward.magnitude);
            if (Vector3.Cross(right, up).Dot(forward) < 0) scale.x = -scale.x;

            holder.transform.localPosition = pos;
            holder.transform.localRotation = Quaternion.LookRotation(forward.normalized, up.normalized);
            holder.transform.localScale = scale;

            if (renderersById.TryGetValue(o.gameobject_id, out var parts))
            {
                foreach (Transform part in parts.ToArray())
                    if (part != null) part.SetParent(holder.transform, false);
                created++;
            }
            else
            {
                missing++;
                missingIds.Add(o.gameobject_id);
            }
        }

        string scenePath = generated + "/DarkRPCity.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.SaveAssets();

        string report = "Objets manifest : " + data.objets.Length +
            "\nObjets avec géométrie trouvée : " + created +
            "\nObjets sans géométrie importée : " + missing +
            "\nFichiers OBJ importés : " + imported +
            "\nSecteurs créés : " + sectors.Count +
            "\n\nAttention : les collisions, LOD et l'optimisation finale restent à réaliser.";

        File.WriteAllText(generated + "/BUILD_REPORT.txt", report +
            "\nIDs absents : " + string.Join(",", missingIds));

        AssetDatabase.Refresh();
        AssetImporter importer = AssetImporter.GetAtPath(scenePath);
        if (importer != null) importer.assetBundleName = "darkrpcity_scene";

        string bundlePath = "Assets/DarkRPCity_Build";
        if (!AssetDatabase.IsValidFolder(bundlePath))
            AssetDatabase.CreateFolder("Assets", "DarkRPCity_Build");

        var manifestBundle = BuildPipeline.BuildAssetBundles(
            bundlePath,
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.Android
        );

        if (manifestBundle == null) throw new Exception("Échec génération AssetBundle Android.");

        Debug.Log(report);
        Debug.Log("DARKRPCITY_SCENE_BUILD_OK");
    }
}
