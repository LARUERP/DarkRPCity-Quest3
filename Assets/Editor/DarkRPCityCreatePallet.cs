using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SLZ.Marrow;
using SLZ.Marrow.Warehouse;
using SLZ.MarrowEditor;

public static class DarkRPCityCreatePallet
{
    private const string PalletTitle = "DarkRPCity";
    private const string PalletAuthor = "LARUERP";
    private const string LevelTitle = "DarkRPCity";
    private const string ScenePath =
        "Assets/DarkRPCity_Generated/DarkRPCity.unity";

    [MenuItem("DarkRPCity/Create and Pack BONELAB Quest Pallet")]
    public static void Create()
    {
        try
        {
            CreateAndPack();
        }
        catch (Exception e)
        {
            Debug.LogError("ÉCHEC DARKRPCITY : " + e);
            throw;
        }
    }

    public static void CreateAndPack()
    {
        if (!File.Exists(ScenePath))
            throw new Exception("Scène introuvable : " + ScenePath);

        SceneAsset sceneAsset =
            AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);

        if (sceneAsset == null)
            throw new Exception("Unity ne reconnaît pas la scène DarkRPCity.");

        string sceneGuid =
            AssetDatabase.AssetPathToGUID(ScenePath);

        if (string.IsNullOrEmpty(sceneGuid))
            throw new Exception("GUID de scène invalide.");

        var scene = EditorSceneManager.OpenScene(
            ScenePath, OpenSceneMode.Single);

