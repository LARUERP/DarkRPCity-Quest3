// Importateur OBJ par groupes pour DarkRPCity - Unity 2021.3.16f1.
// Lit les index OBJ globaux et reconstruit un Mesh Unity par GameObject du manifeste.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class DarkRPCityObjStreamImporter
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    private static readonly Regex GoPattern = new Regex(@"^GO_(\d+)_", RegexOptions.Compiled);

    private struct VertexKey : IEquatable<VertexKey>
    {
        public int v, vt, vn, group;
        public VertexKey(int v, int vt, int vn, int group) { this.v = v; this.vt = vt; this.vn = vn; this.group = group; }
        public bool Equals(VertexKey x) { return v == x.v && vt == x.vt && vn == x.vn && group == x.group; }
        public override bool Equals(object x) { return x is VertexKey && Equals((VertexKey)x); }
        public override int GetHashCode() { unchecked { return (((v * 397) ^ vt) * 397 ^ vn) * 397 ^ group; } }
    }

    private sealed class Group
    {
        public int goId, ordinal, baseV, baseVT, baseVN;
        public string material = "";
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<Vector3> normals = new List<Vector3>();
    }

    private sealed class MeshData
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly Dictionary<VertexKey, int> lookup = new Dictionary<VertexKey, int>();
        public readonly List<string> materialNames = new List<string>();
        public readonly Dictionary<string, int> materialSlots = new Dictionary<string, int>();
        public readonly List<List<int>> triangles = new List<List<int>>();
        public bool missingNormals;

        public int MaterialSlot(string name)
        {
            name = name ?? "";
            int slot;
            if (materialSlots.TryGetValue(name, out slot)) return slot;
            slot = materialNames.Count;
            materialSlots.Add(name, slot);
            materialNames.Add(name);
            triangles.Add(new List<int>());
            return slot;
        }
    }

    private sealed class MtlData
    {
        public Color color = Color.white;
        public float alpha = 1f;
        public string diffuseMap;
    }

    // Retourne le nombre d'objets reconstruits et ajoute leurs IDs dans createdIds.
    public static int ImportPrimaryObj(string objAssetPath, int meshId, int groupCount,
        DarkRPCitySceneBuilder.Manifest manifest, Transform parent, string generatedFolder, string materialsFolder,
        ISet<int> createdIds)
    {
        string objFullPath = Path.GetFullPath(objAssetPath);
        if (!File.Exists(objFullPath)) throw new FileNotFoundException("OBJ absent", objFullPath);
        if (manifest == null || manifest.objets == null) throw new Exception("Manifeste absent.");
        if (!AssetDatabase.IsValidFolder(generatedFolder)) throw new Exception("Dossier Unity absent : " + generatedFolder);

        var objects = manifest.objets.Where(x => x.mesh_id == meshId).ToDictionary(x => x.gameobject_id);
        int[] groupToGo = Enumerable.Repeat(-1, groupCount).ToArray();
        foreach (var obj in objects.Values)
        {
            if (obj.plage_mesh == null || obj.plage_mesh.debut < 0 || obj.plage_mesh.nombre <= 0 ||
                obj.plage_mesh.debut + obj.plage_mesh.nombre > groupCount)
                throw new Exception("Plage OBJ invalide pour GO_" + obj.gameobject_id);
            for (int i = obj.plage_mesh.debut; i < obj.plage_mesh.debut + obj.plage_mesh.nombre; i++)
            {
                if (groupToGo[i] != -1) throw new Exception("Plages OBJ chevauchantes : groupe " + i);
                groupToGo[i] = obj.gameobject_id;
            }
        }
        if (groupToGo.Any(x => x < 0)) throw new Exception("Au moins un groupe OBJ n'est pas relié au manifeste (mesh " + meshId + ").");

        Dictionary<string, MtlData> mtl = LoadMaterialLibraries(objFullPath);
        var completedIds = new HashSet<int>();
        var materialAssets = new Dictionary<string, Material>();
        Group group = null;
        MeshData currentMesh = null;
        int currentGo = -1;
        int globalV = 0, globalVT = 0, globalVN = 0, groupIndex = -1, saved = 0;
        string line;

        using (var reader = new StreamReader(objFullPath, true))
        {
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("o ", StringComparison.Ordinal))
                {
                    FinishGroup(group);
                    groupIndex++;
                    if (groupIndex >= groupCount) throw new Exception("Trop de groupes OBJ dans mesh " + meshId + ".");
                    string groupName = line.Substring(2).Trim();
                    Match match = GoPattern.Match(groupName);
                    if (!match.Success || int.Parse(match.Groups[1].Value, CI) != groupToGo[groupIndex])
                        throw new Exception("Groupe inattendu " + groupIndex + " : " + groupName);
                    int nextGo = groupToGo[groupIndex];
                    if (currentGo != -1 && nextGo != currentGo)
                    {
                        SaveObject(currentGo, currentMesh, objects, parent, generatedFolder, materialsFolder, mtl, materialAssets, createdIds);
                        completedIds.Add(currentGo);
                        saved++;
                        currentMesh = null;
                    }
                    if (completedIds.Contains(nextGo))
                        throw new Exception("Les groupes du même GameObject ne sont pas contigus : GO_" + nextGo);
                    currentGo = nextGo;
                    if (currentMesh == null) currentMesh = new MeshData();
                    group = new Group { goId = nextGo, ordinal = groupIndex, baseV = globalV, baseVT = globalVT, baseVN = globalVN };
                    continue;
                }
                if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length < 4) throw new Exception("Sommet OBJ invalide.");
                    if (group != null) group.vertices.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
                    globalV++;
                }
                else if (line.StartsWith("vt ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length < 3) throw new Exception("UV OBJ invalide.");
                    if (group != null) group.uvs.Add(new Vector2(F(p[1]), F(p[2])));
                    globalVT++;
                }
                else if (line.StartsWith("vn ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length < 4) throw new Exception("Normale OBJ invalide.");
                    if (group != null) group.normals.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
                    globalVN++;
                }
                else if (line.StartsWith("usemtl ", StringComparison.Ordinal) && group != null)
                    group.material = line.Substring(7).Trim();
                else if (line.StartsWith("f ", StringComparison.Ordinal) && group != null)
                {
                    string[] p = Split(line);
                    if (p.Length < 4) continue;
                    int slot = currentMesh.MaterialSlot(group.material);
                    int first = AddVertex(ParseVertex(p[1], group, globalV, globalVT, globalVN), group, currentMesh);
                    for (int i = 2; i < p.Length - 1; i++)
                    {
                        currentMesh.triangles[slot].Add(first);
                        currentMesh.triangles[slot].Add(AddVertex(ParseVertex(p[i], group, globalV, globalVT, globalVN), group, currentMesh));
                        currentMesh.triangles[slot].Add(AddVertex(ParseVertex(p[i + 1], group, globalV, globalVT, globalVN), group, currentMesh));
                    }
                }
            }
        }
        FinishGroup(group);
        if (groupIndex + 1 != groupCount) throw new Exception("Groupes lus : " + (groupIndex + 1) + "/" + groupCount + " pour mesh " + meshId);
        if (currentGo != -1 && currentMesh != null)
        {
            SaveObject(currentGo, currentMesh, objects, parent, generatedFolder, materialsFolder, mtl, materialAssets, createdIds);
            completedIds.Add(currentGo); saved++;
        }
        if (saved != objects.Count || completedIds.Count != objects.Count)
            throw new Exception("Objets reconstruits pour mesh " + meshId + " : " + completedIds.Count + "/" + objects.Count);
        return saved;
    }

    private static void FinishGroup(Group group)
    {
        if (group == null) return;
        // Les index OBJ sont globaux, mais les déclarations v/vt/vn doivent appartenir au groupe courant.
        // Le contrôle précis est effectué à la résolution des faces.
    }

    private static VertexKey ParseVertex(string token, Group g, int totalV, int totalVT, int totalVN)
    {
        string[] a = token.Split('/');
        int v = Resolve(a[0], totalV) - g.baseV - 1;
        int vt = a.Length > 1 && a[1].Length > 0 ? Resolve(a[1], totalVT) - g.baseVT - 1 : -1;
        int vn = a.Length > 2 && a[2].Length > 0 ? Resolve(a[2], totalVN) - g.baseVN - 1 : -1;
        return new VertexKey(v, vt, vn, g.ordinal);
    }

    private static int AddVertex(VertexKey key, Group g, MeshData mesh)
    {
        int index;
        if (mesh.lookup.TryGetValue(key, out index)) return index;
        if (key.v < 0 || key.v >= g.vertices.Count) throw new Exception("Index vertex hors groupe " + g.ordinal + " (" + key.v + "/" + g.vertices.Count + ").");
        index = mesh.vertices.Count;
        mesh.lookup.Add(key, index);
        mesh.vertices.Add(g.vertices[key.v]);
        mesh.uvs.Add(key.vt >= 0 && key.vt < g.uvs.Count ? g.uvs[key.vt] : Vector2.zero);
        if (key.vn >= 0 && key.vn < g.normals.Count) mesh.normals.Add(g.normals[key.vn]);
        else { mesh.normals.Add(Vector3.zero); mesh.missingNormals = true; }
        return index;
    }

    private static void SaveObject(int id, MeshData data, Dictionary<int, DarkRPCitySceneBuilder.CityObject> objects,
        Transform parent, string folder, string materialsFolder, Dictionary<string, MtlData> mtl, Dictionary<string, Material> materialAssets, ISet<int> createdIds)
    {
        if (data == null || data.vertices.Count == 0) throw new Exception("Aucune géométrie pour GO_" + id);
        if (createdIds.Contains(id)) throw new Exception("GO_" + id + " importé deux fois.");
        var obj = objects[id];
        Mesh mesh = new Mesh { name = "GO_" + id + "_" + SafeName(obj.nom) };
        mesh.indexFormat = data.vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(data.vertices);
        if (data.uvs.Count == data.vertices.Count) mesh.SetUVs(0, data.uvs);
        if (!data.missingNormals && data.normals.Count == data.vertices.Count) mesh.SetNormals(data.normals);
        mesh.subMeshCount = data.triangles.Count;
        for (int i = 0; i < data.triangles.Count; i++) mesh.SetTriangles(data.triangles[i], i, false);
        if (data.missingNormals) mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder.TrimEnd('/') + "/GO_" + id + ".asset");
        AssetDatabase.CreateAsset(mesh, assetPath);

        GameObject go = new GameObject("GO_" + id + "_" + obj.nom);
        go.transform.SetParent(parent, false);
        var filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = ResolveMaterials(data.materialNames, mtl, materialsFolder, materialAssets);
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
        renderer.receiveShadows = true;
        createdIds.Add(id);
    }

    private static Material[] ResolveMaterials(List<string> names, Dictionary<string, MtlData> libraries, string folder, Dictionary<string, Material> cache)
    {
        var result = new Material[names.Count];
        Shader shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Diffuse");
        if (shader == null) throw new Exception("Aucun shader compatible trouvé pour les matériaux OBJ.");
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i] ?? "";
            Material mat;
            if (!cache.TryGetValue(name, out mat))
            {
                MtlData info;
                libraries.TryGetValue(name, out info);
                mat = new Material(shader) { name = string.IsNullOrEmpty(name) ? "DarkRPCity_Default" : name };
                if (info != null)
                {
                    mat.color = new Color(info.color.r, info.color.g, info.color.b, info.alpha);
                    if (!string.IsNullOrEmpty(info.diffuseMap))
                    {
                        string texturePath = ToAssetPath(info.diffuseMap);
                        Texture2D tex = string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                        if (tex != null)
                        {
                            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                            else if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                        }
                    }
                }
                string safe = Regex.Replace(string.IsNullOrEmpty(name) ? "Default" : name, "[^A-Za-z0-9_-]", "_");
                string path = AssetDatabase.GenerateUniqueAssetPath(folder.TrimEnd('/') + "/MAT_" + safe + ".mat");
                AssetDatabase.CreateAsset(mat, path);
                cache.Add(name, mat);
            }
            result[i] = mat;
        }
        return result;
    }

    private static Dictionary<string, MtlData> LoadMaterialLibraries(string objPath)
    {
        var result = new Dictionary<string, MtlData>();
        string sourceRoot = Path.GetFullPath("Assets/DarkRPCity_Source");
        foreach (string file in Directory.GetFiles(sourceRoot, "*.mtl", SearchOption.AllDirectories))
        {
            MtlData current = null;
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("newmtl ", StringComparison.Ordinal))
                {
                    string name = line.Substring(7).Trim();
                    current = new MtlData(); result[name] = current;
                }
                else if (current != null && line.StartsWith("Kd ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length >= 4) current.color = new Color(F(p[1]), F(p[2]), F(p[3]), 1f);
                }
                else if (current != null && line.StartsWith("d ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length >= 2) current.alpha = Mathf.Clamp01(F(p[1]));
                }
                else if (current != null && line.StartsWith("Tr ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length >= 2) current.alpha = 1f - Mathf.Clamp01(F(p[1]));
                }
                else if (current != null && line.StartsWith("map_Kd ", StringComparison.Ordinal))
                {
                    string[] p = Split(line); if (p.Length >= 2)
                    {
                        string rel = p[p.Length - 1].Trim('"');
                        current.diffuseMap = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), rel)).Replace('\\', '/');
                    }
                }
            }
        }
        return result;
    }

    private static string ToAssetPath(string fullPath)
    {
        string root = Path.GetFullPath("Assets").Replace('\\', '/').TrimEnd('/') + "/";
        string value = Path.GetFullPath(fullPath).Replace('\\', '/');
        if (!value.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
        return value.Substring(root.Length - "Assets/".Length);
    }

    private static int Resolve(string value, int count)
    {
        int n = int.Parse(value, CI);
        return n < 0 ? count + n + 1 : n;
    }
    private static string[] Split(string line) { return line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); }
    private static float F(string value) { return float.Parse(value, CI); }
    private static string SafeName(string value) { return string.IsNullOrEmpty(value) ? "Object" : Regex.Replace(value, "[^A-Za-z0-9_-]", "_"); }
}
