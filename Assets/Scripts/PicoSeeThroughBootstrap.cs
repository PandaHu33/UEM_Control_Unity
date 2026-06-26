using UnityEngine;
using Unity.XR.PXR;

public sealed class PicoSeeThroughBootstrap : MonoBehaviour
{
    [SerializeField]
    bool m_EnableOnStart = true;

    [SerializeField]
    bool m_TransparentCameraBackground = true;

    [SerializeField]
    Color m_ClearColor = new Color(0f, 0f, 0f, 0f);

    void Awake()
    {
        ConfigureCameras();

        if (m_EnableOnStart)
            SetSeeThrough(true);
    }

    void OnEnable()
    {
        if (m_EnableOnStart)
            SetSeeThrough(true);
    }

    void OnApplicationPause(bool pause)
    {
        if (!pause && m_EnableOnStart)
            SetSeeThrough(true);
    }

    void OnDestroy()
    {
        SetSeeThrough(false);
    }

    public void SetSeeThrough(bool enabled)
    {
        PXR_Manager.EnableVideoSeeThrough = enabled;
        Debug.Log($"PicoSeeThroughBootstrap EnableVideoSeeThrough={enabled}", this);
    }

    void ConfigureCameras()
    {
        if (!m_TransparentCameraBackground)
            return;

        var cameras = FindObjectsOfType<Camera>(true);
        foreach (var camera in cameras)
        {
            if (camera.stereoTargetEye == StereoTargetEyeMask.None)
                continue;

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = m_ClearColor;
        }
    }
}
