using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using Varjo.XR;
using Varjo.XR.Layers;

/// <summary>
/// A test behavior that checks if the layer support library works.
/// </summary>
public class VarjoLayersDiagnostics : MonoBehaviour
{
    private const int kMaxViews = 4;

    [Tooltip("Seconds between status reports.")]
    public float reportInterval = 5.0f;

    [Tooltip("Submit a translucent per-view color layer above the plugin's layer.")]
    public bool selfTest = true;

    [Range(0.05f, 1.0f)]
    public float selfTestAlpha = 0.25f;

    [Tooltip("The XR camera, for the view order check. Defaults to Camera.main.")]
    public Camera xrCamera;

    private static readonly Color[] s_ViewColors = { Color.red, Color.green, Color.blue, Color.yellow };

    private readonly VarjoLayersSupport.LayerView[] m_ApplicationViews = new VarjoLayersSupport.LayerView[kMaxViews];
    private readonly List<XRDisplaySubsystem> m_DisplaySubsystems = new List<XRDisplaySubsystem>();
    private readonly RenderTexture[] m_SelfTestTargets = new RenderTexture[kMaxViews];
    private VarjoLayer m_SelfTestLayer;
    private CommandBuffer m_CommandBuffer;
    private VarjoLayersSupport.FrameStats m_LastStats;
    private float m_NextReport;

    private void OnEnable()
    {
        if (xrCamera == null)
            xrCamera = Camera.main;

        m_CommandBuffer = new CommandBuffer { name = "VarjoLayersDiagnostics self-test" };
        m_NextReport = Time.realtimeSinceStartup + 1.0f;
    }

    private void OnDisable()
    {
        m_SelfTestLayer?.Dispose();
        m_SelfTestLayer = null;

        for (int view = 0; view < kMaxViews; ++view)
        {
            if (m_SelfTestTargets[view] != null)
            {
                m_SelfTestTargets[view].Release();
                Destroy(m_SelfTestTargets[view]);
                m_SelfTestTargets[view] = null;
            }
        }

        m_CommandBuffer?.Release();
        m_CommandBuffer = null;
    }

    private void Start()
    {
        ReportEnvironment();

        if (selfTest)
            VarjoLayersSupport.SetEnabled(true);
    }

    private void Update()
    {
        if (selfTest)
            RenderSelfTest();
        else if (m_SelfTestLayer != null)
        {
            m_SelfTestLayer.Dispose();
            m_SelfTestLayer = null;
        }

        if (Time.realtimeSinceStartup >= m_NextReport)
        {
            m_NextReport = Time.realtimeSinceStartup + reportInterval;
            Report();
        }
    }