        DarkRPCityMarrowIntegration.EnsurePlayerMarker(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        string root = MarrowSDK.GetMarrowAssetsPath("_Pallets");
        Directory.CreateDirectory(root);
        AssetDatabase.Refresh();

        Pallet pallet = AssetDatabase.FindAssets(
                "t:Pallet", new[] { root })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(path => AssetDatabase.LoadAssetAtPath<Pallet>(path))
            .FirstOrDefault(p => p != null &&
                p.Title == PalletTitle &&
                p.Author == PalletAuthor);

        if (pallet == null)
        {
            pallet = Pallet.CreatePallet(PalletTitle, PalletAuthor);

            string folder = MarrowSDK.GetMarrowAssetsPath(
                "_Pallets", pallet.Barcode.ID);

            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();

            string palletPath = Path.Combine(
                folder, pallet.GetAssetFilename());

            AssetDatabase.CreateAsset(pallet, palletPath);
            Debug.Log("Pallet créé : " + palletPath);
        }
        else
        {
            Debug.Log("Pallet existant réutilisé : " +
                AssetDatabase.GetAssetPath(pallet));
        }

        if (pallet.Crates == null)
            pallet.Crates = new List<Crate>();

        string palletFolder = Path.GetDirectoryName(
            AssetDatabase.GetAssetPath(pallet));

        LevelCrate crate = AssetDatabase.FindAssets(
                "t:LevelCrate", new[] { palletFolder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(path => AssetDatabase.LoadAssetAtPath<LevelCrate>(path))
            .FirstOrDefault(c => c != null &&
                c.Title == LevelTitle &&
                c.MainScene.AssetGUID == sceneGuid);

        if (crate == null)
        {
            crate = Crate.CreateCrate(
                typeof(LevelCrate),
                pallet,
                LevelTitle,
                new MarrowScene(sceneGuid)) as LevelCrate;

            if (crate == null)
                throw new Exception("Impossible de créer le Level Crate.");

            string cratePath = Path.Combine(
                palletFolder, crate.GetAssetFilename());

            AssetDatabase.CreateAsset(crate, cratePath);
            Debug.Log("Level Crate créé : " + cratePath);
        }
        else
        {
            Debug.Log("Level Crate existant réutilisé.");
        }

        if (!pallet.Crates.Contains(crate))
            pallet.Crates.Add(crate);

        EditorUtility.SetDirty(pallet);
        EditorUtility.SetDirty(crate);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("DARKRPCITY : PALLET ET LEVEL CRATE CRÉÉS.");

        // Vérifier la cible réelle avant de configurer la compression Android.
        Debug.LogError("DARKRPCITY ASTC DIAG : cible active = " +
            EditorUserBuildSettings.activeBuildTarget);

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            throw new Exception(
                "La cible Unity active n'est pas Android : " +
                EditorUserBuildSettings.activeBuildTarget);

        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

        Debug.LogError("DARKRPCITY ASTC DIAG : sous-cible Android après réglage = " +
            EditorUserBuildSettings.androidBuildSubtarget);

        if (EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
            throw new Exception(
                "Unity n'a pas conservé le réglage ASTC pour Android.");

        // Relever les règles Marrow sans masquer une éventuelle erreur.
        var issues = new List<MarrowProjectValidation.MarrowValidationRule>();
        MarrowProjectValidation.GetIssues(issues);

        Debug.LogError("DARKRPCITY MARROW DIAG : nombre de règles signalées = " +
            issues.Count);

        for (int i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            Debug.LogError("DARKRPCITY MARROW DIAG [" + i + "] : " +
                (issue == null ? "<null>" : issue.GetType().FullName + " | " + issue));
        }

        if (issues.Count > 0)
        {
            Debug.Log("Marrow SDK : tentative de correction de " +
                issues.Count + " problème(s).");

            try
            {
                MarrowProjectValidation.FixIssues(issues);
            }
            catch (System.Reflection.TargetParameterCountException e)
            {
                Debug.LogError(
                    "DARKRPCITY : le correcteur automatique Marrow a échoué " +
                    "(TargetParameterCountException). Nouvelle validation stricte. " + e);
            }
        }

        if (!MarrowProjectValidation.ValidateProject())
            throw new Exception(
                "Le projet ne passe pas la validation Marrow SDK après contrôle ASTC.");

        Debug.Log("DARKRPCITY : validation Marrow réussie.");

        // Sauvegarder les réglages Addressables existants avant le packer.
        var backup = new AddressablesBackup();

        try
        {
            AssetDatabase.SaveAssets();

            Debug.Log("DARKRPCITY : démarrage du packer Marrow BONELAB.");

            bool started = PalletPackerEditor.PackPallet(
                pallet, out var result, false, false);

            if (!started)
                throw new Exception("Le packer Marrow n'a pas démarré.");

            if (result == null)
                throw new Exception("Le packer n'a retourné aucun résultat.");

            if (!string.IsNullOrEmpty(result.Error))
                throw new Exception("Erreur du packer : " + result.Error);

            string builtPath = Path.Combine(
                "BuiltPallets",
                EditorUserBuildSettings.activeBuildTarget.ToString(),
                pallet.Barcode.ID
            );

            if (!Directory.Exists(builtPath))
                throw new Exception("Dossier Pallet compilé introuvable : " + builtPath);

            string[] builtFiles = Directory.GetFiles(
                builtPath, "*", SearchOption.AllDirectories
            );

            if (builtFiles.Length == 0)
                throw new Exception("Le dossier Pallet compilé est vide : " + builtPath);

            Debug.Log("DARKRPCITY_OUTPUT_VERIFIED : " +
                      builtPath + " (" + builtFiles.Length + " fichier(s)).");

            Debug.Log("DARKRPCITY_PACKAGING_SUCCESS : résultat sans erreur.");
        }
        finally
        {
            backup.Restore();
        }

        Debug.Log(
            "DARKRPCITY : PALLET, LEVEL CRATE ET PACKAGING TERMINÉS.");
    }

    private sealed class AddressablesBackup
    {
        private const string DataPath =
            "Assets/AddressableAssetsData";

        private const string SettingsPath =
            "ProjectSettings/AddressableAssetSettings.asset";

        private readonly string backupRoot;
        private readonly bool hadData;
        private readonly bool hadSettings;

        public AddressablesBackup()
        {
            backupRoot = Path.Combine(
                Path.GetTempPath(),
                "DarkRPCity_AddressablesBackup_" + Guid.NewGuid());

            hadData = Directory.Exists(DataPath);
            hadSettings = File.Exists(SettingsPath);

            Directory.CreateDirectory(backupRoot);

            if (hadData)
                FileUtil.CopyFileOrDirectory(
                    DataPath, Path.Combine(backupRoot, "AddressableAssetSettings"));

            if (hadSettings)
                File.Copy(
                    SettingsPath,
                    Path.Combine(backupRoot, "AddressableAssetSettings.asset"));
        }

        public void Restore()
        {
            try
            {
                if (hadData)
                {
                    if (Directory.Exists(DataPath))
                        Directory.Delete(DataPath, true);

                    FileUtil.CopyFileOrDirectory(
                        Path.Combine(backupRoot, "AddressableAssetSettings"),
                        DataPath);
                }
                else if (Directory.Exists(DataPath))
                {
                    Directory.Delete(DataPath, true);
                }

                if (hadSettings)
                {
                    File.Copy(
                        Path.Combine(backupRoot, "AddressableAssetSettings.asset"),
                        SettingsPath, true);
                }
                else if (File.Exists(SettingsPath))
                {
                    File.Delete(SettingsPath);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                Debug.Log("Réglages Addressables préexistants restaurés.");
            }
            finally
            {
                if (Directory.Exists(backupRoot))
                    Directory.Delete(backupRoot, true);
            }
        }
    }
}
