namespace dev.limitex.avatar.compressor.texture
{
    /// <summary>
    /// Interface for texture complexity analysis strategies.
    /// </summary>
    public interface ITextureComplexityAnalyzer
    {
        /// <summary>
        /// Analyzes the complexity of processed pixel data.
        /// </summary>
        /// <param name="data">Processed pixel data to analyze</param>
        /// <returns>Normalized complexity value (0-1)</returns>
        float Analyze(ProcessedPixelData data);
    }
}
