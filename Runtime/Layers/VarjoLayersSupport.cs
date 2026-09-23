using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Varjo.XR.Layers
{
    public static class VarjoLayersSupport
    {
        #region "Type definitions"

        [Flags]
        public enum InstallStatus : uint
        {
            None = 0,
            ModuleFound = 1 << 0,
            VarjoApiResolved = 1 << 1,
            EndFramePatched = 1 << 2,
            ShutdownPatched = 1 << 3,
            VarjoLibDelayLoaded = 1 << 4,
            UnityPluginLoaded = 1 << 5,
            GraphicsBackendReady = 1 << 6,
        }

        public enum Result
        {
            Ok = 0,
            InvalidLayer = -1,
            InvalidArgument = -2,
            TooManyLayers = -3,
            InvalidView = -4,
            WrongGraphicsApi = -5,
            UnsupportedFormat = -6,
            UnsupportedLayout = -7,
            NoGraphicsBackend = -8,
        }

        public enum TextureUsage
        {
            Color = 0,
            Depth = 1,
        }

        public enum MatrixSource
        {
            Layer = 0,
            Custom = 1,
        }

        public enum FrameResult
        {
            Composed = 0,
            Disabled = 1,
            ApiUnavailable = 2,
            NoApplicationLayer = 3,
            MultipleApplicationLayers = 4,
            TooManyLayers = 5,
            NoGraphicsBackend = 6,
        }

        public enum LayerResult
        {
            Pending = -1,
            Submitted = 0,
            Disabled = 1,
            NoViews = 2,
            TooManyViews = 3,
            MissingTexture = 4,
            SwapChainFailed = 5,
            CopyFailed = 6,
            NotPrepared = 7,
        }

        [Flags]
        public enum LayerFlags
        {
            None = 0x0,
            AlphaBlend = 0x2,
            DepthTesting = 0x4,
            InvertAlpha = 0x8,
            UsingOcclusionMesh = 0x10,
            ChromaKeyMasking = 0x20,
            Foveated = 0x40,
        }

        public enum RenderEvent
        {
            EndLayerFrame = 0,
            CommitLayerViews = 1,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ApplicationBaseLayerDesc
        {
            public long setFlags;
            public long clearFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LayerDesc
        {
            public long order;
            public int enabled;
            public MatrixSource matrixSource;
            public long flags;
            public long space;
            public int depthEnabled;
            public int depthTestRangeEnabled;
            public double minDepth;
            public double maxDepth;
            public double nearZ;
            public double farZ;
            public double depthTestNearZ;
            public double depthTestFarZ;

            public static LayerDesc Create(long order, LayerFlags flags, MatrixSource matrixSource = MatrixSource.Layer)
            {
                return new LayerDesc
                {
                    order = order,
                    enabled = 1,
                    matrixSource = matrixSource,
                    flags = (long)flags,
                    minDepth = 0.0,
                    maxDepth = 1.0,
                    nearZ = 1000.0,
                    farZ = 0.1,
                    depthTestNearZ = 0.0,
                    depthTestFarZ = 1000.0,
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct LayerView
        {
            public fixed double projection[16];
            public fixed double view[16];

            public double GetProjection(int index)
            {
                fixed (double* values = projection)
                    return values[index];
            }

            public double GetView(int index)
            {
                fixed (double* values = view)
                    return values[index];
            }

            public static LayerView FromColumnMajor(Matrix4x4 projection, Matrix4x4 view)
            {
                var result = new LayerView();

                for (int column = 0; column < 4; ++column)
                {
                    for (int row = 0; row < 4; ++row)
                    {
                        result.projection[column * 4 + row] = projection[row, column];
                        result.view[column * 4 + row] = view[row, column];
                    }
                }

                return result;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameStats
        {
            public ulong framesTotal;
            public ulong framesComposited;
            public ulong framesPassedThrough;
            public long applicationLayerFlags;
            public ulong endOfFrameEvents;
            public ulong unfencedSubmits;
            public FrameResult lastResult;
            public int viewCount;
            public int renderer;
            public int layerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LayerStats
        {
            public ulong framesSubmitted;
            public ulong framesSkipped;
            public LayerResult lastResult;
            public int viewCount;
        }

        #endregion

        private static IntPtr s_RenderEventFunc = IntPtr.Zero;

        public static InstallStatus LastInstallStatus { get; private set; }

        public static bool IsInstalled 
            => (LastInstallStatus & InstallStatus.EndFramePatched) != 0;

        public static int InstallGeneration { get; private set; }

        public static int RenderEventBase { get; private set; } = -1;

        public static InstallStatus Install()
        {
            if (!CheckLayouts())
                return InstallStatus.None;

            try
            {
                string logPath = Path.Combine(Application.persistentDataPath, "VarjoXRLayersSupport.log");
                Native.VarjoLayers_SetLogPath(logPath);
                LastInstallStatus = (InstallStatus)Native.VarjoLayers_Install();
                s_RenderEventFunc = Native.VarjoLayers_GetRenderEventFunc();
                RenderEventBase = Native.VarjoLayers_GetRenderEventBase();
                ++InstallGeneration;
                Debug.Log($"[VarjoLayersSupport] Install status: {LastInstallStatus}. Native log: {logPath}");
            }
            catch (DllNotFoundException e)
            {
                Debug.LogError($"[VarjoLayersSupport] Native library not found: {e.Message}");
                return LastInstallStatus = InstallStatus.None;
            }

            if ((LastInstallStatus & InstallStatus.VarjoLibDelayLoaded) != 0)
                Debug.LogError("[VarjoLayersSupport] VarjoLib.dll is delay-loaded; the hooks will not see its calls.");

            if ((LastInstallStatus & InstallStatus.VarjoApiResolved) == 0)
                Debug.LogError("[VarjoLayersSupport] Required VarjoLib.dll functions not found; see the native log.");

            if ((LastInstallStatus & InstallStatus.UnityPluginLoaded) == 0)
                Debug.LogError("[VarjoLayersSupport] Unity did not call UnityPluginLoad; no graphics backend is available.");
            else if ((LastInstallStatus & InstallStatus.GraphicsBackendReady) == 0)
                Debug.LogWarning($"[VarjoLayersSupport] No graphics backend for {SystemInfo.graphicsDeviceType}; frames pass through unchanged.");

            return LastInstallStatus;
        }

        public static void SetEnabled(bool enabled) 
            => Native.VarjoLayers_SetEnabled(enabled);

        public static bool IsUnityPluginLoaded() 
            => Native.IsUnityPluginLoaded();

        public static Result SetApplicationBaseLayer(LayerFlags set, LayerFlags clear)
        {
            var desc = new ApplicationBaseLayerDesc { setFlags = (long)set, clearFlags = (long)clear };
            return (Result)Native.VarjoLayers_SetApplicationBaseLayer(ref desc);
        }

        public static int GetApplicationBaseLayerViews(LayerView[] views) 
            => Native.VarjoLayers_GetApplicationBaseLayerViews(views, views?.Length ?? 0);

        public static FrameStats GetFrameStats()
        {
            Native.VarjoLayers_GetFrameStats(out FrameStats stats);
            return stats;
        }

        public static void ResetStats() 
            => Native.VarjoLayers_ResetStats();

        public static bool IssueRenderEvent(CommandBuffer commandBuffer, RenderEvent renderEvent, IntPtr data)
        {
            if (RenderEventBase < 0 || s_RenderEventFunc == IntPtr.Zero)
                return false;

            commandBuffer.IssuePluginEventAndData(s_RenderEventFunc, RenderEventBase + (int)renderEvent, data);
            return true;
        }

        private static unsafe bool CheckLayouts()
        {
            bool match = 
                sizeof(ApplicationBaseLayerDesc) == 16 && 
                sizeof(LayerDesc) == 88 && 
                sizeof(LayerView) == 256 &&
                sizeof(FrameStats) == 64 && 
                sizeof(LayerStats) == 24;

            if (!match)
                Debug.LogError("[VarjoLayersSupport] Managed struct layouts don't match the native plugin; not installing.");

            return match;
        }

        /// <summary>
        /// The imported symbols from the layers support library (P/Invoke).
        /// </summary>
        internal static class Native
        {
            private const string LIBRARY_NAME = "VarjoXRLayersSupport";

            [DllImport(LIBRARY_NAME, CharSet = CharSet.Unicode)] 
            public static extern void VarjoLayers_SetLogPath(string path);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern uint VarjoLayers_Install();

            [DllImport(LIBRARY_NAME)] 
            public static extern void VarjoLayers_SetEnabled([MarshalAs(UnmanagedType.U1)] bool enabled);
            
            [DllImport(LIBRARY_NAME)] 
            [return: MarshalAs(UnmanagedType.U1)] 
            public static extern bool IsUnityPluginLoaded();

            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_SetApplicationBaseLayer(ref ApplicationBaseLayerDesc desc);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_CreateLayer(ref LayerDesc desc);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_UpdateLayer(int layer, ref LayerDesc desc);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_DestroyLayer(int layer);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_SetLayerTexture(int layer, int view, TextureUsage usage, IntPtr nativeTexture);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_ClearLayerTextures(int layer);
            
            [DllImport(LIBRARY_NAME)] 
            
            public static extern uint VarjoLayers_StageLayerViews(int layer, [In] LayerView[] views, int viewCount);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_GetApplicationBaseLayerViews([Out] LayerView[] views, int capacity);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern void VarjoLayers_GetFrameStats(out FrameStats stats);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_GetLayerStats(int layer, out LayerStats stats);
            
            [DllImport(LIBRARY_NAME)] 
            public static extern void VarjoLayers_ResetStats();
            
            [DllImport(LIBRARY_NAME)] 
            public static extern IntPtr VarjoLayers_GetRenderEventFunc();
            
            [DllImport(LIBRARY_NAME)] 
            public static extern int VarjoLayers_GetRenderEventBase();
        }
    }
}
