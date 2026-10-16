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
    /// What to shrink is chosen before compressing so textures are compressed once: the
    /// avatar is measured as it is, the preview predicts the compressed textures, and while
    /// the prediction is over the textures that lose the least are halved, one at a time
    /// (<see cref="TextureCompressor.FitHalvings"/>).
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

        /// <summary>A texture "Keep under the mobile size limit" makes smaller for the upload.</summary>
        internal struct FitChange
        {
            public Texture2D Texture;
            public Vector2Int From;
            public Vector2Int To;
        }

        internal sealed class FitPlan
        {
            /// <summary>Predicted sizes of the upload with <see cref="Changes"/>.</summary>
            public Sizes Predicted;

            /// <summary>Textures made smaller, in the order they were picked.</summary>
            public List<FitChange> Changes = new List<FitChange>();

            /// <summary>True when the avatar is predicted to stay over even with every texture it can shrink shrunk.</summary>
            public bool CannotFit;
        }

        /// <summary>
        /// Picks the textures to make smaller so the avatar is predicted to fit, and sets them on
        /// <paramref name="config"/> (<see cref="TextureCompressor.FitHalvings"/>) for the
        /// compression to apply. Textures that lose the least are picked first.
        /// </summary>
        public static FitPlan Plan(GameObject avatarRoot, TextureCompressor config) =>
            Plan(avatarRoot, config, shrink: true, log: true);

        /// <param name="shrink">False to only predict the sizes at the current settings.</param>
        private static FitPlan Plan(GameObject avatarRoot, TextureCompressor config, bool shrink, bool log)
        {
            var before = Calculate(avatarRoot);
            long download = BundleSizeCalculator.DownloadSize(before);
            long stripped = StrippedPropertyBytes(avatarRoot, config);
            long uncompressedGoal = (long)(UncompressedTarget * (1 - PlanSlack));
            long downloadGoal = (long)(DownloadTarget * (1 - PlanSlack));

            // Work in differences from the measured avatar: textures the preview doesn't list
            // (not on a material) stay counted as they are
            var halvings = new Dictionary<string, int>();
            config.FitHalvings = halvings;
            var preview = new PreviewGenerator();
            var rawCache = new Dictionary<Texture2D, byte[]>();
            long textureDownloadNow = -1;
            var pickOrder = new List<string>();
            var plan = new FitPlan();
            for (int round = 0; ; round++)
            {
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
                bool fits = uncompressed <= uncompressedGoal && predictedDownload <= downloadGoal;
                if (log)
                    Debug.Log(
                        $"[Avatar Compressor] Size plan{(round > 0 ? $" ({halvings.Count} textures smaller)" : "")}: "
                            + $"download ~{MiB(predictedDownload)}, uncompressed ~{MiB(uncompressed)}{(fits ? " (fits)" : "")}"
                    );
                plan.Predicted = new Sizes { Uncompressed = uncompressed, Download = predictedDownload };
                plan.Changes = data.Where(d => d.IsProcessed && d.FitHalvings > 0)
                    .Select(d => new FitChange
                    {
                        Texture = d.Texture,
                        From = new Vector2Int(d.RecommendedSize.x << d.FitHalvings, d.RecommendedSize.y << d.FitHalvings),
                        To = d.RecommendedSize,
                    })
                    .OrderBy(c => pickOrder.IndexOf(data.First(d => d.Texture == c.Texture).Guid))
                    .ToList();
                if (fits || !shrink)
                    return plan;
                if (round >= MaxPlanRounds)
                {
                    plan.CannotFit = true;
                    return plan;
                }

                // Download saved per uncompressed byte saved, from the textures as they are now
                long predictedBytes = data.Sum(d => d.IsProcessed ? d.EstimatedMemory : d.OriginalMemory);
                double downloadRatio = predictedBytes > 0 ? (double)textureDownloadAfter / predictedBytes : 0.25;
                if (!PickHalvings(data, config, halvings, pickOrder, uncompressed - uncompressedGoal, predictedDownload - downloadGoal, downloadRatio))
                {
                    plan.CannotFit = true;
                    return plan;
                }
            }
        }

        /// <summary>Times the plan predicts again after picking textures before giving up.</summary>
        private const int MaxPlanRounds = 6;

        /// <summary>
        /// Halves textures, cheapest loss first, until the estimate says the excess is gone.
        /// Returns false when no texture can be made smaller.
        /// </summary>
        /// <remarks>
        /// A halving saves about three quarters of a texture's memory (mipmaps included) and
        /// the same share of its download. What it costs is weighed by how much the texture
        /// shows: its kind (base color most, masks and AO least), whether it is on the head,
        /// how much detail it has, and whether it would still meet its on-avatar density. Each
        /// halving of the same texture costs twice the one before, so the loss is spread out
        /// instead of one texture shrinking to nothing.
        /// </remarks>
        private static bool PickHalvings(
            List<TexturePreviewData> data,
            TextureCompressor config,
            Dictionary<string, int> halvings,
            List<string> pickOrder,
            long uncompressedExcess,
            long downloadExcess,
            double downloadRatio
        )
        {
            var candidates = data.Where(d =>
                    d.IsProcessed
                    && !d.IsFrozen
                    && d.FollowsGuid == null
                    && !d.FormatOnly
                    && !string.IsNullOrEmpty(d.Guid)
                )
                .Select(d => new Candidate { Data = d, Size = d.RecommendedSize, Memory = d.EstimatedMemory, Halvings = d.FitHalvings })
                .ToList();
            bool picked = false;
            while (uncompressedExcess > 0 || downloadExcess > 0)
            {
                Candidate best = null;
                double bestScore = double.MaxValue;
                foreach (var c in candidates)
                {
                    var next = FitHalving.Halve(c.Size, 1);
                    if (Math.Max(next.x, next.y) < config.MinResolution || c.Memory <= 0)
                        continue;
                    double score = HalvingCost(c.Data, next, c.Halvings) / (c.Memory * 0.75);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = c;
                    }
                }
                if (best == null)
                    break;

                long saved = (long)(best.Memory * 0.75);
                uncompressedExcess -= saved;
                downloadExcess -= (long)(saved * downloadRatio);
                best.Size = FitHalving.Halve(best.Size, 1);
                best.Memory -= saved;
                best.Halvings++;
                halvings[best.Data.Guid] = best.Halvings;
                if (!pickOrder.Contains(best.Data.Guid))
                    pickOrder.Add(best.Data.Guid);
                picked = true;
            }
            return picked;
        }

        private sealed class Candidate
        {
            public TexturePreviewData Data;
            public Vector2Int Size;
            public long Memory;
            public int Halvings;
        }

        /// <summary>How much making the texture <paramref name="next"/> in size costs the avatar's look.</summary>
        private static double HalvingCost(TexturePreviewData d, Vector2Int next, int halvingsTaken)
        {
            double cost = d.TextureType switch
            {
                TexturePropertyCategory.Main => 1.0,
                TexturePropertyCategory.Normal => 0.7,
                TexturePropertyCategory.Emission => 0.5,
                _ => 0.4,
            };
            cost *= Math.Max(0.1, d.Complexity);
            if (d.Density.HasValue)
            {
                var density = d.Density.Value;
                if (density.FromHead)
                    cost *= 1.5;
                // Still as sharp on the avatar as it needs to be: hardly a loss
                if (density.DensityAt(next) >= density.TargetDensity)
                    cost *= 0.1;
            }
            return cost * (1 << halvingsTaken);
        }

        internal struct Estimate
        {
            /// <summary>Predicted sizes of the upload.</summary>
            public Sizes Sizes;

            /// <summary>Textures Keep under size limit will make smaller for the upload.</summary>
            public List<FitChange> Changes;

            /// <summary>True when the avatar is predicted to stay over even with those.</summary>
            public bool CannotFit;
        }

        /// <summary>
        /// Predicts the sizes the avatar will upload at with <paramref name="config"/>, for the
        /// preview. Runs the SDK's build steps on a copy of the avatar, as the upload does
        /// (VRCFury merges, editor-only components removed), without compressing, then predicts
        /// the compression as <see cref="Plan(GameObject, TextureCompressor)"/> does. Neither the
        /// avatar nor <paramref name="config"/> is changed.
        /// </summary>
        /// <param name="progressMessage">
        /// Shown in a progress bar while the sizes are calculated, after the build steps: their
        /// own progress windows and warnings (VRCFury's) stay visible while they run.
        /// </param>
        /// <exception cref="BuildStepsFailedException">A build step (VRCFury or another tool) failed.</exception>
        public static Estimate EstimateUpload(GameObject avatar, TextureCompressor config, string progressMessage = null)
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
                        throw new BuildStepsFailedException();
                }
                finally
                {
                    TextureCompressorBuildHook.SkipCompression = false;
                }

                if (progressMessage != null)
                    EditorUtility.DisplayProgressBar("Avatar Compressor", progressMessage, 0.5f);

                var plan = Plan(clone, settings, settings.KeepUnderSizeLimit, log: false);
                return new Estimate
                {
                    Sizes = plan.Predicted,
                    Changes = plan.Changes,
                    CannotFit = plan.CannotFit,
                };
            }
            finally
            {
                if (progressMessage != null)
                    EditorUtility.ClearProgressBar();
                UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// The SDK's build steps (VRCFury and other tools) reported a failure on the avatar
        /// copy; a real upload would most likely stop at the same step.
        /// </summary>
        internal sealed class BuildStepsFailedException : Exception
        {
            public BuildStepsFailedException()
                : base("The avatar's build steps failed (see the console)") { }
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
