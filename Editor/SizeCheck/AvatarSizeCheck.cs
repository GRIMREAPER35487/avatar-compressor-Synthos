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
        /// LZMA size per byte of freshly encoded block data, used only when a texture's sample
        /// can't be encoded (<see cref="SampledDownloadRatio"/>). Measured on an avatar whose
        /// textures went from imported ASTC 6x6 to mostly ASTC 4x4: about 0.48.
        /// </summary>
        private const double FreshEncodeDownloadRatio = 0.5;

        /// <summary>Tiles per side and tile size of the sample <see cref="SampledDownloadRatio"/> encodes.</summary>
        private const int SampleGrid = 4;

        private const int SampleTile = 64;

        // Samples encoded during the current plan and the time they took, for its log
        private static int _sampled;
        private static readonly System.Diagnostics.Stopwatch _sampleTime = new System.Diagnostics.Stopwatch();

        // The current plan's correction for the samples (Calibrate), worked out once per plan
        private static double? _calibration;

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

            /// <summary>LZMA size of the texture data on its own; only measured when asked for.</summary>
            public long TextureDownload;
        }

        /// <summary>Calculates both sizes of the avatar as it is now.</summary>
        /// <param name="textures">Also measure <see cref="Sizes.TextureDownload"/> (takes a moment longer).</param>
        public static Sizes Measure(GameObject avatarRoot, bool textures = false)
        {
            var res = Calculate(avatarRoot);
            return new Sizes
            {
                Uncompressed = res.Uncompressed,
                Download = BundleSizeCalculator.DownloadSize(res),
                TextureDownload = textures && res.TextureStream != null && res.TextureStream.Length > 0
                    ? BundleSizeCalculator.CompressedSize(res.TextureStream)
                    : 0,
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

            /// <summary>
            /// Controllers the build could not point at the compressed materials. The plan
            /// assumes the original textures drop out of the upload; these keep them in, so the
            /// prediction is low by about their size.
            /// </summary>
            public List<string> UnremappedControllers = new List<string>();

            /// <summary>Predicted download of all of the upload's texture data, to compare with the check's measurement.</summary>
            public long PredictedTextureDownload;

            // What the check after the build needs to make more textures smaller
            internal List<Candidate> Candidates;
            internal Dictionary<string, int> Halvings;
            internal List<string> PickOrder;
            internal int MinResolution;

            /// <summary>The compressed texture each source texture became, set by the build.</summary>
            internal Dictionary<Texture2D, Texture2D> Outputs;
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
            // The download is predicted less exactly than the uncompressed size; if the plan is
            // still low, the check after the build makes more textures smaller (ShrinkAfterBuild)
            long downloadGoal = (long)(DownloadTarget * (1 - PlanSlack));

            // Work in differences from the measured avatar: textures the preview doesn't list
            // (not on a material) stay counted as they are
            var halvings = new Dictionary<string, int>();
            config.FitHalvings = halvings;
            var preview = new PreviewGenerator();
            var rawCache = new Dictionary<Texture2D, byte[]>();
            var sampleCache = new Dictionary<(Texture2D, TextureFormat, Vector2Int), double>();
            _sampled = 0;
            _sampleTime.Reset();
            _calibration = null;
            long textureDownloadNow = -1;
            long allTexturesDownloadNow = -1;
            var pickOrder = new List<string>();
            var plan = new FitPlan { Halvings = halvings, PickOrder = pickOrder, MinResolution = config.MinResolution };
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
                {
                    textureDownloadNow = BundleSizeCalculator.CompressedSize(TextureStream(data, rawCache, predicted: false));
                    allTexturesDownloadNow = before.TextureStream != null && before.TextureStream.Length > 0
                        ? BundleSizeCalculator.CompressedSize(before.TextureStream)
                        : 0;
                }
                var perTexture = new List<(TexturePreviewData Data, long Download)>();
                long textureDownloadAfter = PredictedTextureDownload(data, rawCache, sampleCache, perTexture);
                plan.PredictedTextureDownload = allTexturesDownloadNow - textureDownloadNow + textureDownloadAfter;

                long uncompressed = before.Uncompressed - stripped - now + after;
                long predictedDownload = download - (long)(stripped * StrippedDownloadRatio) - textureDownloadNow + textureDownloadAfter;
                bool fits = uncompressed <= uncompressedGoal && predictedDownload <= downloadGoal;
                if (log)
                    Debug.Log(
                        $"[Avatar Compressor] Size plan{(round > 0 ? $" ({halvings.Count} textures smaller)" : "")}: "
                            + $"download ~{MiB(predictedDownload)} (textures ~{MiB(plan.PredictedTextureDownload)}), "
                            + $"uncompressed ~{MiB(uncompressed)}{(fits ? " (fits)" : "")}"
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
                plan.Candidates = CandidatesFrom(data, perTexture);
                bool done = fits || !shrink || round >= MaxPlanRounds;
                if (done && log)
                    LogTexturePredictions(perTexture);
                if (fits && shrink && halvings.Count > 0)
                {
                    var (memory, dl, count) = GiveBack(plan.Candidates, halvings, uncompressedGoal - uncompressed, downloadGoal - predictedDownload);
                    if (count > 0)
                    {
                        plan.Predicted = new Sizes { Uncompressed = uncompressed + memory, Download = predictedDownload + dl };
                        plan.PredictedTextureDownload += dl;
                        plan.Changes = ChangesFrom(plan.Candidates, pickOrder);
                        if (log)
                            Debug.Log(
                                $"[Avatar Compressor] Size plan: kept {count} halvings back that weren't needed "
                                    + $"({halvings.Count} textures smaller): download ~{MiB(plan.Predicted.Download)}, "
                                    + $"uncompressed ~{MiB(plan.Predicted.Uncompressed)} (fits)"
                            );
                    }
                }
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
                if (!PickHalvings(plan.Candidates, config.MinResolution, halvings, pickOrder, uncompressed - uncompressedGoal, predictedDownload - downloadGoal, downloadRatio))
                {
                    plan.CannotFit = true;
                    if (log)
                        LogTexturePredictions(perTexture);
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
            List<Candidate> candidates,
            int minResolution,
            Dictionary<string, int> halvings,
            List<string> pickOrder,
            long uncompressedExcess,
            long downloadExcess,
            double downloadRatio,
            Func<Candidate, bool> canHalve = null
        )
        {
            bool picked = false;
            while (uncompressedExcess > 0 || downloadExcess > 0)
            {
                Candidate best = null;
                double bestScore = double.MaxValue;
                foreach (var c in candidates)
                {
                    var next = FitHalving.Halve(c.Size, 1);
                    if (Math.Max(next.x, next.y) < minResolution || c.Memory <= 0)
                        continue;
                    if (canHalve != null && !canHalve(c))
                        continue;
                    // How much of what is over this halving removes, each limit counted up to
                    // all of its excess: a texture that clears the excess on its own isn't
                    // favoured over a cheaper one that also does
                    double savedMemory = c.Memory * 0.75;
                    double savedDownload = savedMemory * RatioOf(c, downloadRatio);
                    double benefit =
                        (uncompressedExcess > 0 ? Math.Min(1.0, savedMemory / uncompressedExcess) : 0)
                        + (downloadExcess > 0 ? Math.Min(1.0, savedDownload / downloadExcess) : 0);
                    if (benefit <= 0)
                        continue;
                    double score = HalvingCost(c.Data, next, c.Halvings) / benefit;
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
                downloadExcess -= (long)(saved * RatioOf(best, downloadRatio));
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

        /// <summary>The textures the plan made smaller, from the candidates, in the order they were picked.</summary>
        private static List<FitChange> ChangesFrom(List<Candidate> candidates, List<string> pickOrder) =>
            candidates.Where(c => c.Halvings > 0)
                .Select(c => (c, original: new Vector2Int(c.Data.RecommendedSize.x << c.Data.FitHalvings, c.Data.RecommendedSize.y << c.Data.FitHalvings)))
                .Select(p => new FitChange { Texture = p.c.Data.Texture, From = p.original, To = FitHalving.Halve(p.original, p.c.Halvings) })
                .OrderBy(ch => pickOrder.IndexOf(candidates.First(c => c.Data.Texture == ch.Texture).Data.Guid))
                .ToList();

        /// <summary>Download per byte of the candidate's memory, or <paramref name="fallback"/> if unknown.</summary>
        private static double RatioOf(Candidate c, double fallback) => c.DownloadRatio > 0 ? c.DownloadRatio : fallback;

        /// <summary>
        /// Textures "Keep under size limit" may make smaller, each with its own predicted
        /// download per byte (textures compress anywhere from about 0.1 to 0.7).
        /// </summary>
        private static List<Candidate> CandidatesFrom(
            List<TexturePreviewData> data,
            List<(TexturePreviewData Data, long Download)> perTexture
        )
        {
            var downloads = new Dictionary<TexturePreviewData, long>();
            foreach (var (d, dl) in perTexture)
                downloads[d] = dl;
            return data.Where(d =>
                    d.IsProcessed
                    && !d.IsFrozen
                    && d.FollowsGuid == null
                    && !d.FormatOnly
                    && !string.IsNullOrEmpty(d.Guid)
                )
                .Select(d => new Candidate
                {
                    Data = d,
                    Size = d.RecommendedSize,
                    Memory = d.EstimatedMemory,
                    Halvings = d.FitHalvings,
                    DownloadRatio = d.EstimatedMemory > 0 && downloads.TryGetValue(d, out long dl) ? (double)dl / d.EstimatedMemory : 0,
                })
                .ToList();
        }

        internal sealed class Candidate
        {
            public TexturePreviewData Data;
            public Vector2Int Size;
            public long Memory;
            public int Halvings;

            /// <summary>Predicted download per byte of memory.</summary>
            public double DownloadRatio;
        }

        /// <summary>
        /// After the plan fits, undoes halvings it didn't need, the most visible first, while
        /// the avatar is still predicted to fit: the picks are made in whole halvings with
        /// estimated savings, so the last ones often go past the goal.
        /// </summary>
        /// <returns>The memory and download added back.</returns>
        private static (long Memory, long Download, int Count) GiveBack(
            List<Candidate> candidates,
            Dictionary<string, int> halvings,
            long uncompressedRoom,
            long downloadRoom
        )
        {
            long addedMemory = 0, addedDownload = 0;
            int count = 0;
            while (true)
            {
                Candidate best = null;
                double bestCost = double.MinValue;
                long bestMemory = 0, bestDownload = 0;
                foreach (var c in candidates)
                {
                    if (c.Halvings <= 0 || !halvings.ContainsKey(c.Data.Guid) || c.DownloadRatio <= 0)
                        continue;
                    long memory = c.Memory * 3; // the next size up is about four times as large
                    long dl = (long)(memory * c.DownloadRatio);
                    if (memory > uncompressedRoom || dl > downloadRoom)
                        continue;
                    // what the last halving of this texture cost its look
                    double cost = HalvingCost(c.Data, c.Size, c.Halvings - 1);
                    if (cost > bestCost)
                    {
                        bestCost = cost;
                        best = c;
                        bestMemory = memory;
                        bestDownload = dl;
                    }
                }
                if (best == null)
                    break;

                var original = new Vector2Int(best.Data.RecommendedSize.x << best.Data.FitHalvings, best.Data.RecommendedSize.y << best.Data.FitHalvings);
                best.Halvings--;
                best.Size = FitHalving.Halve(original, best.Halvings);
                best.Memory += bestMemory;
                if (best.Halvings > 0)
                    halvings[best.Data.Guid] = best.Halvings;
                else
                    halvings.Remove(best.Data.Guid);
                uncompressedRoom -= bestMemory;
                downloadRoom -= bestDownload;
                addedMemory += bestMemory;
                addedDownload += bestDownload;
                count++;
            }
            return (addedMemory, addedDownload, count);
        }

        /// <summary>
        /// The safety net for a low download prediction: when the check after the build measures
        /// the avatar over the limit although the plan said it fits, makes more textures smaller
        /// on the built avatar, picked the same way as the plan picks them. A compressed texture
        /// is halved by dropping its largest mip level, so it is not encoded again and looks the
        /// same as its next mip. Measures again after each round and returns the last sizes.
        /// </summary>
        /// <param name="measured">The sizes the check measured.</param>
        /// <param name="halved">Number of textures made smaller here.</param>
        public static Sizes ShrinkAfterBuild(GameObject avatarRoot, FitPlan plan, Sizes measured, out int halved)
        {
            halved = 0;
            if (plan?.Candidates == null || plan.Outputs == null || plan.Outputs.Count == 0)
                return measured;

            var startHalvings = plan.Candidates.ToDictionary(c => c, c => c.Halvings);
            var startSizes = plan.Candidates.ToDictionary(c => c, c => c.Size);
            var dropped = new Dictionary<Candidate, int>();
            var sizes = measured;

            // How the upload's own texture data compresses, measured
            var res = Calculate(avatarRoot);
            double downloadRatio = res.TextureStream != null && res.TextureStream.Length > 0
                ? (double)BundleSizeCalculator.CompressedSize(res.TextureStream) / res.TextureStream.Length
                : FreshEncodeDownloadRatio;

            for (int round = 0; round < MaxShrinkRounds; round++)
            {
                long uncompressedExcess = sizes.Uncompressed - (long)(UncompressedTarget * (1 - PlanSlack));
                long downloadExcess = sizes.Download - (long)(DownloadTarget * (1 - PlanSlack));
                if (uncompressedExcess <= 0 && downloadExcess <= 0)
                    break;

                bool CanHalve(Candidate c) =>
                    plan.Outputs.TryGetValue(c.Data.Texture, out var output)
                    && CanDropTopMips(output, c.Halvings - startHalvings[c] + 1);
                if (!PickHalvings(plan.Candidates, plan.MinResolution, plan.Halvings, plan.PickOrder, uncompressedExcess, downloadExcess, downloadRatio, CanHalve))
                    break;

                bool changed = false;
                foreach (var c in plan.Candidates)
                {
                    int total = c.Halvings - startHalvings[c];
                    dropped.TryGetValue(c, out int done);
                    if (total <= done)
                        continue;
                    if (!DropTopMips(plan.Outputs[c.Data.Texture], total - done))
                    {
                        // keep the bookkeeping in line with the texture
                        c.Halvings = startHalvings[c] + done;
                        c.Size = FitHalving.Halve(startSizes[c], done);
                        plan.Halvings[c.Data.Guid] = c.Halvings;
                        continue;
                    }
                    dropped[c] = total;
                    changed = true;
                }
                if (!changed)
                    break;

                AssetDatabase.SaveAssets();
                sizes = Measure(avatarRoot, textures: true);
                Debug.Log(
                    $"[Avatar Compressor] Size check: made {dropped.Count} more textures smaller after the build; "
                        + $"download {MiB(sizes.Download)}, uncompressed {MiB(sizes.Uncompressed)}.",
                    avatarRoot
                );
            }

            foreach (var pair in dropped)
            {
                var c = pair.Key;
                int index = plan.Changes.FindIndex(ch => ch.Texture == c.Data.Texture);
                if (index >= 0)
                {
                    var change = plan.Changes[index];
                    change.To = FitHalving.Halve(change.To, pair.Value);
                    plan.Changes[index] = change;
                }
                else
                {
                    plan.Changes.Add(new FitChange
                    {
                        Texture = c.Data.Texture,
                        From = startSizes[c],
                        To = FitHalving.Halve(startSizes[c], pair.Value),
                    });
                }
            }
            halved = dropped.Count;
            return sizes;
        }

        /// <summary>Times the check after the build makes more textures smaller and measures again.</summary>
        private const int MaxShrinkRounds = 3;

        private static bool CanDropTopMips(Texture2D texture, int levels) =>
            texture != null && levels > 0 && texture.mipmapCount > levels && HasFullMipChain(texture);

        private static bool HasFullMipChain(Texture2D texture)
        {
            int largest = Math.Max(texture.width, texture.height);
            int full = 1;
            while (largest > 1)
            {
                largest >>= 1;
                full++;
            }
            return texture.mipmapCount == full;
        }

        /// <summary>
        /// Halves a texture <paramref name="levels"/> times in place by dropping its largest
        /// mip levels: the remaining levels are already encoded at the smaller sizes, so
        /// materials and animations keep referencing the same texture.
        /// </summary>
        private static bool DropTopMips(Texture2D texture, int levels)
        {
            if (!CanDropTopMips(texture, levels))
                return false;

            using var serialized = new SerializedObject(texture);
            var readable = serialized.FindProperty("m_IsReadable");
            bool wasReadable = readable == null || readable.boolValue;
            try
            {
                var raw = texture.GetRawTextureData();
                int width = texture.width, height = texture.height;
                long offset = 0;
                for (int i = 0; i < levels; i++)
                {
                    offset += (long)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(width, height, texture.graphicsFormat);
                    width = Math.Max(1, width >> 1);
                    height = Math.Max(1, height >> 1);
                }
                if (offset <= 0 || offset >= raw.Length)
                    return false;

                var rest = new byte[raw.Length - offset];
                Buffer.BlockCopy(raw, (int)offset, rest, 0, rest.Length);
                if (!wasReadable)
                {
                    readable.boolValue = true;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                if (!texture.Reinitialize(width, height, texture.format, true))
                    return false;
                texture.LoadRawTextureData(rest);
                texture.Apply(false, false);
                EditorUtility.SetDirty(texture);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Avatar Compressor] Couldn't make '{texture.name}' smaller after the build: {ex.Message}");
                return false;
            }
            finally
            {
                if (!wasReadable)
                {
                    serialized.Update();
                    readable.boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
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
        /// ratio for all sizes would miss. Only textures that keep their imported format are
        /// predicted from their bytes. A texture written in another format compresses
        /// differently (imported ASTC 6x6 written as ASTC 4x4 compresses to about 0.48 of its
        /// size, its import to about 0.27), so a sample of it is encoded in the output format and
        /// measured instead (<see cref="SampledDownloadRatio"/>).
        /// </summary>
        /// <param name="perTexture">Filled with each texture's predicted download, for the log.</param>
        private static long PredictedTextureDownload(
            List<TexturePreviewData> data,
            Dictionary<Texture2D, byte[]> rawCache,
            Dictionary<(Texture2D, TextureFormat, Vector2Int), double> sampleCache,
            List<(TexturePreviewData Data, long Download)> perTexture
        )
        {
            var sameFormat = data.Where(d => !ChangesFormat(d)).ToList();
            long total = 0;

            var stream = TextureStream(sameFormat, rawCache, predicted: true);
            double ratio = stream.Length > 0 ? (double)BundleSizeCalculator.CompressedSize(stream) / stream.Length : 0;
            if (_calibration == null && data.Any(ChangesFormat))
                _calibration = Calibrate(sameFormat, rawCache);
            foreach (var d in data)
            {
                long bytes = d.IsProcessed ? d.EstimatedMemory : d.OriginalMemory;
                double textureRatio = ratio;
                if (ChangesFormat(d))
                {
                    var key = (d.Texture, d.PredictedFormat.Value, d.RecommendedSize);
                    if (!sampleCache.TryGetValue(key, out double sampled))
                        sampleCache[key] = sampled = SampledDownloadRatio(d.Texture, d.PredictedFormat.Value, d.RecommendedSize);
                    textureRatio = Math.Min(1.0, sampled * (_calibration ?? 1.0));
                }
                long download = (long)(bytes * textureRatio);
                perTexture?.Add((d, download));
                total += download;
            }
            return total;
        }

        /// <summary>
        /// How well the texture compresses in the upload when written as <paramref name="format"/>
        /// at <paramref name="size"/>: tiles spread over the texture at that size are encoded the
        /// way the build encodes (best quality) and LZMA-compressed. The tiles cover empty and
        /// detailed areas alike, at the output's detail per pixel. Falls back to
        /// <see cref="FreshEncodeDownloadRatio"/> if the sample can't be made.
        /// </summary>
        private static double SampledDownloadRatio(Texture2D texture, TextureFormat format, Vector2Int size)
        {
            if (TrySampleRatio(texture, format, size, out double ratio, out string error))
                return ratio;
            Debug.LogWarning(
                $"[Avatar Compressor] Size plan: couldn't sample '{texture.name}' as {format} ({error}); "
                    + $"assuming it compresses to {FreshEncodeDownloadRatio:0.0} of its size."
            );
            return FreshEncodeDownloadRatio;
        }

        /// <summary>Textures that keep their format whose real bytes check the samples (<see cref="Calibrate"/>).</summary>
        private const int CalibrationTextures = 3;

        /// <summary>
        /// How far the samples are off: a sample is a small part of a texture, encoded on its own,
        /// so LZMA finds less to repeat than in the whole texture and the ratio comes out a bit
        /// high (about 0.6 MiB too much on a 5 MiB avatar). The largest textures that keep their
        /// format are sampled in that same format and compared with how their real bytes compress;
        /// the samples of the textures that change format are scaled by the difference. 1 (no
        /// change, on the safe side) when there is nothing to compare with.
        /// </summary>
        private static double Calibrate(List<TexturePreviewData> sameFormat, Dictionary<Texture2D, byte[]> rawCache)
        {
            var references = sameFormat
                .Where(d =>
                    UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsCompressedFormat(d.Texture.graphicsFormat)
                    && !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsCrunchFormat(d.Texture.format)
                )
                .Select(d => (Data: d, Size: d.IsProcessed ? d.RecommendedSize : new Vector2Int(d.Texture.width, d.Texture.height)))
                .Where(r => r.Size.x >= 128 && r.Size.y >= 128)
                .OrderByDescending(r => r.Data.IsProcessed ? r.Data.EstimatedMemory : r.Data.OriginalMemory)
                .Take(CalibrationTextures)
                .ToList();

            double real = 0, sampled = 0;
            foreach (var (d, size) in references)
            {
                try
                {
                    var stream = TextureStream(new List<TexturePreviewData> { d }, rawCache, predicted: true);
                    if (stream.Length == 0 || !TrySampleRatio(d.Texture, d.Texture.format, size, out double sampleRatio, out _))
                        continue;
                    double bytes = d.IsProcessed ? d.EstimatedMemory : d.OriginalMemory;
                    real += bytes * BundleSizeCalculator.CompressedSize(stream) / stream.Length;
                    sampled += bytes * sampleRatio;
                }
                catch (Exception)
                {
                    // one reference less
                }
            }
            if (sampled <= 0)
                return 1.0;
            return Math.Max(0.6, Math.Min(1.5, real / sampled));
        }

        private static bool TrySampleRatio(Texture2D texture, TextureFormat format, Vector2Int size, out double ratio, out string error)
        {
            ratio = 0;
            error = null;
            RenderTexture rt = null;
            Texture2D full = null, sample = null;
            var previous = RenderTexture.active;
            _sampled++;
            _sampleTime.Start();
            try
            {
                int width = Math.Max(1, size.x), height = Math.Max(1, size.y);
                bool linear = !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
                rt = RenderTexture.GetTemporary(
                    width,
                    height,
                    0,
                    RenderTextureFormat.ARGB32,
                    linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB
                );
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                full = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
                full.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                full.Apply(false);
                var pixels = full.GetPixels32();

                // Small textures are encoded whole; larger ones as a grid of tiles
                int tile = Math.Min(SampleTile, Math.Min(width, height) / SampleGrid);
                Color32[] samplePixels;
                int sampleWidth, sampleHeight;
                if (tile < 4 || width * height <= SampleGrid * SampleGrid * SampleTile * SampleTile)
                {
                    samplePixels = pixels;
                    sampleWidth = width;
                    sampleHeight = height;
                }
                else
                {
                    sampleWidth = sampleHeight = SampleGrid * tile;
                    samplePixels = new Color32[sampleWidth * sampleHeight];
                    for (int ty = 0; ty < SampleGrid; ty++)
                    {
                        for (int tx = 0; tx < SampleGrid; tx++)
                        {
                            // the middle of each grid cell
                            int sx = (int)((tx + 0.5) * width / SampleGrid) - tile / 2;
                            int sy = (int)((ty + 0.5) * height / SampleGrid) - tile / 2;
                            for (int y = 0; y < tile; y++)
                                Array.Copy(pixels, (sy + y) * width + sx, samplePixels, (ty * tile + y) * sampleWidth + tx * tile, tile);
                        }
                    }
                }

                sample = new Texture2D(sampleWidth, sampleHeight, TextureFormat.RGBA32, false, linear);
                sample.SetPixels32(samplePixels);
                sample.Apply(false);
                EditorUtility.CompressTexture(sample, format, TextureCompressionQuality.Best);
                var raw = sample.GetRawTextureData();
                if (raw.Length == 0)
                {
                    error = "the encoded sample is empty";
                    return false;
                }
                ratio = Math.Min(1.0, (double)BundleSizeCalculator.CompressedSize(raw) / raw.Length);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                _sampleTime.Stop();
                RenderTexture.active = previous;
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
                if (full != null)
                    UnityEngine.Object.DestroyImmediate(full);
                if (sample != null)
                    UnityEngine.Object.DestroyImmediate(sample);
            }
        }

        /// <summary>True when the compressor writes the texture in a different format than it is imported in.</summary>
        private static bool ChangesFormat(TexturePreviewData d) =>
            d.IsProcessed && d.PredictedFormat.HasValue && d.PredictedFormat.Value != d.Texture.format;

        /// <summary>One log entry with each texture's predicted size, largest download first.</summary>
        private static void LogTexturePredictions(List<(TexturePreviewData Data, long Download)> perTexture)
        {
            if (perTexture == null || perTexture.Count == 0)
                return;
            var lines = perTexture
                .OrderByDescending(p => p.Download)
                .Select(p =>
                {
                    var d = p.Data;
                    string output = d.IsProcessed ? (d.PredictedFormat?.ToString() ?? "kept") : "not processed";
                    var size = d.IsProcessed ? d.RecommendedSize : new Vector2Int(d.Texture.width, d.Texture.height);
                    long bytes = d.IsProcessed ? d.EstimatedMemory : d.OriginalMemory;
                    return $"  {d.Texture.name}: {d.Texture.format} -> {output} {size.x}x{size.y}, "
                        + $"{MiB(bytes)} memory, ~{MiB(p.Download)} download{(ChangesFormat(d) ? " (sampled in the new format)" : "")}";
                });
            Debug.Log(
                $"[Avatar Compressor] Size plan per texture ({_sampled} samples encoded in {_sampleTime.ElapsedMilliseconds / 1000.0:0.0} s"
                    + (_calibration.HasValue ? $", samples scaled by {_calibration.Value:0.00} from textures that keep their format" : "")
                    + "):\n"
                    + string.Join("\n", lines)
            );
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
