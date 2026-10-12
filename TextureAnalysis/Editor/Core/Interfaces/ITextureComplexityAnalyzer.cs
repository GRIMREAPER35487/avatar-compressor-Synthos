namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// Interface for texture complexity analysis strategies.
    /// Inherits from IAnalyzer for consistency with the common interface pattern.
    /// </summary>
    internal interface ITextureComplexityAnalyzer
        : IAnalyzer<ProcessedPixelData, TextureComplexityResult> { }
}
