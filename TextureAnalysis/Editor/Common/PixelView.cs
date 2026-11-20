using System;
using Unity.Collections;
using UnityEngine;

namespace Synthos.TextureAnalysis
{
    /// <summary>
    /// The level-0 pixels of a readable texture, without a managed copy where possible.
    /// </summary>
    /// <remarks>
    /// Full-resolution <c>GetPixels32()</c> arrays (64 MB for a 4096x4096 texture) were the
    /// main source of editor heap growth across builds: Mono's non-moving collector frees them,
    /// but the heap keeps growing to fit the next ones and never shrinks. For a readable RGBA32
    /// texture this is a view of the texture's own CPU memory and allocates nothing. Any other
    /// readable format falls back to <c>GetPixels32()</c> copied into native memory, so callers
    /// see the same values as before.
    /// <para>
    /// A view is invalidated by anything that changes the texture's CPU data (Apply, SetPixels,
    /// Reinitialize, EditorUtility.CompressTexture). Writes to a view change the texture; call
    /// Apply afterwards to upload them. Always dispose.
    /// </para>
    /// </remarks>
    internal readonly struct PixelView : IDisposable
    {
        public readonly NativeArray<Color32> Pixels;
        private readonly bool _owned;

        private PixelView(NativeArray<Color32> pixels, bool owned)
        {
            Pixels = pixels;
            _owned = owned;
        }

        public int Length => Pixels.Length;

        /// <summary>True when <see cref="Of"/> returns a view without copying.</summary>
        public static bool IsDirect(Texture2D texture)
        {
            return texture != null && texture.isReadable && texture.format == TextureFormat.RGBA32;
        }

        /// <summary>The texture's level-0 pixels. The texture must be readable.</summary>
        public static PixelView Of(Texture2D texture)
        {
            if (IsDirect(texture))
                return new PixelView(texture.GetPixelData<Color32>(0), owned: false);

            return new PixelView(
                new NativeArray<Color32>(texture.GetPixels32(), Allocator.Persistent),
                owned: true
            );
        }

        /// <summary>A native copy of the texture's level-0 pixels, owned by the caller.</summary>
        public static PixelView CopyOf(Texture2D texture)
        {
            if (IsDirect(texture))
            {
                return new PixelView(
                    new NativeArray<Color32>(texture.GetPixelData<Color32>(0), Allocator.Persistent),
                    owned: true
                );
            }
            return Of(texture);
        }

        public void Dispose()
        {
            if (_owned && Pixels.IsCreated)
                Pixels.Dispose();
        }
    }
}
