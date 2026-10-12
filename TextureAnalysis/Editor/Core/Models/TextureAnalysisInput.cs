namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// What the analysis needs to know about a texture besides its pixels.
    /// </summary>
    internal readonly struct TextureAnalysisInput
    {
        public readonly bool IsNormalMap;

        /// <summary>
        /// True when alpha is see-through on screen (see <see cref="AlphaCoverage"/>), so
        /// transparent pixels are left out of the score. Otherwise alpha holds other data, such
        /// as smoothness or a mask, and every pixel is scored.
        /// </summary>
        public readonly bool AlphaIsTransparency;

        public TextureAnalysisInput(bool isNormalMap, bool alphaIsTransparency = false)
        {
            IsNormalMap = isNormalMap;
            AlphaIsTransparency = alphaIsTransparency;
        }
    }
}
