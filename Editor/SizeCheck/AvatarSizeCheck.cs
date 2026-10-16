using System;
using System.Collections.Generic;
using System.Linq;
using dev.limitex.avatar.compressor.editor.texture;
using dev.limitex.avatar.compressor.editor.texture.ui;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.size
{
    /// <summary>
    /// Keeps a mobile avatar under VRChat's size limits: picks how hard to compress before
    /// compressing, then checks the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sizes come from <see cref="BundleSizeCalculator"/>, which rebuilds the asset bundle's bytes
    /// without building it (uncompressed within about 0.001%, download within about 0.1% of the
    /// average upload; one upload can differ from the next by up to about 0.6% because Unity
    /// gives the avatar's objects new ids on every build). The targets keep a margin below the
    /// limits for that.
    /// </para>
    /// <para>
    /// The compression level is chosen before compressing so textures are compressed once:
    /// the avatar is measured as it is, the preview predicts the texture sizes for each step of
    /// <see cref="TextureCompressor.MaxResolution"/> (halved per step), and the first step that is
    /// predicted to fit is used.
    /// </para>
    /// </remarks>
    internal static class AvatarSizeCheck
    {
        /// <summary>VRChat's mobile (Android and iOS) limits. Settable for tests.</summary>
        public static long DownloadLimit = 10L * 1024 * 1024;

        public static long UncompressedLimit = 40L * 1024 * 1024;

        /// <summary>
        /// Targets below the limits: the download varies by up to ~0.6% between uploads of the
        /// same avatar, the uncompressed size is calculated almost exactly.
        /// </summary>
        public static long DownloadTarget => (long)(DownloadLimit * 0.99);

        public static long UncompressedTarget => (long)(UncompressedLimit * 0.999);

        /// <summary>
        /// Extra room when choosing the step: the plan predicts the compressed textures (within
        /// about 1% of the avatar's sizes on test avatars), the final check measures them.
        /// </summary>
        private const double PlanSlack = 0.01;

        /// <summary>
        /// Download saved per byte of removed material properties: they are near-identical
        /// values that compress well (1.93 MB removed saved 124 KB on a 16-material avatar).
        /// </summary>
        private const double StrippedDownloadRatio = 0.064;

        private const int LowestMaxResolution = 128;


        /// <summary>
        /// True when sizes can be calculated: a mobile build target and the Unity version the
        /// calculator's type table comes from.
        /// </summary>
        public static bool IsSupported(out string reason)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android && target != BuildTarget.iOS)
            {
                reason = "the size limit is only kept for Android and iOS builds";
                return false;
            }
            if (!Application.unityVersion.StartsWith("2022.3."))
            {
                reason = $"sizes can't be calculated in Unity {Application.unityVersion} (needs 2022.3)";
                return false;
            }
            reason = null;
            return true;
        }

        internal struct Sizes
        {
            public long Uncompressed;
            public long Download;
        }

        /// <summary>Calculates both sizes of the avatar as it is now.</summary>
        public static Sizes Measure(GameObject avatarRoot)
        {
            var res = Calculate(avatarRoot);
            return new Sizes
            {
                Uncompressed = res.Uncompressed,
                Download = BundleSizeCalculator.DownloadSize(res),
            };
        }

        /// <summary>
        /// Lowers <see cref="TextureCompressor.MaxResolution"/> on <paramref name="config"/> until
        /// the avatar is predicted to fit. Returns the number of steps taken, or -1 when even the
        /// lowest step is predicted to be over.
        /// </summary>
        public static int Plan(GameObject avatarRoot, TextureCompressor config) =>
            Plan(avatarRoot, config, lowerResolution: true, log: true, out _);

        /// <param name="lowerResolution">False to only predict the sizes at the current settings.</param>
        /// <param name="predicted">Predicted sizes at the step returned (the lowest when -1).</param>
        private static int Plan(GameObject avatarRoot, TextureCompressor config, bool lowerResolution, bool log, out Sizes predicted)
        {
            var before = Calculate(avatarRoot);
            long download = BundleSizeCalculator.DownloadSize(before);
            long stripped = StrippedPropertyBytes(avatarRoot, config);

            // Work in differences from the measured avatar: textures the preview doesn't list
            // (not on a material) stay counted as they are
            int original = config.MaxResolution;
            var preview = new PreviewGenerator();
            var rawCache = new Dictionary<Texture2D, byte[]>();
            long textureDownloadNow = -1;
            for (int step = 0; ; step++)
            {
                int maxResolution = Math.Max(original >> step, LowestMaxResolution);
                config.MaxResolution = maxResolution;
                var data = preview.Generate(
                    config,
                    TextureCompressorPreferences.AnalysisBackend,
                    TextureCompressorPreferences.ResizeBackend,
                    avatarRoot
                ).Where(d => d != null && d.Texture != null).ToList();

                long now = data.Sum(d => d.OriginalMemory);
                long after = data.Sum(d => d.IsProcessed ? d.EstimatedMemory : d.OriginalMemory);
                if (textureDownloadNow < 0)
                    textureDownloadNow = BundleSizeCalculator.CompressedSize(TextureStream(data, rawCache, predicted: false));
                long textureDownloadAfter = PredictedTextureDownload(data, rawCache);

                long uncompressed = before.Uncompressed - stripped - now + after;
                long predictedDownload = download - (long)(stripped * StrippedDownloadRatio) - textureDownloadNow + textureDownloadAfter;
                bool fits = uncompressed <= UncompressedTarget * (1 - PlanSlack) && predictedDownload <= DownloadTarget * (1 - PlanSlack);
                if (log)
                    Debug.Log(
                        $"[Avatar Compressor] Size plan: max resolution {maxResolution} -> download ~{MiB(predictedDownload)}, "
                            + $"uncompressed ~{MiB(uncompressed)}{(fits ? " (fits)" : "")}"
                    );
                predicted = new Sizes { Uncompressed = uncompressed, Download = predictedDownload };
                if (fits || !lowerResolution)
                    return step;
                if (maxResolution == LowestMaxResolution)
                    return -1;
            }
        }

        internal struct Estimate
        {
            /// <summary>Predicted sizes of the upload.</summary>
            public Sizes Sizes;

            /// <summary>Max Resolution the upload will use (lowered by Keep under size limit).</summary>
            public int MaxResolution;

            /// <summary>True when even the lowest Max Resolution is predicted to be over.</summary>
            public bool CannotFit;
        }

        /// <summary>
        /// Predicts the sizes the avatar will upload at with <paramref name="config"/>, for the
        /// preview. Runs the SDK's build steps on a copy of the avatar, as the upload does
        /// (VRCFury merges, editor-only components removed), without compressing, then predicts
        /// the compression as <see cref="Plan(GameObject, TextureCompressor)"/> does. Neither the
        /// avatar nor <paramref name="config"/> is changed.
        /// </summary>
        public static Estimate EstimateUpload(GameObject avatar, TextureCompressor config)
        {
            var clone = UnityEngine.Object.Instantiate(avatar);
            clone.name = avatar.name + "(Clone)";
            clone.SetActive(true);
            var holder = new GameObject("AvatarCompressorSettings") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var settings = holder.AddComponent<TextureCompressor>();
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(config), settings);

                TextureCompressorBuildHook.SkipCompression = true;
                try
                {
                    if (!VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPreprocessAvatar(clone))
                        throw new InvalidOperationException("the avatar's build steps failed (see the console)");
                }
                finally
                {
                    TextureCompressorBuildHook.SkipCompression = false;
                }

                int steps = Plan(clone, settings, settings.KeepUnderSizeLimit, log: false, out var predicted);
                return new Estimate
                {
                    Sizes = predicted,
                    MaxResolution = settings.MaxResolution,
                    CannotFit = steps < 0,
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// Compressed size of the textures as the preview predicts them. A texture's mipmaps are
        /// its downscaled versions, already encoded, so a texture predicted at 512 is stood in
        /// for by its mip chain from 512 down; smaller textures compress worse, which a single
        /// ratio for all sizes would miss. Scaled to the predicted bytes for format changes.
        /// </summary>
        private static long PredictedTextureDownload(List<TexturePreviewData> data, Dictionary<Texture2D, byte[]> rawCache)
        {
            var stream = TextureStream(data, rawCache, predicted: true);
            if (stream.Length == 0)
                return 0;
            long predictedBytes = data.Sum(d => d.IsProcessed ? d.EstimatedMemory : d.OriginalMemory);
            double ratio = (double)BundleSizeCalculator.CompressedSize(stream) / stream.Length;
            return (long)(ratio * predictedBytes);
        }

        /// <summary>
        /// The textures' data back to back, 16-byte aligned like the bundle's texture stream:
        /// as imported, or (predicted) from the mip level matching each predicted size.
        /// </summary>
        private static byte[] TextureStream(List<TexturePreviewData> data, Dictionary<Texture2D, byte[]> rawCache, bool predicted)
        {
            var stream = new System.IO.MemoryStream();
            foreach (var d in data)
            {
                var tex = d.Texture;
                if (!rawCache.TryGetValue(tex, out var raw))
                    rawCache[tex] = raw = tex.GetRawTextureData();
                int offset = 0;
                if (predicted && d.IsProcessed)
                {
                    int width = tex.width, height = tex.height;
                    for (int mip = 0; mip < tex.mipmapCount - 1 && width > d.RecommendedSize.x; mip++)
                    {
                        offset += (int)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(width, height, tex.graphicsFormat);
                        width = Math.Max(1, width >> 1);
                        height = Math.Max(1, height >> 1);
                    }
                }
                if (offset >= raw.Length)
                    offset = 0;
                stream.Write(raw, offset, raw.Length - offset);
                while (stream.Length % 16 != 0)
                    stream.WriteByte(0);
            }
            return stream.ToArray();
        }

        public static string MiB(long bytes) => $"{bytes / (1024.0 * 1024.0):0.00} MiB";

        private const string TempFolder = "Assets/_AvatarCompressorTemp/_SizeCheck";

        /// <summary>
        /// Calculates the avatar as the SDK uploads it: saved as a prefab, as the SDK does before
        /// building (the live copy in the scene measures a few percent larger).
        /// </summary>
        private static BundleSizeCalculator.Result Calculate(GameObject avatarRoot)
        {
            string name = avatarRoot.name.Replace("(Clone)", "").Trim();
            if (!AssetDatabase.IsValidFolder("Assets/_AvatarCompressorTemp"))
                AssetDatabase.CreateFolder("Assets", "_AvatarCompressorTemp");
            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets/_AvatarCompressorTemp", "_SizeCheck");
            string path = $"{TempFolder}/{Guid.NewGuid():N}.prefab";
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(avatarRoot, path);
                // The SDK's file names only change a few bytes of the bundle
                return BundleSizeCalculator.Calculate(prefab, name.ToLowerInvariant() + ".unity3d", path.ToLowerInvariant(), emit: true);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        /// <summary>
        /// Bytes "Remove properties the shader doesn't use" will take out of the avatar's materials.
        /// </summary>
        private static long StrippedPropertyBytes(GameObject avatarRoot, TextureCompressor config)
        {
            if (!config.StripUnusedMaterialProperties)
                return 0;
            long bytes = 0;
            var materials = new HashSet<Material>(
                avatarRoot.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null)
            );
            foreach (var material in materials)
                bytes += UnusedMaterialPropertyStripper.RemovableBytes(material);
            return bytes;
        }
    }
}
