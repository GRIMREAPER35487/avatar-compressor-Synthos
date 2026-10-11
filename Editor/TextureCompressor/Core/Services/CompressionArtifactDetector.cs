using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Predicts whether a low-bitrate block format (DXT1/DXT5, ASTC 5x5/6x6/8x8) would show
    /// visible artifacts on a texture: stair-stepping (banding) across smooth color changes, or
    /// blocky wrong colors along clean edges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Artifacts are only visible where the texture is clean: noise and fine detail hide them.
    /// The detector picks tiles that are mostly smooth but contain a color change (a gradient or
    /// an edge between flat areas), packs them into a small mosaic, compresses the mosaic with
    /// the candidate format, and compares the decoded pixels with the originals. Tiles are a
    /// multiple of every block size involved (4, 6, 8), so no compression block straddles two
    /// tiles.
    /// </para>
    /// <para>
    /// This measures the actual encoder output instead of guessing from statistics, so it also
    /// catches what a complexity score misses: a smooth gradient has little detail, yet it is
    /// where low-bitrate formats band the most.
    /// </para>
    /// </remarks>
    internal static class CompressionArtifactDetector
    {
        /// <summary>Tile edge length. Multiple of 4, 6 and 8 so no block straddles tiles.</summary>
        internal const int TileSize = 48;

        /// <summary>At most this many tiles are tested (an 8x8 mosaic, 384x384 pixels).</summary>
        private const int MaxTiles = 64;

        /// <summary>Neighbouring pixels at most this far apart (any channel) are a smooth area.</summary>
        internal const int SmoothDelta = 2;

        /// <summary>
        /// A tile is clean enough for artifacts to be visible when at least this fraction of its
        /// neighbour pairs is smooth. Noisy and finely detailed tiles mask artifacts.
        /// </summary>
        private const float MinSmoothPairFraction = 0.6f;

        /// <summary>A tile needs at least this channel range to contain a color change.</summary>
        private const int MinTileRange = 2;

        /// <summary>
        /// A tile with at least this many new steps contains a visible step line (a band edge
        /// crossing it). Banding over large smooth areas shows up as one such line per band.
        /// </summary>
        internal const int StepLineLength = TileSize / 4;

        /// <summary>
        /// In a smooth area, a decoded neighbour difference this much larger than the original
        /// (any channel) is a new step the original did not have.
        /// </summary>
        internal const int StepIncrease = 3;

        /// <summary>A decoded pixel off by more than this in any channel is visibly wrong.</summary>
        internal const int BadPixelError = 16;

        /// <summary>
        /// Artifacts are reported when they would cover more than this fraction of the whole
        /// texture. A small smooth patch on an otherwise detailed texture does not justify
        /// doubling the memory of the entire texture.
        /// </summary>
        internal const float MaxArtifactArea = 0.03f;

        /// <summary>Weight of visibly wrong pixels relative to banded tiles.</summary>
        private const float BadPixelAreaWeight = 4f;

        internal readonly struct Measurement
        {
            public readonly float StepFraction;
            public readonly float BandedTileFraction;
            public readonly float BadPixelFraction;
            public readonly float MeanSmoothError;
            public readonly int TilesTested;

            /// <summary>Fraction of the whole texture made of clean tiles with a color change.</summary>
            public readonly float CleanArea;

            public Measurement(
                float steps,
                float banded,
                float bad,
                float meanError,
                int tiles,
                float cleanArea
            )
            {
                StepFraction = steps;
                BandedTileFraction = banded;
                BadPixelFraction = bad;
                MeanSmoothError = meanError;
                TilesTested = tiles;
                CleanArea = cleanArea;
            }

            /// <summary>
            /// Estimated fraction of the whole texture showing artifacts: the tested tiles stand
            /// in for all clean tiles.
            /// </summary>
            public float ArtifactArea =>
                Mathf.Max(BandedTileFraction, BadPixelFraction * BadPixelAreaWeight) * CleanArea;

            public bool HasArtifacts => ArtifactArea > MaxArtifactArea;
        }

        /// <summary>
        /// Returns true for the low-bitrate formats this detector can veto.
        /// </summary>
        public static bool IsCandidateFormat(TextureFormat format)
        {
            return format == TextureFormat.DXT1
                || format == TextureFormat.DXT5
                || format == TextureFormat.ASTC_5x5
                || format == TextureFormat.ASTC_6x6
                || format == TextureFormat.ASTC_8x8;
        }

        /// <summary>
        /// Returns the next higher-quality format to try after a candidate format shows
        /// artifacts. DXT5 uses the same color encoding as DXT1, so DXT1/DXT5 go straight to
        /// BC7; ASTC steps down one block size at a time (8x8, 6x6, 5x5, 4x4).
        /// </summary>
        public static TextureFormat GetSaferFormat(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                    return TextureFormat.BC7;
                case TextureFormat.ASTC_8x8:
                    return TextureFormat.ASTC_6x6;
                case TextureFormat.ASTC_6x6:
                    return TextureFormat.ASTC_5x5;
                default:
                    return TextureFormat.ASTC_4x4;
            }
        }

        /// <summary>
        /// Returns <paramref name="format"/> if it compresses <paramref name="texture"/> without
        /// visible artifacts, otherwise the cheapest higher-quality format that does (ending at
        /// BC7 or ASTC 4x4, which are not tested). Non-candidate formats are returned unchanged.
        /// The texture must be readable.
        /// </summary>
        public static TextureFormat ChooseCleanFormat(Texture2D texture, TextureFormat format)
        {
            if (!IsCandidateFormat(format) || !TryBuildMosaic(texture, out var mosaic))
                return format;

            while (IsCandidateFormat(format) && Measure(mosaic, format).HasArtifacts)
                format = GetSaferFormat(format);
            return format;
        }

        /// <summary>
        /// Returns true if compressing <paramref name="texture"/> with <paramref name="format"/>
        /// would show visible banding or blocky edges. The texture must be readable.
        /// </summary>
        public static bool WouldShowArtifacts(Texture2D texture, TextureFormat format)
        {
            return Measure(texture, format).HasArtifacts;
        }

        /// <summary>
        /// Measures the artifacts of a candidate format on the clean tiles of a texture.
        /// Returns zeros when the texture has no clean tile with a color change.
        /// </summary>
        internal static Measurement Measure(Texture2D texture, TextureFormat format)
        {
            return TryBuildMosaic(texture, out var mosaic) ? Measure(mosaic, format) : default;
        }

        /// <summary>The clean tiles of a texture, packed side by side.</summary>
        private readonly struct Mosaic
        {
            public readonly Color32[] Pixels;
            public readonly int Width;
            public readonly int Height;
            public readonly int TileCount;
            public readonly int Columns;
            public readonly float CleanArea;
            public readonly bool Linear;

            public Mosaic(
                Color32[] pixels,
                int width,
                int height,
                int tileCount,
                int columns,
                float cleanArea,
                bool linear
            )
            {
                Pixels = pixels;
                Width = width;
                Height = height;
                TileCount = tileCount;
                Columns = columns;
                CleanArea = cleanArea;
                Linear = linear;
            }
        }

        private static bool TryBuildMosaic(Texture2D texture, out Mosaic mosaic)
        {
            mosaic = default;
            if (
                texture == null
                || !texture.isReadable
                || texture.width < TileSize
                || texture.height < TileSize
            )
            {
                return false;
            }

            int width = texture.width;
            var pixels = texture.GetPixels32();

            var tiles = SelectTiles(pixels, width, texture.height, out int cleanTiles);
            if (tiles.Count == 0)
                return false;

            int totalTiles = (width / TileSize) * (texture.height / TileSize);
            float cleanArea = cleanTiles / (float)totalTiles;

            int columns = Mathf.CeilToInt(Mathf.Sqrt(tiles.Count));
            int rows = Mathf.CeilToInt(tiles.Count / (float)columns);
            int mosaicWidth = columns * TileSize;
            var mosaicPixels = new Color32[mosaicWidth * rows * TileSize];

            for (int t = 0; t < tiles.Count; t++)
            {
                var (tileX, tileY) = tiles[t];
                int mx = (t % columns) * TileSize;
                int my = (t / columns) * TileSize;
                for (int y = 0; y < TileSize; y++)
                {
                    Array.Copy(
                        pixels,
                        (tileY + y) * width + tileX,
                        mosaicPixels,
                        (my + y) * mosaicWidth + mx,
                        TileSize
                    );
                }
            }

            mosaic = new Mosaic(
                mosaicPixels,
                mosaicWidth,
                rows * TileSize,
                tiles.Count,
                columns,
                cleanArea,
                !texture.isDataSRGB
            );
            return true;
        }

        private static Measurement Measure(in Mosaic mosaic, TextureFormat format)
        {
            var texture = new Texture2D(
                mosaic.Width,
                mosaic.Height,
                TextureFormat.RGBA32,
                false,
                mosaic.Linear
            );
            try
            {
                texture.SetPixels32(mosaic.Pixels);
                texture.Apply(false);
                EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Best);
                var decoded = texture.GetPixels32();
                return Compare(
                    mosaic.Pixels,
                    decoded,
                    mosaic.Width,
                    mosaic.TileCount,
                    mosaic.Columns,
                    mosaic.CleanArea
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// Picks tiles that are mostly smooth but contain a color change, smoothest first.
        /// </summary>
        private static List<(int X, int Y)> SelectTiles(
            Color32[] pixels,
            int width,
            int height,
            out int cleanTiles
        )
        {
            var candidates = new List<(float Smooth, int X, int Y)>();
            int pairsPerTile = 2 * TileSize * (TileSize - 1);

            for (int tileY = 0; tileY + TileSize <= height; tileY += TileSize)
            {
                for (int tileX = 0; tileX + TileSize <= width; tileX += TileSize)
                {
                    int smoothPairs = 0;
                    int transparent = 0;
                    int minR = 255,
                        minG = 255,
                        minB = 255;
                    int maxR = 0,
                        maxG = 0,
                        maxB = 0;

                    for (int y = 0; y < TileSize; y++)
                    {
                        int row = (tileY + y) * width + tileX;
                        for (int x = 0; x < TileSize; x++)
                        {
                            var c = pixels[row + x];
                            if (c.a < 128)
                                transparent++;
                            if (c.r < minR)
                                minR = c.r;
                            if (c.g < minG)
                                minG = c.g;
                            if (c.b < minB)
                                minB = c.b;
                            if (c.r > maxR)
                                maxR = c.r;
                            if (c.g > maxG)
                                maxG = c.g;
                            if (c.b > maxB)
                                maxB = c.b;

                            if (x + 1 < TileSize && MaxDelta(c, pixels[row + x + 1]) <= SmoothDelta)
                                smoothPairs++;
                            if (
                                y + 1 < TileSize
                                && MaxDelta(c, pixels[row + x + width]) <= SmoothDelta
                            )
                                smoothPairs++;
                        }
                    }

                    // Color errors in mostly transparent tiles cannot be seen.
                    if (transparent > TileSize * TileSize / 4)
                        continue;

                    int range = Math.Max(maxR - minR, Math.Max(maxG - minG, maxB - minB));
                    float smooth = smoothPairs / (float)pairsPerTile;
                    if (smooth >= MinSmoothPairFraction && range >= MinTileRange)
                        candidates.Add((smooth, tileX, tileY));
                }
            }

            cleanTiles = candidates.Count;
            candidates.Sort((a, b) => b.Smooth.CompareTo(a.Smooth));
            var tiles = new List<(int X, int Y)>(Math.Min(MaxTiles, candidates.Count));
            for (int i = 0; i < candidates.Count && i < MaxTiles; i++)
                tiles.Add((candidates[i].X, candidates[i].Y));
            return tiles;
        }

        private static Measurement Compare(
            Color32[] original,
            Color32[] decoded,
            int mosaicWidth,
            int tileCount,
            int columns,
            float cleanArea
        )
        {
            long smoothPairs = 0;
            long steps = 0;
            long badPixels = 0;
            long smoothPixels = 0;
            long smoothError = 0;
            int bandedTiles = 0;

            for (int t = 0; t < tileCount; t++)
            {
                int mx = (t % columns) * TileSize;
                int my = (t / columns) * TileSize;
                long stepsBefore = steps;

                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        int i = (my + y) * mosaicWidth + mx + x;
                        int error = MaxDelta(original[i], decoded[i]);
                        if (error > BadPixelError)
                            badPixels++;

                        // Neighbour pairs stay inside the tile: tile seams are not image content.
                        bool smooth = false;
                        if (x + 1 < TileSize)
                            smooth |= CountStep(
                                original,
                                decoded,
                                i,
                                i + 1,
                                ref smoothPairs,
                                ref steps
                            );
                        if (y + 1 < TileSize)
                            smooth |= CountStep(
                                original,
                                decoded,
                                i,
                                i + mosaicWidth,
                                ref smoothPairs,
                                ref steps
                            );

                        if (smooth)
                        {
                            smoothPixels++;
                            smoothError += error;
                        }
                    }
                }

                if (steps - stepsBefore >= StepLineLength)
                    bandedTiles++;
            }

            return new Measurement(
                smoothPairs > 0 ? steps / (float)smoothPairs : 0f,
                bandedTiles / (float)tileCount,
                badPixels / (float)(tileCount * TileSize * TileSize),
                smoothPixels > 0 ? smoothError / (float)smoothPixels : 0f,
                tileCount,
                cleanArea
            );
        }

        /// <summary>Counts a step if a smooth original pair became noticeably less smooth.</summary>
        /// <returns>True if the original pair was smooth.</returns>
        private static bool CountStep(
            Color32[] original,
            Color32[] decoded,
            int a,
            int b,
            ref long smoothPairs,
            ref long steps
        )
        {
            int originalDelta = MaxDelta(original[a], original[b]);
            if (originalDelta > SmoothDelta)
                return false;

            smoothPairs++;
            if (MaxDelta(decoded[a], decoded[b]) - originalDelta >= StepIncrease)
                steps++;
            return true;
        }

        private static int MaxDelta(Color32 a, Color32 b)
        {
            int r = Math.Abs(a.r - b.r);
            int g = Math.Abs(a.g - b.g);
            int bl = Math.Abs(a.b - b.b);
            return Math.Max(r, Math.Max(g, bl));
        }
    }
}
