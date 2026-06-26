#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class WristPoseDisplayInstaller
{
    const string DisplayObjectName = "Wrist Pose Display";

    static WristPoseDisplayInstaller()
    {
        EditorApplication.delayCall += EnsureDisplayObject;
    }

    [MenuItem("Tools/Hand Tracking/Add Wrist Pose Display")]
    public static void EnsureDisplayObject()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (Object.FindObjectOfType<WristPoseDisplay>() != null)
            return;

        var displayObject = new GameObject(DisplayObjectName);
        displayObject.AddComponent<WristPoseDisplay>();
        Undo.RegisterCreatedObjectUndo(displayObject, "Add Wrist Pose Display");

        var activeScene = EditorSceneManager.GetActiveScene();
        if (activeScene.IsValid())
            EditorSceneManager.MarkSceneDirty(activeScene);

        Selection.activeGameObject = displayObject;
    }
}
#endif
