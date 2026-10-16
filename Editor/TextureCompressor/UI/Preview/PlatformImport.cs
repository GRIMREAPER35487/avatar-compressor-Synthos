using System;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture.ui
{
    /// <summary>
    /// A texture's import (format, size, mip count) on the platform the settings compress for.
    /// </summary>
    /// <remarks>
    /// Unity imports a texture separately for each build target, and a loaded texture is the
    /// import for the current one. When Target Platform is set to the other platform (Mobile
    /// while the build target is Windows, or Desktop on Android), the loaded texture's DXT/BC or
    /// ASTC data isn't what that platform uses, so the preview reads that platform's import
    /// settings instead: its format (override or Unity's automatic choice), Max Size and mipmaps.
    /// Unity doesn't import anything for this. Texture pixels still come from the current import.
    /// </remarks>
    internal readonly struct PlatformImport
    {
        public readonly TextureFormat Format;
        public readonly Vector2Int Size;
        public readonly int MipCount;

        private PlatformImport(TextureFormat format, Vector2Int size, int mipCount)
        {
            Format = format;
            Size = size;
            MipCount = mipCount;
        }

        /// <summary>
        /// The build platform's importer name whose settings the preview reads instead of the
        /// loaded import, or null when Target Platform matches the build target.
        /// </summary>
        public static string OtherPlatformName(CompressionPlatform setting)
        {
            bool mobile = TextureFormatSelector.ResolvePlatform(setting) == CompressionPlatform.Mobile;
            var target = EditorUserBuildSettings.activeBuildTarget;
            bool mobileTarget = target == BuildTarget.Android || target == BuildTarget.iOS;
            if (mobile == mobileTarget)
                return null;
            return mobile ? "Android" : "Standalone";
        }

        public static PlatformImport Get(Texture2D texture, CompressionPlatform setting)
        {
            var loaded = new PlatformImport(
                texture.format,
                new Vector2Int(texture.width, texture.height),
                texture.mipmapCount
            );
            string platform = OtherPlatformName(setting);
            if (platform == null)
                return loaded;
            if (!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer))
                return loaded;

            var settings = importer.GetPlatformTextureSettings(platform);
            bool overridden = settings != null && settings.overridden;
            var importerFormat =
                overridden && settings.format != TextureImporterFormat.Automatic
                    ? settings.format
                    : importer.GetAutomaticFormat(platform);
            var format = Enum.IsDefined(typeof(TextureFormat), (int)importerFormat)
                ? (TextureFormat)(int)importerFormat
                : texture.format;

            // The loaded import's size before its Max Size, then the other platform's Max Size
            int maxSize = overridden ? settings.maxTextureSize : importer.maxTextureSize;
            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            Vector2Int size;
            if (width > 0 && height > 0)
            {
                if (importer.npotScale != TextureImporterNPOTScale.None)
                {
                    width = Mathf.ClosestPowerOfTwo(width);
                    height = Mathf.ClosestPowerOfTwo(height);
                }
                size = TextureSourceCache.FitWithin(new Vector2Int(width, height), maxSize);
            }
            else
            {
                size = TextureSourceCache.FitWithin(loaded.Size, maxSize);
            }

            int mipCount = importer.mipmapEnabled ? MemoryCalculator.FullMipCount(size.x, size.y) : 1;
            return new PlatformImport(format, size, mipCount);
        }
    }
}
