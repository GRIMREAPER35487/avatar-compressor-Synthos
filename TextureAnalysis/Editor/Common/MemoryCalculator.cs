using UnityEngine;

namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// Provides memory calculation utilities for textures.
    /// </summary>
    internal static class MemoryCalculator
    {
        /// <summary>
        /// Calculates compressed memory size based on format and mipmap count.
        /// </summary>
        /// <remarks>
        /// Block-compressed formats store whole blocks (4x4 for DXT/BC, up to 12x12 for ASTC), so
        /// each mip level is rounded up to whole blocks: small levels, and sizes that aren't a
        /// multiple of the block, cost more than bits-per-pixel alone says. Levels past 1x1 are
        /// not counted, so a mip count from a larger texture is safe to pass.
        /// </remarks>
        public static long CalculateCompressedMemory(
            int width,
            int height,
            TextureFormat format,
            int mipmapCount
        )
        {
            if (width <= 0 || height <= 0)
                return 0;
            int levels = Mathf.Min(mipmapCount, FullMipCount(width, height));
            bool blocks = TryGetBlock(format, out int blockWidth, out int blockHeight, out int blockBytes);
            float bitsPerPixel = GetBitsPerPixel(format);
            long bytes = 0;
            for (int index = 0; index < levels; ++index)
            {
                int levelWidth = Mathf.Max(1, width >> index);
                int levelHeight = Mathf.Max(1, height >> index);
                bytes += blocks
                    ? (long)((levelWidth + blockWidth - 1) / blockWidth)
                        * ((levelHeight + blockHeight - 1) / blockHeight)
                        * blockBytes
                    : (long)Mathf.RoundToInt((long)levelWidth * levelHeight * bitsPerPixel / 8f);
            }
            return bytes;
        }

        /// <summary>
        /// Number of mip levels of a full chain down to 1x1.
        /// </summary>
        public static int FullMipCount(int width, int height)
        {
            int longest = Mathf.Max(width, height);
            int count = 1;
            while (longest > 1)
            {
                longest >>= 1;
                count++;
            }
            return count;
        }

        private static bool TryGetBlock(TextureFormat format, out int width, out int height, out int bytes)
        {
            width = height = 4;
            bytes = 16;
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4:
                    bytes = 8;
                    return true;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ASTC_4x4:
                    return true;
                case TextureFormat.ASTC_5x5:
                    width = height = 5;
                    return true;
                case TextureFormat.ASTC_6x6:
                    width = height = 6;
                    return true;
                case TextureFormat.ASTC_8x8:
                    width = height = 8;
                    return true;
                case TextureFormat.ASTC_10x10:
                    width = height = 10;
                    return true;
                case TextureFormat.ASTC_12x12:
                    width = height = 12;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Formats bytes to human-readable string.
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / 1024f / 1024f:F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024f:F2} KB";
            return $"{bytes} B";
        }

        /// <summary>
        /// Returns bits per pixel for the given texture format.
        /// </summary>
        public static float GetBitsPerPixel(TextureFormat format)
        {
            switch (format)
            {
                // DXT/BC formats (Desktop)
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                    return 4f;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC5:
                case TextureFormat.BC7:
                    return 8f;
                case TextureFormat.BC4:
                    return 4f;
                case TextureFormat.BC6H:
                    return 8f;

                // ASTC formats (Mobile)
                case TextureFormat.ASTC_4x4:
                    return 8f;
                case TextureFormat.ASTC_5x5:
                    return 5.12f;
                case TextureFormat.ASTC_6x6:
                    return 3.56f;
                case TextureFormat.ASTC_8x8:
                    return 2f;
                case TextureFormat.ASTC_10x10:
                    return 1.28f;
                case TextureFormat.ASTC_12x12:
                    return 0.89f;

                // Uncompressed formats
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    return 32f;
                case TextureFormat.RGB24:
                    return 24f;
                case TextureFormat.RGB565:
                case TextureFormat.RGBA4444:
                case TextureFormat.ARGB4444:
                    return 16f;

                default:
                    return 32f; // Assume uncompressed RGBA
            }
        }
    }
}
