using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

public sealed class WristPoseTcpSender : MonoBehaviour
{
    public enum PoseInputSource
    {
        XRHandWrist,
        RightController,
        Both,
    }

    [SerializeField]
    string m_Host = "127.0.0.1";

    [SerializeField]
    int m_Port = 5005;

    [SerializeField, Min(1f)]
    float m_SendHz = 60f;

    [SerializeField]
    bool m_SendLeftHand = false;

    [SerializeField]
    bool m_SendRightHand = true;

    [SerializeField]
    PoseInputSource m_InputSource = PoseInputSource.Both;

    [SerializeField]
    bool m_ControlEnabled;

    [SerializeField]
    bool m_LogConnection = true;

    static readonly List<XRHandSubsystem> s_HandSubsystems = new List<XRHandSubsystem>();

    XRHandSubsystem m_HandSubsystem;
    TcpClient m_Client;
    Stream m_Stream;
    float m_NextSendTime;
    float m_NextConnectAttemptTime;
    uint m_Sequence;
    readonly byte[] m_Newline = { (byte)'\n' };

    public string Host
    {
        get => m_Host;
        set => m_Host = value;
    }

    public int Port
    {
        get => m_Port;
        set => m_Port = value;
    }

    public bool ControlEnabled => m_ControlEnabled;

    public PoseInputSource InputSource
    {
        get => m_InputSource;
        set => m_InputSource = value;
    }

    public void SetControlEnabled(bool enabled)
    {
        if (m_ControlEnabled == enabled)
            return;

        m_ControlEnabled = enabled;

        if (!m_ControlEnabled && m_Stream != null)
        {
            if (m_SendLeftHand)
                SendLine(FormatUnavailable("left", "xr_hand_wrist", false));
            if (m_SendRightHand)
                SendLine(FormatUnavailable("right", "xr_hand_wrist", false));
        }

        if (m_LogConnection)
            Debug.Log($"WristPoseTcpSender control={(m_ControlEnabled ? 1 : 0)}", this);
    }

    void Update()
    {
        EnsureSubsystem();
        EnsureConnected();

        if (m_Stream == null || Time.unscaledTime < m_NextSendTime)
            return;

        m_NextSendTime = Time.unscaledTime + 1f / Mathf.Max(1f, m_SendHz);

        if (SendsHandWrist())
        {
            if (!m_ControlEnabled)
            {
                if (m_SendLeftHand)
                    SendLine(FormatUnavailable("left", "xr_hand_wrist", false));
                if (m_SendRightHand)
                    SendLine(FormatUnavailable("right", "xr_hand_wrist", false));
            }
            else if (m_HandSubsystem == null || !m_HandSubsystem.running)
            {
                SendLine(FormatUnavailable("right", "xr_hand_wrist", false));
            }
            else
            {
                if (m_SendLeftHand)
                    SendHand("left", m_HandSubsystem.leftHand);

                if (m_SendRightHand)
                    SendHand("right", m_HandSubsystem.rightHand);
            }
        }

        if (SendsRightController())
            SendRightController();
    }

    bool SendsHandWrist()
    {
        return m_InputSource == PoseInputSource.XRHandWrist || m_InputSource == PoseInputSource.Both;
    }

    bool SendsRightController()
    {
        return m_InputSource == PoseInputSource.RightController || m_InputSource == PoseInputSource.Both;
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

    void EnsureConnected()
    {
        if (m_Client != null && m_Client.Connected && m_Stream != null)
            return;

        CloseConnection();

        if (Time.unscaledTime < m_NextConnectAttemptTime)
            return;

        m_NextConnectAttemptTime = Time.unscaledTime + 1f;

        try
        {
            m_Client = new TcpClient();
            m_Client.NoDelay = true;
            m_Client.Connect(m_Host, m_Port);
            m_Stream = m_Client.GetStream();

            if (m_LogConnection)
                Debug.Log($"WristPoseTcpSender connected to {m_Host}:{m_Port}", this);
        }
        catch (Exception exception)
        {
            CloseConnection();
            if (m_LogConnection)
                Debug.LogWarning($"WristPoseTcpSender connect failed: {exception.Message}", this);
        }
    }

    void SendHand(string handedness, XRHand hand)
    {
        if (!hand.isTracked)
        {
            SendLine(FormatUnavailable(handedness, "xr_hand_wrist", false));
            return;
        }

        var wrist = hand.GetJoint(XRHandJointID.Wrist);
        if (!wrist.TryGetPose(out var pose))
        {
            SendLine(FormatUnavailable(handedness, "xr_hand_wrist", false));
            return;
        }

        SendPose(handedness, "xr_hand_wrist", true, pose.position, pose.rotation, false);
    }

    void SendRightController()
    {
        var deadman = IsLeftDeadmanPressed();
        var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!device.isValid)
        {
            SendLine(FormatUnavailable("right", "right_controller", deadman));
            return;
        }

        var tracked = true;
        if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked))
            tracked = isTracked;

        var hasPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position);
        var hasRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation);
        if (!tracked || !hasPosition || !hasRotation)
        {
            SendLine(FormatUnavailable("right", "right_controller", deadman));
            return;
        }

        SendPose("right", "right_controller", true, position, rotation, deadman);
    }

    bool IsLeftDeadmanPressed()
    {
        var device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        return device.isValid &&
               device.TryGetFeatureValue(CommonUsages.primaryButton, out bool pressed) &&
               pressed;
    }

    void SendPose(string handedness, string source, bool tracked, Vector3 position, Quaternion rotation, bool deadman)
    {
        var p = position;
        var q = rotation;
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1:F6},{2},{3},{4:F6},{5:F6},{6:F6},{7:F6},{8:F6},{9:F6},{10:F6},{11},{12}",
            m_Sequence++,
            Time.realtimeSinceStartupAsDouble,
            handedness,
            tracked ? 1 : 0,
            p.x,
            p.y,
            p.z,
            q.x,
            q.y,
            q.z,
            q.w,
            source,
            deadman ? 1 : 0);

        SendLine(line);
    }

    string FormatUnavailable(string handedness)
    {
        return FormatUnavailable(handedness, "xr_hand_wrist", false);
    }

    string FormatUnavailable(string handedness, string source, bool deadman)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1:F6},{2},0,0,0,0,0,0,0,1,{3},{4}",
            m_Sequence++,
            Time.realtimeSinceStartupAsDouble,
            handedness,
            source,
            deadman ? 1 : 0);
    }

    void SendLine(string line)
    {
        try
        {
            var bytes = Encoding.ASCII.GetBytes(line);
            m_Stream.Write(bytes, 0, bytes.Length);
            m_Stream.Write(m_Newline, 0, m_Newline.Length);
        }
        catch (Exception exception)
        {
            if (m_LogConnection)
                Debug.LogWarning($"WristPoseTcpSender send failed: {exception.Message}", this);
            CloseConnection();
        }
    }

    void OnDestroy()
    {
        CloseConnection();
    }

    void CloseConnection()
    {
        if (m_Stream != null)
        {
            m_Stream.Dispose();
            m_Stream = null;
        }

        if (m_Client != null)
        {
            m_Client.Close();
            m_Client = null;
        }
    }
}
