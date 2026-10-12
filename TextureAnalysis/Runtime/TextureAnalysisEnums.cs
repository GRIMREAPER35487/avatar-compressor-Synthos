namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// Strategy type for complexity analysis.
    /// </summary>
    internal enum AnalysisStrategyType
    {
        Fast,
        HighAccuracy,
        Perceptual,
        Combined,
    }

    /// <summary>
    /// Target platform for texture compression format selection.
    /// </summary>
    internal enum CompressionPlatform
    {
        Auto, // Detect from build target
        Desktop, // DXT/BC formats (PC VRChat)
        Mobile, // ASTC formats (Quest/Android)
    }

    /// <summary>
    /// Format options for frozen textures.
    /// </summary>
    internal enum FrozenTextureFormat
    {
        Auto, // Use automatic format selection
        DXT1, // RGB only, 4 bpp (Desktop)
        DXT5, // RGBA, 8 bpp (Desktop)
        BC5, // Normal maps, 8 bpp (Desktop)
        BC7, // Highest quality, 8 bpp (Desktop)
        ASTC_4x4, // Mobile highest quality, 8 bpp
        ASTC_6x6, // Mobile balanced, 3.56 bpp
        ASTC_8x8, // Mobile efficient, 2 bpp
    }
}
