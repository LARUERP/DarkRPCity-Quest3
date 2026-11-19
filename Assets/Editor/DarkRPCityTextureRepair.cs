// Réparation des textures : MTL en secours, JSON _BaseMap en priorité.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DarkRPCityTextureRepair
{
    public static int LoadedTextures { get; private set; }
    public static int MissingTextures { get; private set; }

    [Serializable]
    private class MaterialMapRoot
    {
        public MaterialMap[] items;
    }

    [Serializable]
    private class MaterialMap
    {
        public int material_id;
        public string nom;
        public TextureMap[] textures;
    }

    [Serializable]
    private class TextureMap
    {
        public string propriete;
        public int texture_id;
        public string nom_texture;
    }

    public static void Repair()
    {
        LoadedTextures = 0;
        MissingTextures = 0;

        const string sourceRoot = "Assets/DarkRPCity_Source";
        const string textureRoot = "Assets/DarkRPCity_Source/DarkRPCity_TEXTURES_PNG";
        const string materialRoot = "Assets/DarkRPCity_Generated/Materials";

        if (!AssetDatabase.IsValidFolder(sourceRoot) ||
            !AssetDatabase.IsValidFolder(textureRoot) ||
            !AssetDatabase.IsValidFolder(materialRoot))
            throw new Exception("Dossiers source ou matériaux absents pour la réparation des textures.");

        var textureByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textureByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { textureRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);

            if (!textureByName.ContainsKey(fileName))
                textureByName.Add(fileName, path);

            if (!textureByStem.ContainsKey(stem))
                textureByStem.Add(stem, path);
        }

        if (textureByName.Count < 200)
            throw new Exception("Import Unity incomplet : seulement " +
                textureByName.Count + " textures trouvées.");

        foreach (string texturePath in textureByName.Values)
        {
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) continue;

            importer.maxTextureSize =
                Mathf.Min(Mathf.Max(importer.maxTextureSize, 1024), 2048);
            importer.textureCompression = TextureImporterCompression.Compressed;

            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings {
                name = "Android",
                overridden = true,
                maxTextureSize = importer.maxTextureSize,
                format = TextureImporterFormat.ASTC_6x6,
                textureCompression = TextureImporterCompression.Compressed
            });

            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // Secours : références diffuse du MTL.
        var diffuseByMaterial =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string mtlPath in Directory.GetFiles(
            sourceRoot, "*.mtl", SearchOption.AllDirectories))
        {
            string current = null;

            foreach (string raw in File.ReadLines(mtlPath))
            {
                string line = raw.Trim();

                if (line.StartsWith("newmtl ", StringComparison.OrdinalIgnoreCase))
                {
                    current = line.Substring(7).Trim();
                }
                else if (current != null &&
                    line.StartsWith("map_Kd ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(
                        (char[])null, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 2)
                        diffuseByMaterial[current] =
                            Path.GetFileName(parts[parts.Length - 1].Trim('"'));
                }
            }
        }

        // Source prioritaire : les textures _BaseMap du JSON.
        int jsonBaseMapEntries = 0;
        int jsonTextureFilesFound = 0;

        foreach (string jsonPath in Directory.GetFiles(
            "Assets", "DarkRPCity_MATERIAUX_TEXTURES.json",
            SearchOption.AllDirectories))
        {
            string raw = File.ReadAllText(jsonPath);
            MaterialMapRoot root =
                JsonUtility.FromJson<MaterialMapRoot>("{\"items\":" + raw + "}");

            if (root == null || root.items == null) continue;

            foreach (MaterialMap entry in root.items)
            {
                if (entry == null || entry.textures == null) continue;

                foreach (TextureMap texture in entry.textures)
                {
                    if (texture == null ||
                        !string.Equals(texture.propriete, "_BaseMap",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(texture.nom_texture))
                        continue;

                    jsonBaseMapEntries++;

                    string stem = texture.texture_id + "_" + texture.nom_texture;
                    string texturePath;

                    if (textureByStem.TryGetValue(stem, out texturePath))
                    {
                        diffuseByMaterial["MAT_" + entry.material_id] =
                            Path.GetFileName(texturePath);

                        if (!string.IsNullOrEmpty(entry.nom))
                            diffuseByMaterial[entry.nom] =
                                Path.GetFileName(texturePath);

                        jsonTextureFilesFound++;
                    }
                    else
                    {
                        // Conserve la référence pour que le diagnostic
                        // signale ensuite explicitement l'image manquante.
                        string expectedName = stem + ".png";
                        diffuseByMaterial["MAT_" + entry.material_id] = expectedName;

                        if (!string.IsNullOrEmpty(entry.nom))
                            diffuseByMaterial[entry.nom] = expectedName;
                    }
                }
            }
        }

        string[] materialGuids =
            AssetDatabase.FindAssets("t:Material", new[] { materialRoot });

        int matchedNames = 0;
        var unmatched = new List<string>();

        foreach (string guid in materialGuids)
        {
            string matPath = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) continue;

            string textureName;

            if (!TryResolveMaterialTexture(
                mat.name, matPath, diffuseByMaterial, out textureName))
            {
                if (unmatched.Count < 20)
                    unmatched.Add(mat.name + " [" +
                        Path.GetFileNameWithoutExtension(matPath) + "]");
                continue;
            }

            matchedNames++;

            string texturePath;
            if (!textureByName.TryGetValue(textureName, out texturePath))
            {
                MissingTextures++;
                continue;
            }

            Texture2D texture =
                AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);

            if (texture == null)
            {
                MissingTextures++;
                continue;
            }

            string property = mat.HasProperty("_BaseMap") ? "_BaseMap" :
                mat.HasProperty("_MainTex") ? "_MainTex" : null;

            if (property == null) continue;

            mat.SetTexture(property, texture);
            EditorUtility.SetDirty(mat);
            LoadedTextures++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        Debug.Log("DarkRPCity texture diagnostics: material assets=" +
            materialGuids.Length +
            ", aliases=" + diffuseByMaterial.Count +
            ", JSON BaseMap entries=" + jsonBaseMapEntries +
            ", JSON texture files found=" + jsonTextureFilesFound +
            ", names matched=" + matchedNames +
            ", textures linked=" + LoadedTextures +
            ", missing=" + MissingTextures +
            ", unmatched samples=" + string.Join(" | ", unmatched.ToArray()));

        if (LoadedTextures < 150)
            throw new Exception("Réparation textures refusée : " +
                LoadedTextures + " textures liées sur " +
                materialGuids.Length +
                " matériaux. Voir les diagnostics JSON/alias dans le log.");
    }

    private static bool TryResolveMaterialTexture(
        string materialName,
        string materialPath,
        Dictionary<string, string> diffuseByMaterial,
        out string textureName)
    {
        textureName = null;

        if (!string.IsNullOrEmpty(materialName) &&
            diffuseByMaterial.TryGetValue(materialName, out textureName))
            return true;

        string fileName = Path.GetFileNameWithoutExtension(materialPath);

        if (!string.IsNullOrEmpty(fileName) &&
            diffuseByMaterial.TryGetValue(fileName, out textureName))
            return true;

        string normalized = NormalizeMaterialAlias(materialName);

        if (!string.IsNullOrEmpty(normalized) &&
            diffuseByMaterial.TryGetValue(normalized, out textureName))
            return true;

        normalized = NormalizeMaterialAlias(fileName);

        if (!string.IsNullOrEmpty(normalized) &&
            diffuseByMaterial.TryGetValue(normalized, out textureName))
            return true;

        return false;
    }

    private static string NormalizeMaterialAlias(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        string result = value.Trim();
        int lastSpace = result.LastIndexOf(' ');

        if (lastSpace > 0 && lastSpace < result.Length - 1)
        {
            string suffix = result.Substring(lastSpace + 1);
            bool numeric = true;

            for (int i = 0; i < suffix.Length; i++)
            {
                if (!char.IsDigit(suffix[i]))
                    numeric = false;
            }

            if (numeric)
                result = result.Substring(0, lastSpace);
        }

        if (result.StartsWith("MAT_MAT_", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(4);

        return result;
    }

}
