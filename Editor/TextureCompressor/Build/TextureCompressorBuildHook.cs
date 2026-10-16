using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dev.limitex.avatar.compressor.editor.size;
using dev.limitex.avatar.compressor.editor.texture.integrations;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// VRChat SDK avatar build hook that compresses avatar textures, including those referenced
    /// by animations, on the build copy of the avatar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs in two steps. At callback order -1025, before the SDK strips <c>IEditorOnly</c>
    /// components (-1024), it reads the <see cref="TextureCompressor"/> settings and removes the
    /// component. The textures are compressed at <see cref="TextureCompressorCompressHook"/>'s
    /// order 101: after VRCFury (-10000), so merged clothing, props and controllers are
    /// included, and after Poiyomi locks the avatar's materials on upload (100), so textures the
    /// lock strips (features that are turned off) are never compressed, and the lock applies to
    /// the project's materials as it does without the compressor rather than to its copies.
    /// </para>
    /// <para>
    /// The SDK, VRCFury Play Mode and VRCFury's "Build an Editor Test Copy" all invoke this hook
    /// on a copy of the avatar, so the scene avatar and project assets are never modified.
    /// </para>
    /// </remarks>
    internal sealed class TextureCompressorBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        internal const int CallbackOrder = -1025;

        public int callbackOrder => CallbackOrder;

        // Settings read at -1025 for the compression at 101, per avatar copy being built
        private static readonly Dictionary<GameObject, string> PendingSettings =
            new Dictionary<GameObject, string>();

        /// <summary>
        /// Set while the preview runs the build steps to estimate the upload size: the avatar
        /// is left uncompressed (the SDK still removes the component).
        /// </summary>
        internal static bool SkipCompression;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                CaptureSettings(avatarGameObject);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, avatarGameObject);
                Debug.LogError(
                    "[Avatar Compressor] Texture compression failed; the build was stopped so a "
                        + "partially processed avatar is not uploaded. See the exception above.",
                    avatarGameObject
                );
                return false;
            }
        }

        /// <summary>
        /// Reads the settings of the avatar's <see cref="TextureCompressor"/> and removes the
        /// component, before the SDK strips it. <see cref="CompressCaptured"/> uses them.
        /// </summary>
        internal static void CaptureSettings(GameObject avatarRoot)
        {
            foreach (var stale in PendingSettings.Keys.Where(k => k == null).ToList())
                PendingSettings.Remove(stale);
            if (avatarRoot == null || SkipCompression)
                return;

            var components = avatarRoot.GetComponentsInChildren<TextureCompressor>(true);
            if (components.Length == 0)
                return;

            ValidateComponents(avatarRoot, components);
            PendingSettings[avatarRoot] = EditorJsonUtility.ToJson(components[0]);

            foreach (var component in components)
                ComponentUtils.SafeDestroy(component);
        }

        /// <summary>
        /// Compresses the avatar with the settings <see cref="CaptureSettings"/> read from it.
        /// </summary>
        internal static void CompressCaptured(GameObject avatarRoot)
        {
            if (avatarRoot == null || !PendingSettings.TryGetValue(avatarRoot, out string json))
                return;
            PendingSettings.Remove(avatarRoot);

            // The settings on a hidden component outside the avatar (TextureCompressor is a component)
            var holder = new GameObject("AvatarCompressorSettings") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var config = holder.AddComponent<TextureCompressor>();
                EditorJsonUtility.FromJsonOverwrite(json, config);
                Compress(avatarRoot, config);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// Both steps at once: compresses the avatar with its component's settings and removes
        /// the component.
        /// </summary>
        internal static void Process(GameObject avatarRoot)
        {
            CaptureSettings(avatarRoot);
            CompressCaptured(avatarRoot);
        }

        private static void Compress(GameObject avatarRoot, TextureCompressor config)
        {
            BuildAssetStore.CleanupUnused();

            var sizePlan = config.KeepUnderSizeLimit ? PlanSizeLimit(avatarRoot, config) : null;

            using (ReplacedObjectRegistry.BeginScope())
            {
                var store = new BuildAssetStore(avatarRoot.name.Replace("(Clone)", ""));
                var materialReferences = MaterialCollector.CollectAll(avatarRoot);

                // Probe the lilToon integrations first (cheap reflection lookups) and only scan the
                // avatar's animations when at least one integration can actually be used, so builds
                // without lilToon never pay for a usage map they would discard. Each service
                // re-validates the full gate (config + map + integration availability).
                LilToonUnusedSlotOptimizer unusedSlotOptimizer = null;
                LilToonTextureBaker lilToonBaker = null;
                AnimationUsageMap animationUsageMap = null;

                if (config.DetectUnusedTextures)
                    unusedSlotOptimizer = new LilToonUnusedSlotOptimizer();
                if (config.BakeLilToonTextures)
                    lilToonBaker = new LilToonTextureBaker();

                if (unusedSlotOptimizer?.IsAvailable == true || lilToonBaker?.IsAvailable == true)
                    animationUsageMap = AnimationUsageMap.Build(avatarRoot);

                // Frozen textures that apply to slot swaps pass their settings to the textures
                // now in their slots (an outfit's swapped-in material, a toggle's material).
                var followedBy = new Dictionary<string, string>();
                var frozenTextures = FrozenSlotResolver.Expand(
                    config.FrozenTextures,
                    avatarRoot,
                    followedBy
                );
                if (AvatarCompressorPreferences.EnableLogging)
                {
                    foreach (var pair in followedBy)
                    {
                        Debug.Log(
                            $"[Texture Compressor] {Path.GetFileName(AssetDatabase.GUIDToAssetPath(pair.Key))}: "
                                + $"frozen settings of {Path.GetFileName(AssetDatabase.GUIDToAssetPath(pair.Value))} (same slot)"
                        );
                    }
                }

                var service = new TextureCompressorService(
                    config,
                    TextureCompressorPreferences.AnalysisBackend,
                    TextureCompressorPreferences.ResizeBackend,
                    animationUsageMap,
                    unusedSlotOptimizer,
                    lilToonBaker,
                    TextureCompressorPreferences.UseSourceFiles
                        ? new TextureSourceCache(BuildAssetStore.RootFolder + "/_SourceImport")
                        : null,
                    frozenTextures
                );
                var (processedTextures, clonedMaterials) = service.CompressWithMappings(
                    materialReferences,
                    AvatarCompressorPreferences.EnableLogging
                );

                // Save textures, then the materials that use them, so every generated object
                // survives the SDK saving the avatar as a prefab. This covers lilToon bakes that
                // were not compressed as well as compressed textures.
                var liveTextures = LilToonTextureBaker.CollectLiveTextures(clonedMaterials.Values);
                foreach (var texture in liveTextures.Concat(processedTextures.Values))
                {
                    if (IsGeneratedTexture(texture))
                        MarkNonReadable(texture);
                    store.Persist(texture);
                }
                if (config.StripUnusedMaterialProperties)
                    StripUnusedProperties(clonedMaterials.Values);
                foreach (var material in clonedMaterials.Values)
                    store.Persist(material);

                new AnimationReferenceRemapper(processedTextures, clonedMaterials, store).Apply(
                    avatarRoot
                );

                // Must run after the remapping above: baked textures can be processedTextures keys.
                lilToonBaker?.DestroyOrphanedBakes(clonedMaterials.Values);

                AssetDatabase.SaveAssets();
            }

            // Measured by SizeCheckBuildHook once every other build step has run: later steps
            // still change the avatar (at this point it is about 1 MB larger than the upload)
            if (sizePlan != null)
                SizeCheckBuildHook.Pending[avatarRoot] = sizePlan;
        }

        /// <summary>
        /// Picks the textures to make smaller on the build's settings so the avatar is predicted
        /// to fit the mobile limits. Returns null when sizes can't be calculated for this build.
        /// </summary>
        private static AvatarSizeCheck.FitPlan PlanSizeLimit(GameObject avatarRoot, TextureCompressor config)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return null; // nothing is uploaded from Play Mode
            if (!AvatarSizeCheck.IsSupported(out string reason))
            {
                Debug.Log($"[Avatar Compressor] Keep under size limit: skipped, {reason}.");
                return null;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            AvatarSizeCheck.FitPlan plan;
            try
            {
                plan = AvatarSizeCheck.Plan(avatarRoot, config);
            }
            catch (Exception ex)
            {
                // A failed plan never stops the upload: compress with the settings as they are
                config.FitHalvings = null;
                Debug.LogWarning($"[Avatar Compressor] Keep under size limit: couldn't plan the sizes ({ex.Message}); compressing with your settings.");
                Debug.LogException(ex);
                return null;
            }
            string took = $"{timer.ElapsedMilliseconds / 1000.0:0.0} s";
            string changes = string.Join(
                "\n",
                plan.Changes.Select(c => $"  {c.Texture.name}: {c.From.x}x{c.From.y} -> {c.To.x}x{c.To.y}")
            );
            if (plan.CannotFit)
                Debug.LogWarning(
                    "[Avatar Compressor] The avatar is predicted to stay over the mobile size limit even with every "
                        + $"texture it could shrink made smaller ({took}); uploading anyway."
                        + (plan.Changes.Count > 0 ? "\n" + changes : ""),
                    avatarRoot
                );
            else if (plan.Changes.Count > 0)
                Debug.LogWarning(
                    $"[Avatar Compressor] Keep under size limit: made {plan.Changes.Count} textures smaller for this upload "
                        + $"to fit ({took}). Your project's textures are not changed.\n{changes}",
                    avatarRoot
                );
            else
                Debug.Log($"[Avatar Compressor] Keep under size limit: fits with your settings ({took}).");
            return plan;
        }

        private static void StripUnusedProperties(IEnumerable<Material> materials)
        {
            int total = 0;
            foreach (var material in materials.Distinct())
            {
                int removed = UnusedMaterialPropertyStripper.Strip(material);
                total += removed;
                if (removed > 0 && AvatarCompressorPreferences.EnableLogging)
                    Debug.Log(
                        $"[Texture Compressor] {material.name}: removed {removed} properties its shader doesn't use"
                    );
            }
            if (total > 0)
                Debug.Log(
                    $"[Texture Compressor] Removed {total} material properties the shaders don't use"
                );
        }

        /// <summary>
        /// True for textures this build generated (compressed or baked), which are registered
        /// as replacements and not yet saved as assets.
        /// </summary>
        private static bool IsGeneratedTexture(Texture2D texture)
        {
            return texture != null
                && !EditorUtility.IsPersistent(texture)
                && ReplacedObjectRegistry.Resolve(texture) != texture;
        }

        /// <summary>
        /// Clears Read/Write on a generated texture so the uploaded avatar doesn't keep a
        /// second, CPU-side copy of it in memory. The pixel data stays in the editor object,
        /// so saving it as an asset still writes the full texture.
        /// </summary>
        private static void MarkNonReadable(Texture2D texture)
        {
            using var serializedTexture = new SerializedObject(texture);
            var readable = serializedTexture.FindProperty("m_IsReadable");
            if (readable == null || !readable.boolValue)
                return;

            readable.boolValue = false;
            serializedTexture.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ValidateComponents(
            GameObject avatarRoot,
            TextureCompressor[] components
        )
        {
            if (components.Length > 1)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Multiple TextureCompressor components found ({components.Length}). "
                        + "Only the first component's settings will be used.",
                    components[0]
                );
            }

            foreach (var component in components)
            {
                if (component != null && component.gameObject != avatarRoot)
                {
                    Debug.LogWarning(
                        $"[Avatar Compressor] Component on '{component.gameObject.name}' is not on the avatar root. "
                            + "It is recommended to place the component on the avatar root GameObject.",
                        component
                    );
                }
            }
        }

        /// <summary>
        /// Returns true if the transform is an avatar root (has a VRC avatar descriptor).
        /// </summary>
        internal static bool IsAvatarRoot(Transform transform)
        {
            return transform != null && transform.GetComponent<VRCAvatarDescriptor>() != null;
        }
    }

    /// <summary>
    /// Second step of <see cref="TextureCompressorBuildHook"/>: compresses the textures with the
    /// settings read at -1025, after Poiyomi locks the avatar's materials on upload (100).
    /// </summary>
    internal sealed class TextureCompressorCompressHook : IVRCSDKPreprocessAvatarCallback
    {
        internal const int CallbackOrder = 101;

        public int callbackOrder => CallbackOrder;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                TextureCompressorBuildHook.CompressCaptured(avatarGameObject);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, avatarGameObject);
                Debug.LogError(
                    "[Avatar Compressor] Texture compression failed; the build was stopped so a "
                        + "partially processed avatar is not uploaded. See the exception above.",
                    avatarGameObject
                );
                return false;
            }
        }
    }
}
