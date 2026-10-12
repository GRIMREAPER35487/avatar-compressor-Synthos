using UnityEngine;
using UnityEngine.Rendering;

namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// Decides whether a texture's alpha is see-through on screen. Only the main texture of a
    /// cutout or transparent material is; elsewhere alpha usually holds data such as
    /// smoothness, a mask or the X of a DXT5nm normal map, and its pixels are visible.
    /// </summary>
    internal static class AlphaCoverage
    {
        public static bool IsCoverage(Material material, string propertyName)
        {
            return material != null
                && IsMainTextureProperty(propertyName)
                && IsCutoutOrTransparent(material);
        }

        public static bool IsMainTextureProperty(string propertyName)
        {
            return propertyName == "_MainTex"
                || propertyName == "_BaseMap"
                || propertyName == "_BaseColorMap";
        }

        public static bool IsCutoutOrTransparent(Material material)
        {
            return material.renderQueue >= (int)RenderQueue.AlphaTest
                || material.IsKeywordEnabled("_ALPHATEST_ON")
                || material.IsKeywordEnabled("_ALPHABLEND_ON")
                || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");
        }
    }
}
