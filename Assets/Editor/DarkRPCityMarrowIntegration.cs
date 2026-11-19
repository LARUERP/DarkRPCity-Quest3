using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DarkRPCityMarrowIntegration
{
    public static void EnsurePlayerMarker(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            throw new Exception("La scène DarkRPCity n'est pas chargée.");

        Type markerType = Type.GetType(
            "SLZ.Marrow.SceneStreaming.PlayerMarker, SLZ.Marrow");

        if (markerType == null ||
            !typeof(Component).IsAssignableFrom(markerType))
        {
            Debug.LogError("Marrow SDK PlayerMarker introuvable.");
            throw new Exception("Le composant PlayerMarker du Marrow SDK est absent.");
        }

        GameObject marker = null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "Player Marker")
            {
                marker = root;
                break;
            }
        }

        if (marker == null)
        {
            marker = new GameObject("Player Marker");
            SceneManager.MoveGameObjectToScene(marker, scene);
            marker.transform.position = new Vector3(0f, 1f, 0f);
            marker.transform.rotation = Quaternion.identity;
        }

        if (marker.GetComponent(markerType) == null)
            marker.AddComponent(markerType);

        EditorUtility.SetDirty(marker);
        Debug.Log("DarkRPCity : Player Marker prêt en (0, 1, 0).");
    }
}
