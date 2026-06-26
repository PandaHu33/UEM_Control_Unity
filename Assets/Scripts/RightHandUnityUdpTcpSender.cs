using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Hands;

[DisallowMultipleComponent]
public sealed class RightHandUnityUdpTcpSender : MonoBehaviour
{
    [SerializeField] string m_Host = "127.0.0.1";
    [SerializeField] int m_Port = 5006;
    [SerializeField, Min(1f)] float m_SendHz = 60f;
    [SerializeField] bool m_AutoSend = true;
    [SerializeField] bool m_SendLeftHandZeros = true;
    [SerializeField] bool m_LogConnection = true;
    [SerializeField, Min(0.5f)] float m_StatusLogIntervalSeconds = 5f;

    static readonly XRHandJointID[] s_UnityToTwentyOne =
    {
        XRHandJointID.Wrist,
        XRHandJointID.ThumbMetacarpal,
        XRHandJointID.ThumbProximal,
        XRHandJointID.ThumbDistal,
        XRHandJointID.ThumbTip,
        XRHandJointID.IndexMetacarpal,
        XRHandJointID.IndexProximal,
        XRHandJointID.IndexIntermediate,
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleMetacarpal,
        XRHandJointID.MiddleProximal,
        XRHandJointID.MiddleIntermediate,
        XRHandJointID.MiddleTip,
        XRHandJointID.RingMetacarpal,
        XRHandJointID.RingProximal,
        XRHandJointID.RingIntermediate,
        XRHandJointID.RingTip,
        XRHandJointID.LittleMetacarpal,
        XRHandJointID.LittleProximal,
        XRHandJointID.LittleIntermediate,
        XRHandJointID.LittleTip,
    };

    static readonly List<XRHandSubsystem> s_HandSubsystems = new List<XRHandSubsystem>();
    static readonly byte[] s_Newline = { (byte)'\n' };

    readonly float[] m_RightPositions = new float[21 * 3];
    readonly float[] m_RightRotations = new float[21 * 3];
    readonly float[] m_LeftZeros = new float[21 * 3];
    readonly StringBuilder m_Builder = new StringBuilder(4096);

    XRHandSubsystem m_HandSubsystem;
    TcpClient m_Client;
    Stream m_Stream;
    float m_NextSendTime;
    float m_NextConnectAttemptTime;
    uint m_FrameId;
    string m_LastStatus;
    DateTime m_NextStatusLogTimeUtc;

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

    public bool AutoSend
    {
        get => m_AutoSend;
        set => m_AutoSend = value;
    }

