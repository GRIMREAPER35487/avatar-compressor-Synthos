using dev.limitex.avatar.compressor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture.ui
{
    /// <summary>
    /// Data class for texture preview display.
    /// Uses class instead of struct because it contains reference types (FrozenTextureSettings).
    /// </summary>
    internal sealed class TexturePreviewData
    {
        public Texture2D Texture { get; set; }
        public string Guid { get; set; }
        public float Complexity { get; set; }
        public int RecommendedDivisor { get; set; }
        public Vector2Int OriginalSize { get; set; }
        public Vector2Int RecommendedSize { get; set; }
        public TexturePropertyCategory TextureType { get; set; }
        public bool IsProcessed { get; set; }
        public SkipReason SkipReason { get; set; }
        public long OriginalMemory { get; set; }
        public long EstimatedMemory { get; set; }
        public bool IsNormalMap { get; set; }
        public TextureFormat? PredictedFormat { get; set; }
        public bool HasAlpha { get; set; }
        public bool IsFrozen { get; set; }
        public FrozenTextureSettings FrozenSettings { get; set; }

        /// <summary>On-avatar density of the texture, when it could be measured.</summary>
        public TexelDensityInfo? Density { get; set; }

        /// <summary>Size the density is measured at before and after compression.</summary>
        public Vector2Int BaseSize { get; set; }

        /// <summary>
        /// Size the texture would need to reach its target density when Max Resolution is
        /// what holds it back; null otherwise.
        /// </summary>
        public Vector2Int? MaxResolutionWanted { get; set; }
    }
}
