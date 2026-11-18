// Réparation déterministe des textures MTL avec alias issus du manifeste matériaux.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DarkRPCityTextureRepair
{
    public static int LoadedTextures { get; private set; }
    public static int MissingTextures { get; private set; }

    [Serializable] private class MaterialMapRoot { public MaterialMap[] items; }
    [Serializable] private class MaterialMap { public int material_id; public string nom; }

    public static void Repair()
    {
        LoadedTextures = 0;
        MissingTextures = 0;
        const string sourceRoot = "Assets/DarkRPCity_Source";
        const string materialRoot = "Assets/DarkRPCity_Generated/Materials";
        if (!AssetDatabase.IsValidFolder(sourceRoot) || !AssetDatabase.IsValidFolder(materialRoot))
            throw new Exception("Dossiers source ou matériaux absents pour la réparation des textures.");

        var textureByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { sourceRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string key = Path.GetFileName(path);
            if (!textureByName.ContainsKey(key)) textureByName.Add(key, path);
        }
        if (textureByName.Count < 200)
            throw new Exception("Import Unity incomplet : seulement " + textureByName.Count + " textures trouvées.");

        foreach (string texturePath in textureByName.Values)
        {
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) continue;
            importer.maxTextureSize = Mathf.Min(Mathf.Max(importer.maxTextureSize, 1024), 2048);
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings {
                name = "Android", overridden = true, maxTextureSize = importer.maxTextureSize,
                format = TextureImporterFormat.ASTC_6x6,
                textureCompression = TextureImporterCompression.Compressed
            });
            importer.SaveAndReimport();
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var diffuseByMaterial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mtlPath in Directory.GetFiles(sourceRoot, "*.mtl", SearchOption.AllDirectories))
        {
            string current = null;
            foreach (string raw in File.ReadLines(mtlPath))
            {
                string line = raw.Trim();
                if (line.StartsWith("newmtl ", StringComparison.OrdinalIgnoreCase))
                    current = line.Substring(7).Trim();
                else if (current != null && line.StartsWith("map_Kd ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        diffuseByMaterial[current] = Path.GetFileName(parts[parts.Length - 1].Trim('"'));
                }
            }
        }

        // Le MTL utilise MAT_<id>; le manifeste peut utiliser le nom sémantique.
        foreach (string jsonPath in Directory.GetFiles(sourceRoot, "DarkRPCity_MATERIAUX_TEXTURES.json", SearchOption.AllDirectories))
        {
            string raw = File.ReadAllText(jsonPath);
            MaterialMapRoot root = JsonUtility.FromJson<MaterialMapRoot>("{\"items\":" + raw + "}");
            if (root == null || root.items == null) continue;
            foreach (MaterialMap entry in root.items)
            {
                string textureName;
                if (diffuseByMaterial.TryGetValue("MAT_" + entry.material_id, out textureName) &&
                    !string.IsNullOrEmpty(entry.nom))
                    diffuseByMaterial[entry.nom] = textureName;
            }
        }

        string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { materialRoot });
        int matchedNames = 0;
        var unmatched = new List<string>();
        foreach (string guid in materialGuids)
        {
            string matPath = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) continue;

            string textureName;
            if (!diffuseByMaterial.TryGetValue(mat.name, out textureName))
            {
                string fileName = Path.GetFileNameWithoutExtension(matPath);
                string alias = fileName.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(4) : fileName;
                if (!diffuseByMaterial.TryGetValue(alias, out textureName) &&
                    !diffuseByMaterial.TryGetValue("MAT_" + alias, out textureName))
                {
                    if (unmatched.Count < 20) unmatched.Add(mat.name + " [" + fileName + "]");
                    continue;
                }
            }
            matchedNames++;

            string texturePath;
            if (!textureByName.TryGetValue(textureName, out texturePath)) { MissingTextures++; continue; }
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null) { MissingTextures++; continue; }

            string property = mat.HasProperty("_BaseMap") ? "_BaseMap" :
                mat.HasProperty("_MainTex") ? "_MainTex" : null;
            if (property == null) continue;
            mat.SetTexture(property, texture);
            EditorUtility.SetDirty(mat);
            LoadedTextures++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("DarkRPCity texture diagnostics: material assets=" + materialGuids.Length +
            ", aliases MTL=" + diffuseByMaterial.Count + ", names matched=" + matchedNames +
            ", textures linked=" + LoadedTextures + ", missing=" + MissingTextures +
            ", unmatched samples=" + string.Join(" | ", unmatched.ToArray()));

        if (LoadedTextures < 150)
            throw new Exception("Réparation textures refusée : " + LoadedTextures +
                " textures liées sur " + materialGuids.Length +
                " matériaux. Voir diagnostics aliases/noms dans le log.");
    }
}
