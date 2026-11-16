using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

public class DarkRPCityImporter : EditorWindow
{
    [MenuItem("DarkRPCity/Importateur de ressources")]
    public static void Open()
    {
        GetWindow<DarkRPCityImporter>("DarkRPCity");
    }

    void OnGUI()
    {
        GUILayout.Label("DARKRPCITY - PREPARATION DES RESSOURCES",
            EditorStyles.boldLabel);

        EditorGUILayout.Space();

        EditorGUILayout.HelpBox(
            "Cet outil extrait les ressources dans un dossier temporaire " +
            "du projet. Il ne construit pas encore la scène Unity.",
            MessageType.Info);

        if (GUILayout.Button("1 - Extraire l'archive ZIP", GUILayout.Height(40)))
            ExtractArchive();

        if (GUILayout.Button("2 - Vérifier les ressources", GUILayout.Height(40)))
            VerifyResources();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Dossier de destination :");
        EditorGUILayout.SelectableLabel(
            Path.Combine(ProjectRoot(), "DarkRPCity_Staging"),
            EditorStyles.textField,
            GUILayout.Height(20));
    }

    static string ProjectRoot()
    {
        return Directory.GetParent(Application.dataPath).FullName;
    }

    void ExtractArchive()
    {
        string zip = EditorUtility.OpenFilePanel(
            "Choisir DarkRPCity_RECONSTRUCTION.zip", "", "zip");

        if (string.IsNullOrEmpty(zip))
            return;

        string destination = Path.Combine(ProjectRoot(), "DarkRPCity_Staging");

        try
        {
            Directory.CreateDirectory(destination);

            int extracted = 0;

            using (FileStream fs = File.OpenRead(zip))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                string root = Path.GetFullPath(destination) +
                    Path.DirectorySeparatorChar;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (String.IsNullOrEmpty(entry.Name))
                        continue;

                    string target = Path.GetFullPath(
                        Path.Combine(destination,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

                    if (!target.StartsWith(root, StringComparison.Ordinal))
                        throw new Exception("Chemin interdit dans l'archive.");

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    entry.ExtractToFile(target, true);
                    extracted++;
                }
            }

            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Extraction terminée",
                extracted + " fichiers extraits dans DarkRPCity_Staging.",
                "OK");

            Debug.Log("DarkRPCity : extraction terminée. Fichiers : " + extracted);
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Erreur d'extraction", e.Message, "OK");
            Debug.LogException(e);
        }
    }

    void VerifyResources()
    {
        string folder = Path.Combine(ProjectRoot(), "DarkRPCity_Staging");

        if (!Directory.Exists(folder))
        {
            EditorUtility.DisplayDialog(
                "Ressources absentes",
                "Extrayez d'abord l'archive ZIP.",
                "OK");
            return;
        }

        string[] files = Directory.GetFiles(
            folder, "*", SearchOption.AllDirectories);

        int obj = files.Count(f => Path.GetExtension(f).Equals(
            ".obj", StringComparison.OrdinalIgnoreCase));

        int png = files.Count(f => Path.GetExtension(f).Equals(
            ".png", StringComparison.OrdinalIgnoreCase));

        int json = files.Count(f => Path.GetExtension(f).Equals(
            ".json", StringComparison.OrdinalIgnoreCase));

        bool manifest = files.Any(f =>
            Path.GetFileName(f).Equals(
                "DarkRPCity_UNITY_MANIFEST.json",
                StringComparison.OrdinalIgnoreCase));

        bool structure = files.Any(f =>
            Path.GetFileName(f).Equals(
                "DarkRPCity_STRUCTURE_RECONSTRUITE.json",
                StringComparison.OrdinalIgnoreCase));

        StringBuilder report = new StringBuilder();
        report.AppendLine("Fichiers trouvés : " + files.Length);
        report.AppendLine("Modèles OBJ : " + obj);
        report.AppendLine("Textures PNG : " + png);
        report.AppendLine("Fichiers JSON : " + json);
        report.AppendLine("Manifest Unity : " + (manifest ? "OUI" : "NON"));
        report.AppendLine("Structure reconstruite : " + (structure ? "OUI" : "NON"));

        File.WriteAllText(
            Path.Combine(folder, "RAPPORT_IMPORT.txt"),
            report.ToString());

        Debug.Log(report.ToString());

        EditorUtility.DisplayDialog(
            "Vérification des ressources",
            report.ToString(),
            "OK");
    }
}