    void Update()
    {
        EnsureSubsystem();
        EnsureConnected();

        if (!m_AutoSend || m_Stream == null || Time.unscaledTime < m_NextSendTime)
            return;

        m_NextSendTime = Time.unscaledTime + 1f / Mathf.Max(1f, m_SendHz);

        if (m_HandSubsystem == null || !m_HandSubsystem.running)
        {
            LogStatusThrottled("XRHandSubsystem not running; waiting to send right hand.");
            return;
        }

        if (!TryFillRightHand(m_HandSubsystem.rightHand))
            return;

        SendLine(BuildJsonLine());
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
                Debug.Log($"RightHandUnityUdpTcpSender connected to {m_Host}:{m_Port}", this);
        }
        catch (Exception exception)
        {
            CloseConnection();
            if (m_LogConnection)
                LogStatusThrottled($"RightHandUnityUdpTcpSender connect failed: {exception.Message}");
        }
    }

    bool TryFillRightHand(XRHand hand)
    {
        if (!hand.isTracked)
        {
            LogStatusThrottled("Right hand is not tracked; no dexterous-hand packet sent.");
            return false;
        }

        for (var i = 0; i < s_UnityToTwentyOne.Length; i++)
        {
            var jointId = s_UnityToTwentyOne[i];
            var joint = hand.GetJoint(jointId);
            if (!joint.TryGetPose(out var pose))
            {
                LogStatusThrottled($"Right hand joint pose unavailable: 21[{i}]={jointId}; packet skipped.");
                return false;
            }

            var baseIndex = i * 3;
            m_RightPositions[baseIndex] = pose.position.x;
            m_RightPositions[baseIndex + 1] = pose.position.y;
            m_RightPositions[baseIndex + 2] = pose.position.z;

            var euler = NormalizeEulerDegrees(pose.rotation.eulerAngles);
            m_RightRotations[baseIndex] = euler.x;
            m_RightRotations[baseIndex + 1] = euler.y;
            m_RightRotations[baseIndex + 2] = euler.z;
        }

        return true;
    }

    string BuildJsonLine()
    {
        m_Builder.Length = 0;
        m_Builder.Append('{');
        AppendJsonProperty("frameId", m_FrameId++);
        m_Builder.Append(',');
        AppendJsonProperty("unityTime", Time.realtimeSinceStartupAsDouble);
        m_Builder.Append(',');
        AppendJsonProperty("source", "xr-hands");
        m_Builder.Append(',');
        AppendJsonProperty("jointFormat", "unity-xr-hands-26-to-mediapipe21");
        m_Builder.Append(',');
        AppendJsonProperty("hand", "right");
        m_Builder.Append(',');
        AppendJsonProperty("rightTracked", true);
        m_Builder.Append(',');
        AppendJsonArray("rightPositions", m_RightPositions);
        m_Builder.Append(',');
        AppendJsonArray("rightRotations", m_RightRotations);

        if (m_SendLeftHandZeros)
        {
            m_Builder.Append(',');
            AppendJsonArray("leftPositions", m_LeftZeros);
            m_Builder.Append(',');
            AppendJsonArray("leftRotations", m_LeftZeros);
        }

        m_Builder.Append('}');
        return m_Builder.ToString();
    }

    void AppendJsonProperty(string name, string value)
    {
        AppendJsonName(name);
        m_Builder.Append('"').Append(value).Append('"');
    }

    void AppendJsonProperty(string name, bool value)
    {
        AppendJsonName(name);
        m_Builder.Append(value ? "true" : "false");
    }

    void AppendJsonProperty(string name, uint value)
    {
        AppendJsonName(name);
        m_Builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    void AppendJsonProperty(string name, double value)
    {
        AppendJsonName(name);
        m_Builder.Append(value.ToString("F6", CultureInfo.InvariantCulture));
    }

    void AppendJsonArray(string name, float[] values)
    {
        AppendJsonName(name);
        m_Builder.Append('[');
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                m_Builder.Append(',');
            m_Builder.Append(values[i].ToString("F6", CultureInfo.InvariantCulture));
        }
        m_Builder.Append(']');
    }

    void AppendJsonName(string name)
    {
        m_Builder.Append('"').Append(name).Append("\":");
    }

    static Vector3 NormalizeEulerDegrees(Vector3 euler)
    {
        return new Vector3(WrapAngleDegrees(euler.x), WrapAngleDegrees(euler.y), WrapAngleDegrees(euler.z));
    }

    static float WrapAngleDegrees(float degrees)
    {
        while (degrees > 180f)
            degrees -= 360f;
        while (degrees < -180f)
            degrees += 360f;
        return degrees;
    }

    void SendLine(string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            m_Stream.Write(bytes, 0, bytes.Length);
            m_Stream.Write(s_Newline, 0, s_Newline.Length);
        }
        catch (Exception exception)
        {
            if (m_LogConnection)
                Debug.LogWarning($"RightHandUnityUdpTcpSender send failed: {exception.Message}", this);
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

    void LogStatusThrottled(string message)
    {
        if (!m_LogConnection)
            return;

        var now = DateTime.UtcNow;
        if (message == m_LastStatus && now < m_NextStatusLogTimeUtc)
            return;

        m_LastStatus = message;
        m_NextStatusLogTimeUtc = now.AddSeconds(m_StatusLogIntervalSeconds);
        Debug.Log("[RightHandUnityUdpTcpSender] " + message, this);
    }
}
