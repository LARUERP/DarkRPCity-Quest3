using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class DarkRPCitySceneBuilder : EditorWindow
{
    [Serializable] class Manifest { public MeshFile[] fichiers_mesh; public Lod[] lod_groups; }
    [Serializable] class MeshFile { public string filename; }
    [Serializable] class Lod { public int gameobject_id; public string nom; public Level[] niveaux; }
    [Serializable] class Level { public float hauteur_ecran; public Item[] objets; }
    [Serializable] class Item { public int gameobject_id; }

    [MenuItem("DarkRPCity/Construire la ville")]
    static void Open() { GetWindow<DarkRPCitySceneBuilder>("DarkRPCity"); }

    void OnGUI()
    {
        GUILayout.Label("GENERATEUR DARKRPCITY", EditorStyles.boldLabel);
        if (GUILayout.Button("CONSTRUIRE LA SCENE", GUILayout.Height(45))) Build();
    }

    void Build()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string stage = Path.Combine(root, "DarkRPCity_Staging");
        string json = Path.Combine(stage, "DarkRPCity_UNITY_MANIFEST.json");
        if (!File.Exists(json)) { EditorUtility.DisplayDialog("Erreur", "Archive non extraite.", "OK"); return; }

        try
        {
            var data = JsonUtility.FromJson<Manifest>(File.ReadAllText(json));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            var world = new GameObject("DARKRPCITY_WORLD");
            var map = new Dictionary<int,List<Renderer>>();
            string folder = "Assets/DarkRPCity_Generated";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets","DarkRPCity_Generated");

            int models = 0;
            foreach (var f in data.fichiers_mesh)
            {
                string src = Path.Combine(stage,"DarkRPCity_MESHES_FUSIONNES",f.filename);
                if (!File.Exists(src)) continue;
                string dst = folder+"/"+f.filename;
                if (!File.Exists(dst)) File.Copy(src,dst);
                AssetDatabase.ImportAsset(dst,ImportAssetOptions.ForceSynchronousImport);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
                if (!prefab) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(world.transform,false);
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    var m = Regex.Match(r.gameObject.name,@"GO_(\d+)_");
                    if (!m.Success) continue;
                    int id = int.Parse(m.Groups[1].Value);
                    if (!map.ContainsKey(id)) map[id]=new List<Renderer>();
                    map[id].Add(r);
                }
                models++;
            }

            int count = 0;
            foreach (var g in data.lod_groups)
            {
                var levels = new List<LOD>();
                foreach (var l in g.niveaux)
                {
                    var rs = new List<Renderer>();
                    foreach (var item in l.objets)
                        if (map.ContainsKey(item.gameobject_id)) rs.AddRange(map[item.gameobject_id]);
                    levels.Add(new LOD(Mathf.Clamp01(l.hauteur_ecran),rs.Distinct().ToArray()));
                }
                if (levels.All(x=>x.renderers.Length==0)) continue;
                var holder = new GameObject("LOD_"+g.gameobject_id+"_"+g.nom);
                holder.transform.SetParent(world.transform,false);
                var lod = holder.AddComponent<LODGroup>();
                lod.SetLODs(levels.ToArray());
                lod.RecalculateBounds();
                count++;
            }

            EditorSceneManager.SaveScene(scene,"Assets/DarkRPCity_Generated/DarkRPCity.unity");
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Termine","Modeles importes : "+models+"\\nGroupes LOD : "+count,"OK");
        }
        catch(Exception e) { Debug.LogException(e); EditorUtility.DisplayDialog("Erreur",e.Message,"OK"); }
    }
}
