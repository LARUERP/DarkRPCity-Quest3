// Réparation déterministe des textures MTL après import Unity.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DarkRPCityTextureRepair
{
    public static int LoadedTextures { get; private set; }
    public static int MissingTextures { get; private set; }

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

        var mapByMaterial = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string mtlPath in Directory.GetFiles(sourceRoot, "*.mtl", SearchOption.AllDirectories))
        {
            string current = null;
            foreach (string raw in File.ReadLines(mtlPath))
            {
                string line = raw.Trim();
                if (line.StartsWith("newmtl ", StringComparison.Ordinal)) current = line.Substring(7).Trim();
                else if (current != null && line.StartsWith("map_Kd ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) mapByMaterial[current] = Path.GetFileName(parts[parts.Length - 1].Trim('"'));
                }
            }
        }

        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { materialRoot }))
        {
            string matPath = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) continue;
            string textureName;
            if (!mapByMaterial.TryGetValue(mat.name, out textureName)) continue;
            string texturePath;
            if (!textureByName.TryGetValue(textureName, out texturePath)) { MissingTextures++; continue; }
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null) { MissingTextures++; continue; }
            string property = mat.HasProperty("_BaseMap") ? "_BaseMap" : mat.HasProperty("_MainTex") ? "_MainTex" : null;
            if (property == null) continue;
            mat.SetTexture(property, texture);
            EditorUtility.SetDirty(mat);
            LoadedTextures++;


        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("DarkRPCity textures : " + LoadedTextures + " matériaux texturés, " + MissingTextures + " textures manquantes.");
        if (LoadedTextures < 150)
            throw new Exception("Réparation textures refusée : seulement " + LoadedTextures + " matériaux texturés.");
    }
}