    [ContextMenu("Report now")]
    public void Report()
    {
        VarjoLayersSupport.FrameStats stats = VarjoLayersSupport.GetFrameStats();
        ulong frames = stats.framesTotal - m_LastStats.framesTotal;
        ulong composed = stats.framesComposited - m_LastStats.framesComposited;
        ulong events = stats.endOfFrameEvents - m_LastStats.endOfFrameEvents;
        ulong unfenced = stats.unfencedSubmits - m_LastStats.unfencedSubmits;
        m_LastStats = stats;

        var report = new StringBuilder("[VarjoLayersSupport] ");
        report.Append($"Frames: {frames} submitted, {composed} composed; last result: {stats.lastResult}. ");
        report.Append($"Plugin layer: {stats.viewCount} views, flags {(stats.applicationLayerFlags < 0 ? "not seen yet" : ((VarjoLayersSupport.LayerFlags)stats.applicationLayerFlags).ToString())}. ");
        report.Append($"Layers: {stats.layerCount}. EndLayerFrame events: {events}, submits without one: {unfenced}.");

        if (m_SelfTestLayer != null)
            AppendLayer(report, "self-test", m_SelfTestLayer);

        VarjoLayerRenderer[] layerRenderers = FindRenderers();

        foreach (var layerRenderer in layerRenderers)
            if (layerRenderer.Layer != null)
                AppendLayer(report, layerRenderer.name, layerRenderer.Layer);

        bool chromaKeyEnabled = VarjoChromaKey.IsChromaKeyEnabled();
        report.Append($" Chroma keying: {(chromaKeyEnabled ? "enabled" : "disabled")}.");
        Debug.Log(report.ToString());

        if (!chromaKeyEnabled)
            foreach (VarjoLayerRenderer layerRenderer in layerRenderers)
                if ((layerRenderer.flags & VarjoLayersSupport.LayerFlags.ChromaKeyMasking) != 0)
                    Debug.LogWarning($"[VarjoLayersSupport] {layerRenderer.name} uses ChromaKeyMasking, but chroma keying is disabled: the layer isn't masked and hides the video pass-through. Enable it with VarjoChromaKey.EnableChromaKey(true).");

        if (frames > 0 && stats.lastResult != VarjoLayersSupport.FrameResult.Composed && stats.lastResult != VarjoLayersSupport.FrameResult.Disabled)
            Debug.LogWarning($"[VarjoLayersSupport] Frames pass through unchanged: {stats.lastResult}.");

        if (events > 0 && unfenced > 0)
            Debug.LogWarning($"[VarjoLayersSupport] {unfenced} submits had no EndLayerFrame event since the previous one: frame submission runs ahead of the layer rendering on the render thread.");

        CheckViewOrder();
    }

    private void ReportEnvironment()
    {
        var status = VarjoLayersSupport.LastInstallStatus;
        var required = VarjoLayersSupport.InstallStatus.ModuleFound | VarjoLayersSupport.InstallStatus.VarjoApiResolved |
                                                   VarjoLayersSupport.InstallStatus.EndFramePatched | VarjoLayersSupport.InstallStatus.ShutdownPatched |
                                                   VarjoLayersSupport.InstallStatus.UnityPluginLoaded | VarjoLayersSupport.InstallStatus.GraphicsBackendReady;

        Debug.Log($"[VarjoLayersSupport] Graphics API: {SystemInfo.graphicsDeviceType}. Install status: {status}. Render event base: {VarjoLayersSupport.RenderEventBase}. Backend renderer: {VarjoLayersSupport.GetFrameStats().renderer}.");

        if ((status & required) != required)
            Debug.LogError($"[VarjoLayersSupport] Installation incomplete, missing: {required & ~status}.");
        else
            Debug.Log("[VarjoLayersSupport] Installation complete.");
    }

    private void RenderSelfTest()
    {
        int viewCount = Mathf.Clamp(VarjoLayersSupport.GetFrameStats().viewCount, 0, kMaxViews);

        if (viewCount == 0)
            return;

        if (m_SelfTestLayer == null || !m_SelfTestLayer.IsValid)
        {
            m_SelfTestLayer = VarjoLayer.Create(VarjoLayersSupport.LayerDesc.Create(1, VarjoLayersSupport.LayerFlags.AlphaBlend));

            if (m_SelfTestLayer == null)
                return;

            for (int view = 0; view < kMaxViews; ++view)
                if (m_SelfTestTargets[view] != null)
                    m_SelfTestLayer.SetTexture(view, VarjoLayersSupport.TextureUsage.Color, m_SelfTestTargets[view]);
        }

        m_CommandBuffer.Clear();

        for (int view = 0; view < viewCount; ++view)
        {
            if (m_SelfTestTargets[view] == null)
            {
                m_SelfTestTargets[view] = new RenderTexture(64, 64, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.None) { name = $"Self-test view {view}" };
                m_SelfTestTargets[view].Create();
                m_SelfTestLayer.SetTexture(view, VarjoLayersSupport.TextureUsage.Color, m_SelfTestTargets[view]);
            }

            Color color = s_ViewColors[view] * selfTestAlpha;
            color.a = selfTestAlpha;

            m_CommandBuffer.SetRenderTarget(m_SelfTestTargets[view]);
            m_CommandBuffer.ClearRenderTarget(false, true, color);
        }

        m_SelfTestLayer.Submit(m_CommandBuffer);
        Graphics.ExecuteCommandBuffer(m_CommandBuffer);
    }

