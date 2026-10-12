namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// What the analysis needs to know about a texture besides its pixels.
    /// </summary>
    internal readonly struct TextureAnalysisInput
    {
        public readonly bool IsNormalMap;

        public TextureAnalysisInput(bool isNormalMap)
        {
            IsNormalMap = isNormalMap;
        }
    }
}
