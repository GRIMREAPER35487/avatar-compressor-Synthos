using System;
using System.Linq;
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
    /// Runs at callback order -1025:
    /// after VRCFury (-10000), so merged clothing, props and controllers are included, and
    /// before the SDK strips <c>IEditorOnly</c> components (-1024), which would otherwise remove
    /// the <see cref="TextureCompressor"/> component before this hook sees it.
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

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                Process(avatarGameObject);
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

        internal static void Process(GameObject avatarRoot)
        {
            if (avatarRoot == null)
                return;

            var components = avatarRoot.GetComponentsInChildren<TextureCompressor>(true);
            if (components.Length == 0)
                return;

            ValidateComponents(avatarRoot, components);
            var config = components[0];

            BuildAssetStore.CleanupUnused();

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

                var service = new TextureCompressorService(
                    config,
                    TextureCompressorPreferences.AnalysisBackend,
                    TextureCompressorPreferences.ResizeBackend,
                    animationUsageMap,
                    unusedSlotOptimizer,
                    lilToonBaker,
                    TextureCompressorPreferences.UseSourceFiles
                        ? new TextureSourceCache(BuildAssetStore.RootFolder + "/_SourceImport")
                        : null
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
                foreach (var material in clonedMaterials.Values)
                    store.Persist(material);

                new AnimationReferenceRemapper(processedTextures, clonedMaterials, store).Apply(
                    avatarRoot
                );

                // Must run after the remapping above: baked textures can be processedTextures keys.
                lilToonBaker?.DestroyOrphanedBakes(clonedMaterials.Values);

                AssetDatabase.SaveAssets();
            }

            foreach (var component in components)
                ComponentUtils.SafeDestroy(component);
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
}
