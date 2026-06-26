using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public sealed class WristPoseControlToggle : MonoBehaviour
{
    [SerializeField]
    WristPoseTcpSender m_Target;

    [SerializeField, Min(0f)]
    float m_CooldownSeconds = 5f;

    [SerializeField]
    bool m_StartEnabled;

    [SerializeField]
    bool m_HoldToRun = true;

    [SerializeField]
    Renderer[] m_StateRenderers;

    [SerializeField]
    Color m_EnabledColor = new Color(0.1f, 0.9f, 0.2f, 1f);

    [SerializeField]
    Color m_DisabledColor = new Color(0.65f, 0.65f, 0.65f, 1f);

    [SerializeField]
    Color m_CooldownColor = new Color(1f, 0.75f, 0.05f, 1f);

    XRSimpleInteractable m_Interactable;
    bool m_ControlEnabled;
    float m_NextToggleTime;

    public bool ControlEnabled => m_ControlEnabled;

    void Awake()
    {
        m_Interactable = GetComponent<XRSimpleInteractable>();
        if (m_Target == null)
            m_Target = FindObjectOfType<WristPoseTcpSender>();

        m_ControlEnabled = m_HoldToRun ? false : m_StartEnabled;
        ApplyState(false);
    }

    void OnEnable()
    {
        if (m_Interactable == null)
            m_Interactable = GetComponent<XRSimpleInteractable>();

        if (m_Interactable != null)
        {
            m_Interactable.selectEntered.AddListener(OnSelectEntered);
            m_Interactable.selectExited.AddListener(OnSelectExited);
        }
    }

    void OnDisable()
    {
        if (m_Interactable != null)
        {
            m_Interactable.selectEntered.RemoveListener(OnSelectEntered);
            m_Interactable.selectExited.RemoveListener(OnSelectExited);
        }
    }

    void Update()
    {
        UpdateVisual(!m_HoldToRun && Time.unscaledTime < m_NextToggleTime);
    }

    void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (m_HoldToRun)
        {
            SetHoldControl(true);
            return;
        }

        TryToggle();
    }

    void OnSelectExited(SelectExitEventArgs args)
    {
        if (m_HoldToRun)
            SetHoldControl(false);
    }

    void SetHoldControl(bool enabled)
    {
        if (m_ControlEnabled == enabled)
            return;

        m_ControlEnabled = enabled;
        ApplyState(true);
    }

    public void TryToggle()
    {
        if (m_HoldToRun)
        {
            Debug.Log("VR wrist control uses hold-to-run deadman; select and hold to enable.", this);
            return;
        }

        var now = Time.unscaledTime;
        if (now < m_NextToggleTime)
        {
            Debug.Log($"VR wrist control toggle ignored. Cooldown remaining {m_NextToggleTime - now:F1}s", this);
            return;
        }

        m_ControlEnabled = !m_ControlEnabled;
        m_NextToggleTime = now + m_CooldownSeconds;
        ApplyState(true);
    }

    void ApplyState(bool log)
    {
        if (m_Target != null)
            m_Target.SetControlEnabled(m_ControlEnabled);

        UpdateVisual(!m_HoldToRun && Time.unscaledTime < m_NextToggleTime);

        if (log)
            Debug.Log($"VR wrist control switch={(m_ControlEnabled ? 1 : 0)}", this);
    }

    void UpdateVisual(bool coolingDown)
    {
        if (m_StateRenderers == null)
            return;

        var color = coolingDown ? m_CooldownColor : (m_ControlEnabled ? m_EnabledColor : m_DisabledColor);
        for (var i = 0; i < m_StateRenderers.Length; i++)
        {
            var renderer = m_StateRenderers[i];
            if (renderer == null)
                continue;
            renderer.material.color = color;
        }
    }
}