    private void CheckViewOrder()
    {
        XRDisplaySubsystem display = GetDisplaySubsystem();

        if (display == null || xrCamera == null)
            return;

        int applicationViews = VarjoLayersSupport.GetApplicationBaseLayerViews(m_ApplicationViews);

        if (applicationViews == 0)
        {
            VarjoLayersSupport.FrameStats stats = VarjoLayersSupport.GetFrameStats();

            if (stats.framesTotal > 0)
                Debug.LogError($"[VarjoLayersSupport] The plugin submitted {stats.framesTotal} frames, but its layer wasn't found in any of them ({stats.lastResult}).");

            return;
        }

        var xrScales = new List<Vector2>();

        for (int passIndex = 0; passIndex < display.GetRenderPassCount(); ++passIndex)
        {
            display.GetRenderPass(passIndex, out XRDisplaySubsystem.XRRenderPass pass);

            for (int parameterIndex = 0; parameterIndex < pass.GetRenderParameterCount(); ++parameterIndex)
            {
                pass.GetRenderParameter(xrCamera, parameterIndex, out XRDisplaySubsystem.XRRenderParameter parameter);
                xrScales.Add(new Vector2(Mathf.Abs(parameter.projection.m00), Mathf.Abs(parameter.projection.m11)));
            }
        }

        if (xrScales.Count != applicationViews)
        {
            Debug.LogError($"[VarjoLayersSupport] View count mismatch: the XR display subsystem has {xrScales.Count} views, the plugin submits {applicationViews}.");
            return;
        }

        var report = new StringBuilder("[VarjoLayersSupport] View order check: ");
        bool ok = true;

        for (int view = 0; view < applicationViews; ++view)
        {
            Vector2 varjo = GetScale(m_ApplicationViews[view]);
            bool match = Mathf.Abs(varjo.x - xrScales[view].x) <= 0.03f * varjo.x && Mathf.Abs(varjo.y - xrScales[view].y) <= 0.03f * varjo.y;
            ok &= match;
            report.Append($"view {view} XR {xrScales[view].x:F3}x{xrScales[view].y:F3} / Varjo {varjo.x:F3}x{varjo.y:F3} {(match ? "ok" : "MISMATCH")}; ");
        }

        if (ok)
            Debug.Log(report.ToString());
        else
            Debug.LogError(report + "the layer renderer's view order doesn't match the plugin's.");
    }

    private static Vector2 GetScale(VarjoLayersSupport.LayerView view)
        => new Vector2((float)System.Math.Abs(view.GetProjection(0)), (float)System.Math.Abs(view.GetProjection(5)));

    private static void AppendLayer(StringBuilder report, string label, VarjoLayer layer)
    {
        VarjoLayersSupport.LayerStats stats = layer.GetStats();
        report.Append($" [{label} #{layer.Handle}: {stats.lastResult}, {stats.viewCount} views, {stats.framesSubmitted} submitted, {stats.framesSkipped} skipped]");
    }

    private static VarjoLayerRenderer[] FindRenderers()
#if UNITY_2022_2_OR_NEWER
        => FindObjectsByType<VarjoLayerRenderer>(FindObjectsSortMode.None);
#else
        => FindObjectsOfType<VarjoLayerRenderer>();
#endif

    private XRDisplaySubsystem GetDisplaySubsystem()
    {
        XRLoader loader = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager?.activeLoader : null;
        XRDisplaySubsystem display = loader != null ? loader.GetLoadedSubsystem<XRDisplaySubsystem>() : null;

        if (display == null)
        {
            SubsystemManager.GetSubsystems(m_DisplaySubsystems);
            display = m_DisplaySubsystems.Find(candidate => candidate.running);
        }

        return display != null && display.running ? display : null;
    }
}
