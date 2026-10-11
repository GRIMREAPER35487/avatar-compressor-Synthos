using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Provides full-quality copies ("source textures") of texture assets so they can be
    /// compressed once, from the original image, instead of re-compressing Unity's
    /// already-compressed import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source texture is made by copying the image file into a temporary folder and importing
    /// the copy uncompressed with the creator's other import settings (normal-map conversion,
    /// alpha handling, color space). The pixels are saved as a lossless PNG in
    /// <c>Library/AvatarCompressorCache</c>, keyed by the asset's GUID and dependency hash,
    /// and the temporary import is deleted. Later builds load the PNG directly.
    /// </para>
    /// <para>
    /// The original asset and its import settings are never modified. Textures without a
    /// source file (runtime-generated, lilToon bakes), HDR textures and non-2D textures are
    /// not supported; callers fall back to the imported texture.
    /// </para>
    /// </remarks>
    internal sealed class TextureSourceCache
    {
        /// <summary>Largest source texture dimension. Larger images are downscaled on import.</summary>
        internal const int MaxSourceSize = 4096;

        // Bump when the cached PNG contents change meaning, to invalidate old entries.
        private const string CacheVersion = "1";

        private const string ImportFolder = BuildAssetStore.RootFolder + "/_SourceImport";

        private static readonly string[] s_hdrExtensions = { ".exr", ".hdr" };

        private readonly Dictionary<Texture2D, string> _cachePaths =
            new Dictionary<Texture2D, string>();

        internal static string CacheFolder =>
            Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Library", "AvatarCompressorCache")
            );

        /// <summary>
        /// Returns true if a source texture can be made for the texture.
        /// Cheap: does not import anything.
        /// </summary>
        public static bool CanUseSource(Texture2D texture)
        {
            return TryGetImporter(texture, out _, out _);
        }

        /// <summary>
        /// Returns the dimensions the source texture has (or will have): the image file's size,
        /// scaled to fit within <see cref="MaxSourceSize"/>. Returns false if unsupported.
        /// </summary>
        public static bool TryGetSourceSize(Texture2D texture, out Vector2Int size)
        {
            size = default;
            if (!TryGetImporter(texture, out _, out var importer))
                return false;

            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (width <= 0 || height <= 0)
                return false;

            if (importer.npotScale != TextureImporterNPOTScale.None)
            {
                width = Mathf.ClosestPowerOfTwo(width);
                height = Mathf.ClosestPowerOfTwo(height);
            }

            size = FitWithin(new Vector2Int(width, height), MaxSourceSize);
            return true;
        }

        /// <summary>
        /// Scales a size down (never up) so its longest edge fits within <paramref name="max"/>,
        /// keeping the aspect ratio.
        /// </summary>
        public static Vector2Int FitWithin(Vector2Int size, int max)
        {
            int longest = Mathf.Max(size.x, size.y);
            if (longest <= max || longest <= 0)
                return size;

            float scale = (float)max / longest;
            return new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt(size.x * scale)),
                Mathf.Max(1, Mathf.RoundToInt(size.y * scale))
            );
        }

        /// <summary>
        /// Deletes every cached source texture. They are recreated on the next build.
        /// </summary>
        public static void ClearCache()
        {
            if (Directory.Exists(CacheFolder))
                Directory.Delete(CacheFolder, true);
        }

        /// <summary>
        /// Returns the size of the cached source texture, read from the PNG header without
        /// decoding it. Returns false if no source is cached for the texture.
        /// </summary>
        public bool TryGetCachedSize(Texture2D texture, out Vector2Int size)
        {
            size = default;
            if (texture == null || !_cachePaths.TryGetValue(texture, out string cachePath))
                return false;

            try
            {
                var header = new byte[24];
                using (var stream = File.OpenRead(cachePath))
                {
                    if (stream.Read(header, 0, header.Length) != header.Length)
                        return false;
                }

                // PNG: 8-byte signature, IHDR length + type, then big-endian width and height.
                int width =
                    (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int height =
                    (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                if (width <= 0 || height <= 0)
                    return false;

                size = new Vector2Int(width, height);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Makes sure a cached source exists for every supported texture, importing the
        /// missing ones in a single batch. Textures that fail are left out and fall back to
        /// their imported version.
        /// </summary>
        public void Prepare(IEnumerable<Texture2D> textures)
        {
            var missing =
                new List<(
                    Texture2D Texture,
                    string SourcePath,
                    TextureImporter Importer,
                    string CachePath
                )>();

            foreach (var texture in textures.Distinct())
            {
                if (texture == null || _cachePaths.ContainsKey(texture))
                    continue;
                if (!TryGetImporter(texture, out string path, out var importer))
                    continue;

                string cachePath = GetCachePath(path);
                if (File.Exists(cachePath))
                    _cachePaths[texture] = cachePath;
                else
                    missing.Add((texture, path, importer, cachePath));
            }

            if (missing.Count > 0)
                Generate(missing);
        }

        /// <summary>
        /// Loads the source texture for an imported texture, or returns null if none is cached.
        /// The result is a readable RGBA32 texture with the imported texture's color space,
        /// mipmap setting and sampler settings. The caller must destroy it.
        /// </summary>
        public Texture2D Load(Texture2D imported)
        {
            if (imported == null || !_cachePaths.TryGetValue(imported, out string cachePath))
                return null;

            bool linear = !imported.isDataSRGB;
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
            try
            {
                if (!File.Exists(cachePath) || !decoded.LoadImage(File.ReadAllBytes(cachePath)))
                    return null;

                var source = new Texture2D(
                    decoded.width,
                    decoded.height,
                    TextureFormat.RGBA32,
                    imported.mipmapCount > 1,
                    linear
                );
                source.SetPixels32(decoded.GetPixels32());
                source.Apply(imported.mipmapCount > 1);
                TextureReadback.CopyTextureSettings(imported, source);
                source.name = imported.name;
                return source;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Could not load source for '{imported.name}': {ex.Message}. "
                        + "Using the imported texture instead."
                );
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(decoded);
            }
        }

        /// <summary>
        /// Returns true if the source of a normal map is stored as plain RGB (XYZ).
        /// False means it was imported as a Unity normal map and its channel layout must be
        /// detected from the pixels.
        /// </summary>
        public static bool IsRawNormalSource(Texture2D texture)
        {
            return TryGetImporter(texture, out _, out var importer)
                && UsesRawNormalImport(importer);
        }

        private static bool UsesRawNormalImport(TextureImporter importer)
        {
            // Plain normal maps are imported as linear default textures so their channels are
            // exactly the file's XYZ. Height-map conversion and green flipping need Unity's
            // normal-map import, so those keep it.
            return importer.textureType == TextureImporterType.NormalMap
                && !importer.convertToNormalmap
                && !importer.flipGreenChannel;
        }

        private void Generate(
            List<(
                Texture2D Texture,
                string SourcePath,
                TextureImporter Importer,
                string CachePath
            )> jobs
        )
        {
            Directory.CreateDirectory(CacheFolder);
            EnsureFolder(ImportFolder);

            var importPaths = new List<(int Job, string Path)>();

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    string guid = AssetDatabase.AssetPathToGUID(job.SourcePath);
                    string importPath = $"{ImportFolder}/{guid}{Path.GetExtension(job.SourcePath)}";

                    try
                    {
                        File.Copy(ToFullPath(job.SourcePath), ToFullPath(importPath), true);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[Avatar Compressor] Could not copy source of '{job.Texture.name}': {ex.Message}"
                        );
                        continue;
                    }

                    var settings = new TextureImporterSettings();
                    job.Importer.ReadTextureSettings(settings);
                    SourceImportPostprocessor.Pending[importPath] = (
                        settings,
                        UsesRawNormalImport(job.Importer)
                    );
                    AssetDatabase.ImportAsset(
                        importPath,
                        ImportAssetOptions.ForceSynchronousImport
                    );
                    importPaths.Add((i, importPath));
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            try
            {
                foreach (var (jobIndex, importPath) in importPaths)
                {
                    var job = jobs[jobIndex];
                    if (WriteCache(importPath, job.CachePath, job.Texture.name))
                    {
                        DeleteOldVersions(job.CachePath);
                        _cachePaths[job.Texture] = job.CachePath;
                    }
                }
            }
            finally
            {
                foreach (var (_, importPath) in importPaths)
                    SourceImportPostprocessor.Pending.Remove(importPath);

                AssetDatabase.DeleteAsset(ImportFolder);
            }
        }

        private static bool WriteCache(string importPath, string cachePath, string textureName)
        {
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(importPath);
            if (imported == null || !imported.isReadable)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Could not import source of '{textureName}'. "
                        + "Using the imported texture instead."
                );
                return false;
            }

            // Re-pack into RGBA32: the uncompressed import may be RGB24, R8 or Alpha8.
            var rgba = new Texture2D(
                imported.width,
                imported.height,
                TextureFormat.RGBA32,
                false,
                !imported.isDataSRGB
            );
            try
            {
                rgba.SetPixels32(imported.GetPixels32());
                rgba.Apply(false);
                File.WriteAllBytes(cachePath, rgba.EncodeToPNG());
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Could not cache source of '{textureName}': {ex.Message}"
                );
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rgba);
            }
        }

        private static bool TryGetImporter(
            Texture2D texture,
            out string path,
            out TextureImporter importer
        )
        {
            path = null;
            importer = null;

            // Only the texture asset itself: build-time replacements (e.g. lilToon bakes) have
            // different pixels than the file their registry entry points at.
            if (
                texture == null
                || !EditorUtility.IsPersistent(texture)
                || !AssetDatabase.IsMainAsset(texture)
            )
                return false;

            path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path))
                return false;
            if (s_hdrExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                return false;

            importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null || importer.textureShape != TextureImporterShape.Texture2D)
                return false;

            return File.Exists(ToFullPath(path));
        }

        private static string GetCachePath(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            Hash128 hash = AssetDatabase.GetAssetDependencyHash(assetPath);
            return Path.Combine(CacheFolder, $"{guid}_{hash}_v{CacheVersion}.png");
        }

        private static void DeleteOldVersions(string currentPath)
        {
            string name = Path.GetFileName(currentPath);
            string guidPrefix = name.Substring(0, name.IndexOf('_') + 1);

            foreach (string file in Directory.GetFiles(CacheFolder, guidPrefix + "*.png"))
            {
                if (!string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // Stale entry stays until the next successful write.
                    }
                }
            }
        }

        private static string ToFullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Applies source-import settings to the temporary copies made by
    /// <see cref="TextureSourceCache"/>, before Unity imports them.
    /// </summary>
    internal sealed class SourceImportPostprocessor : AssetPostprocessor
    {
        internal static readonly Dictionary<
            string,
            (TextureImporterSettings Settings, bool RawNormal)
        > Pending = new Dictionary<string, (TextureImporterSettings, bool)>(
            StringComparer.OrdinalIgnoreCase
        );

        private void OnPreprocessTexture()
        {
            if (!Pending.TryGetValue(assetPath, out var entry))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.SetTextureSettings(entry.Settings);

            if (entry.RawNormal)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
            }

            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.streamingMipmaps = false;
            importer.maxTextureSize = TextureSourceCache.MaxSourceSize;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
        }
    }
}
