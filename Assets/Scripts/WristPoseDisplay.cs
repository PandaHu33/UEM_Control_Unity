using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;

public sealed class WristPoseDisplay : MonoBehaviour
{
    [SerializeField]
    TextMesh m_Text;

    [SerializeField]
    Vector3 m_HeadRelativePosition = new Vector3(0f, -0.25f, 1.2f);

    [SerializeField]
    float m_TextSize = 0.035f;

    static readonly List<XRHandSubsystem> s_HandSubsystems = new List<XRHandSubsystem>();

    XRHandSubsystem m_HandSubsystem;

    void Awake()
    {
        if (m_Text == null)
            m_Text = CreateText();

        m_Text.fontSize = 64;
        m_Text.characterSize = m_TextSize;
        m_Text.anchor = TextAnchor.UpperLeft;
        m_Text.alignment = TextAlignment.Left;
        m_Text.color = Color.white;
    }

    void Update()
    {
        EnsureSubsystem();
        FollowCamera();

        if (m_HandSubsystem == null || !m_HandSubsystem.running)
        {
            m_Text.text = "Wrist Pose\nXRHandSubsystem not running";
            return;
        }

        m_Text.text =
            "Wrist Pose\n" +
            FormatHand("Left", m_HandSubsystem.leftHand) + "\n" +
            FormatHand("Right", m_HandSubsystem.rightHand);
    }

    void EnsureSubsystem()
    {
        if (m_HandSubsystem != null && m_HandSubsystem.running)
            return;

        s_HandSubsystems.Clear();
        SubsystemManager.GetSubsystems(s_HandSubsystems);

        for (var i = 0; i < s_HandSubsystems.Count; i++)
        {
            if (s_HandSubsystems[i].running)
            {
                m_HandSubsystem = s_HandSubsystems[i];
                return;
            }
        }

        m_HandSubsystem = s_HandSubsystems.Count > 0 ? s_HandSubsystems[0] : null;
    }

    void FollowCamera()
    {
        var cameraTransform = Camera.main != null ? Camera.main.transform : null;
        if (cameraTransform == null)
            return;

        transform.position =
            cameraTransform.position +
            cameraTransform.right * m_HeadRelativePosition.x +
            cameraTransform.up * m_HeadRelativePosition.y +
            cameraTransform.forward * m_HeadRelativePosition.z;

        transform.rotation = Quaternion.LookRotation(transform.position - cameraTransform.position, cameraTransform.up);
    }

    static string FormatHand(string label, XRHand hand)
    {
        if (!hand.isTracked)
            return label + ": not tracked";

        var wrist = hand.GetJoint(XRHandJointID.Wrist);
        if (!wrist.TryGetPose(out var pose))
            return label + ": wrist pose unavailable";

        var position = pose.position;
        var rotation = pose.rotation.eulerAngles;
        return string.Format(
            "{0}: Pos ({1:F3}, {2:F3}, {3:F3}) Rot ({4:F1}, {5:F1}, {6:F1})",
            label,
            position.x,
            position.y,
            position.z,
            rotation.x,
            rotation.y,
            rotation.z);
    }

    TextMesh CreateText()
    {
        var textObject = new GameObject("Wrist Pose Text");
        textObject.transform.SetParent(transform, false);
        return textObject.AddComponent<TextMesh>();
    }
}
