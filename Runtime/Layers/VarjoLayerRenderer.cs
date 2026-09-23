using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace Varjo.XR.Layers
{
    public class VarjoLayerRenderer : MonoBehaviour
    {
        private const int kMaxViews = 4;

        [Tooltip("The XR camera, whose per-view matrices the layer is rendered with. Defaults to Camera.main.")]
        public Camera xrCamera;

        [Tooltip("A disabled camera providing culling mask, clear settings and pipeline settings for the view cameras.")]
        public Camera viewCameraTemplate;

        [Tooltip("Below the plugin's layer if negative, above it otherwise.")]
        public int order = -1;

        public VarjoLayersSupport.LayerFlags flags = VarjoLayersSupport.LayerFlags.ChromaKeyMasking;

        [Range(0.1f, 1.0f)]
        [Tooltip("Resolution relative to the XR camera's per-view resolution.")]
        public float resolutionScale = 1.0f;

        [Tooltip("Render upside down on graphics APIs that store render textures bottom-up (Direct3D), so the compositor shows the layer upright. Inverts face culling on the view cameras, which is supported for HDRP cameras.")]
        public bool flipY = true;

        private readonly List<Camera> m_ViewCameras = new List<Camera>();
        private readonly List<RenderTexture> m_Targets = new List<RenderTexture>();
        private readonly List<XRDisplaySubsystem> m_DisplaySubsystems = new List<XRDisplaySubsystem>();
        private readonly HashSet<Camera> m_InvertedCulling = new HashSet<Camera>();
        private bool m_WarnedAboutCulling;
        private VarjoLayer m_Layer;
        private CommandBuffer m_CommandBuffer;
        private int m_ViewCount;
        private bool m_Rendering;

        public VarjoLayer Layer => m_Layer;

        public IReadOnlyList<Camera> ViewCameras => m_ViewCameras;

        private void OnEnable()
        {
            if (xrCamera == null)
                xrCamera = Camera.main;

            m_CommandBuffer = new CommandBuffer { name = $"VarjoLayerRenderer ({name})" };
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;

            m_Layer?.Dispose();
            m_Layer = null;
            DestroyViews();
            m_CommandBuffer?.Release();
            m_CommandBuffer = null;
        }

        public void ApplySettings()
        {
            m_Layer?.Update(VarjoLayersSupport.LayerDesc.Create(order, flags));
        }

        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            m_Rendering = false;

            if (xrCamera == null || viewCameraTemplate == null || !EnsureLayer())
                return;

            XRDisplaySubsystem display = GetDisplaySubsystem();

            if (display == null)
            {
                SetViewCamerasEnabled(false);
                return;
            }

            bool flip = flipY && SystemInfo.graphicsUVStartsAtTop;
            int view = 0;

            for (int passIndex = 0; passIndex < display.GetRenderPassCount() && view < kMaxViews; ++passIndex)
            {
                display.GetRenderPass(passIndex, out XRDisplaySubsystem.XRRenderPass pass);

                for (int parameterIndex = 0; parameterIndex < pass.GetRenderParameterCount() && view < kMaxViews; ++parameterIndex, ++view)
                {
                    pass.GetRenderParameter(xrCamera, parameterIndex, out XRDisplaySubsystem.XRRenderParameter parameter);

                    if (!IsValid(parameter.projection) || !IsValid(parameter.view))
                    {
                        SetViewCamerasEnabled(false);
                        return;
                    }

                    int width = Mathf.Max(1, Mathf.RoundToInt(pass.renderTargetDesc.width * parameter.viewport.width * resolutionScale));
                    int height = Mathf.Max(1, Mathf.RoundToInt(pass.renderTargetDesc.height * parameter.viewport.height * resolutionScale));

                    EnsureView(view, width, height, flip);
                    PlaceViewCamera(m_ViewCameras[view], parameter.view, flip ? s_FlipY * parameter.projection : parameter.projection);
                }
            }

            if (view != m_ViewCount)
                ResizeViews(view);

            SetViewCamerasEnabled(view > 0);
            m_Rendering = view > 0;
        }

        private void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!m_Rendering || m_Layer == null || !m_ViewCameras.Exists(cameras.Contains))
                return;

            m_CommandBuffer.Clear();

            if (m_Layer.Submit(m_CommandBuffer))
            {
                context.ExecuteCommandBuffer(m_CommandBuffer);
                context.Submit();
            }
        }

        private bool EnsureLayer()
        {
            if (m_Layer != null && m_Layer.IsValid)
                return true;

            m_Layer = VarjoLayer.Create(VarjoLayersSupport.LayerDesc.Create(order, flags));

            if (m_Layer == null)
                return false;

            for (int view = 0; view < m_Targets.Count; ++view)
                m_Layer.SetTexture(view, VarjoLayersSupport.TextureUsage.Color, m_Targets[view]);

            return true;
        }

        private void EnsureView(int view, int width, int height, bool flip)
        {
            while (m_ViewCameras.Count <= view)
            {
                Camera viewCamera = Instantiate(viewCameraTemplate, transform);
                viewCamera.name = $"{name} view {m_ViewCameras.Count}";
                viewCamera.stereoTargetEye = StereoTargetEyeMask.None;
                viewCamera.depth = xrCamera.depth - 1;
                viewCamera.eventMask = 0;
                viewCamera.gameObject.SetActive(true);
                viewCamera.enabled = false;
                m_ViewCameras.Add(viewCamera);
                m_Targets.Add(null);
            }

            Camera camera = m_ViewCameras[view];

            if (flip != m_InvertedCulling.Contains(camera))
            {
                if (SetInvertFaceCulling(camera, flip))
                {
                    if (flip)
                        m_InvertedCulling.Add(camera);
                    else
                        m_InvertedCulling.Remove(camera);
                }
                else if (flip && !m_WarnedAboutCulling)
                {
                    m_WarnedAboutCulling = true;
                    Debug.LogWarning($"[VarjoLayerRenderer] {name}: can't invert face culling for this render pipeline. The flipped layer renders back faces instead of front faces.");
                }
            }

            RenderTexture target = m_Targets[view];

            if (target != null && target.width == width && target.height == height)
                return;

            if (target != null)
            {
                target.Release();
                Destroy(target);
            }

            target = new RenderTexture(width, height, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.None)
            {
                name = $"{name} view {view}",
                antiAliasing = 1,
                useMipMap = false,
            };

            target.Create();
            m_Targets[view] = target;
            m_ViewCameras[view].targetTexture = target;
            m_Layer?.SetTexture(view, VarjoLayersSupport.TextureUsage.Color, target);
        }

        private static readonly Matrix4x4 s_FlipY = Matrix4x4.Scale(new Vector3(1.0f, -1.0f, 1.0f));

        private static bool IsValid(Matrix4x4 matrix)
        {
            float determinant = matrix.determinant;
            return !float.IsNaN(determinant) && !float.IsInfinity(determinant) && Mathf.Abs(determinant) > 1e-12f;
        }

        private static bool SetInvertFaceCulling(Camera camera, bool invert)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (MonoBehaviour component in camera.GetComponents<MonoBehaviour>())
            {
                if (component == null)
                    continue;

                System.Type type = component.GetType();
                FieldInfo field = type.GetField("invertFaceCulling", flags);

                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(component, invert);
                    return true;
                }

                PropertyInfo property = type.GetProperty("invertFaceCulling", flags);

                if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
                {
                    property.SetValue(component, invert);
                    return true;
                }
            }

            return false;
        }

        private static void PlaceViewCamera(Camera viewCamera, Matrix4x4 view, Matrix4x4 projection)
        {
            Matrix4x4 cameraToWorld = view.inverse * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f));

            viewCamera.transform.SetPositionAndRotation(cameraToWorld.GetColumn(3), cameraToWorld.rotation);
            viewCamera.worldToCameraMatrix = view;
            viewCamera.projectionMatrix = projection;
        }

        private void ResizeViews(int viewCount)
        {
            for (int view = m_ViewCameras.Count - 1; view >= viewCount; --view)
            {
                m_Layer?.SetTexture(view, VarjoLayersSupport.TextureUsage.Color, null);
                DestroyView(view);
            }

            m_ViewCount = viewCount;
        }

        private void DestroyViews()
        {
            for (int view = m_ViewCameras.Count - 1; view >= 0; --view)
                DestroyView(view);

            m_ViewCount = 0;
        }

        private void DestroyView(int view)
        {
            if (m_ViewCameras[view] != null)
            {
                m_InvertedCulling.Remove(m_ViewCameras[view]);
                Destroy(m_ViewCameras[view].gameObject);
            }

            if (m_Targets[view] != null)
            {
                m_Targets[view].Release();
                Destroy(m_Targets[view]);
            }

            m_ViewCameras.RemoveAt(view);
            m_Targets.RemoveAt(view);
        }

        private void SetViewCamerasEnabled(bool enabled)
        {
            foreach (Camera viewCamera in m_ViewCameras)
                if (viewCamera != null)
                    viewCamera.enabled = enabled;
        }

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
}