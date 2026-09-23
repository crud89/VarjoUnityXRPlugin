using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Varjo.XR.Layers
{
    /// <summary>
    /// A managed wrapper around the varjo layers support library layer.
    /// </summary>
    public sealed class VarjoLayer : IDisposable
    {
        private readonly int m_InstallGeneration;

        public int Handle { get; private set; }

        public bool IsValid => Handle > 0 && m_InstallGeneration == VarjoLayersSupport.InstallGeneration;

        private VarjoLayer(int handle)
        {
            Handle = handle;
            m_InstallGeneration = VarjoLayersSupport.InstallGeneration;
        }

        public static VarjoLayer Create(VarjoLayersSupport.LayerDesc desc)
        {
            if (!VarjoLayersSupport.IsInstalled)
                return null;

            int result = VarjoLayersSupport.Native.VarjoLayers_CreateLayer(ref desc);

            if (result <= 0)
            {
                Debug.LogError($"[VarjoLayersSupport] Layer creation failed: {(VarjoLayersSupport.Result)result}");
                return null;
            }

            return new VarjoLayer(result);
        }

        public VarjoLayersSupport.Result Update(VarjoLayersSupport.LayerDesc desc) 
            => IsValid ? (VarjoLayersSupport.Result)VarjoLayersSupport.Native.VarjoLayers_UpdateLayer(Handle, ref desc) : VarjoLayersSupport.Result.InvalidLayer;

        public VarjoLayersSupport.Result SetTexture(int view, VarjoLayersSupport.TextureUsage usage, Texture texture)
        {
            if (!IsValid)
                return VarjoLayersSupport.Result.InvalidLayer;

            IntPtr pointer = texture != null ? texture.GetNativeTexturePtr() : IntPtr.Zero;
            var result = (VarjoLayersSupport.Result)VarjoLayersSupport.Native.VarjoLayers_SetLayerTexture(Handle, view, usage, pointer);

            if (result != VarjoLayersSupport.Result.Ok)
                Debug.LogError($"[VarjoLayersSupport] Layer {Handle}, view {view} ({usage}): texture rejected ({result}).");

            return result;
        }

        public VarjoLayersSupport.Result Clear() 
            => IsValid ? (VarjoLayersSupport.Result)VarjoLayersSupport.Native.VarjoLayers_ClearLayerTextures(Handle) : VarjoLayersSupport.Result.InvalidLayer;

        public bool Submit(CommandBuffer commandBuffer) 
            => IsValid && VarjoLayersSupport.IssueRenderEvent(commandBuffer, VarjoLayersSupport.RenderEvent.EndLayerFrame, new IntPtr(Handle));

        public bool SetViews(CommandBuffer commandBuffer, VarjoLayersSupport.LayerView[] views, int viewCount)
        {
            if (!IsValid)
                return false;

            uint token = VarjoLayersSupport.Native.VarjoLayers_StageLayerViews(Handle, views, viewCount);

            return token != 0 && VarjoLayersSupport.IssueRenderEvent(commandBuffer, VarjoLayersSupport.RenderEvent.CommitLayerViews, new IntPtr((long)token));
        }

        public VarjoLayersSupport.LayerStats GetStats()
        {
            VarjoLayersSupport.LayerStats stats = default;

            if (IsValid)
                VarjoLayersSupport.Native.VarjoLayers_GetLayerStats(Handle, out stats);

            return stats;
        }

        public void Dispose()
        {
            if (IsValid)
                VarjoLayersSupport.Native.VarjoLayers_DestroyLayer(Handle);

            Handle = 0;
        }
    }
}
