using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Extra size halvings that "Keep under the mobile size limit" picks for single textures
    /// (<see cref="TextureCompressor.FitHalvings"/>), applied after the analysis so the build
    /// and the preview shrink the same textures the same way.
    /// </summary>
    internal static class FitHalving
    {
        /// <summary>
        /// Halves <paramref name="size"/> once per halving set for <paramref name="guid"/>.
        /// </summary>
        public static Vector2Int Apply(TextureCompressor config, string guid, Vector2Int size, out int halvings)
        {
            halvings = 0;
            if (config.FitHalvings == null || string.IsNullOrEmpty(guid))
                return size;
            if (!config.FitHalvings.TryGetValue(guid, out halvings) || halvings <= 0)
                return size;
            return Halve(size, halvings);
        }

        public static Vector2Int Halve(Vector2Int size, int times)
        {
            return new Vector2Int(Mathf.Max(1, size.x >> times), Mathf.Max(1, size.y >> times));
        }
    }
}
