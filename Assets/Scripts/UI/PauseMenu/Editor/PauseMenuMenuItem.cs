using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds "GameObject > UI > Retro Pause Menu" so the menu can be dropped into a
/// scene without hunting through Add Component. The runtime script builds its
/// own canvas, so the created object needs nothing else on it.
/// </summary>
public static class PauseMenuMenuItem
{
    [MenuItem("GameObject/UI/Retro Pause Menu", false, 12)]
    private static void CreatePauseMenu(MenuCommand command)
    {
        PauseMenu existing = Object.FindAnyObjectByType<PauseMenu>();
        if (existing != null)
        {
            Debug.LogWarning("[PauseMenu] This scene already has a pause menu — selecting it instead.", existing);
            Selection.activeObject = existing.gameObject;
            EditorGUIUtility.PingObject(existing.gameObject);
            return;
        }

        GameObject go = new GameObject("PauseMenu");
        go.AddComponent<PauseMenu>();

        GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Retro Pause Menu");

        Selection.activeObject = go;
        EditorGUIUtility.PingObject(go);
    }
}
