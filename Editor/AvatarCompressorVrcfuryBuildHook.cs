#nullable enable
#if ENABLE_VRCFURY || ENABLE_VRCHAT_BASE || VRC_SDK_VRCSDK3

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor.texture;
using dev.limitex.avatar.compressor.editor.texture.integrations;

namespace dev.limitex.avatar.compressor.editor
{
    /// <summary>
    /// VRChat avatar preprocess hook that compresses textures at build/upload time.
    /// Runs after VRCFury (callbackOrder 10000, while VRCFury runs at -10000),
    /// ensuring all modular clothing, props, and animations are already merged.
    /// </summary>
    public class AvatarCompressorVrcfuryBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        // VRCFury runs at callbackOrder -10000.
        // We run at 10000 so we execute after VRCFury has merged everything, but before editor-only components are deleted.
        public int callbackOrder => 10000;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            try
            {
                Process(avatarRoot);
            }
            catch (Exception e)
            {
                Debug.LogException(e, avatarRoot);
            }
            return true;
        }

        public static void Process(GameObject avatarRoot)
        {
            if (avatarRoot == null) return;

            var components = avatarRoot.GetComponentsInChildren<TextureCompressor>(true);
            if (components.Length == 0)
                return;

            ValidateComponents(components);
            var config = components[0];

            // Collect all material references across the avatar
            var materialReferences = MaterialCollector.CollectAll(avatarRoot);
            if (materialReferences == null || materialReferences.Count == 0)
            {
                CleanupComponents(components);
                return;
            }

            // Probe the lilToon integrations first (cheap reflection lookups)
            LilToonUnusedSlotOptimizer? unusedSlotOptimizer = null;
            LilToonTextureBaker? lilToonBaker = null;
            AnimationUsageMap? animationUsageMap = null;

            if (config.DetectUnusedTextures)
                unusedSlotOptimizer = new LilToonUnusedSlotOptimizer();
            if (config.BakeLilToonTextures)
                lilToonBaker = new LilToonTextureBaker();

            if (unusedSlotOptimizer?.IsAvailable == true || lilToonBaker?.IsAvailable == true)
                animationUsageMap = AnimationUsageMap.Build(avatarRoot);

            // Create service and compress textures
            var service = new TextureCompressorService(
                config,
                TextureCompressorPreferences.AnalysisBackend,
                TextureCompressorPreferences.ResizeBackend,
                animationUsageMap,
                unusedSlotOptimizer,
                lilToonBaker
            );

            var (processedTextures, clonedMaterials) = service.CompressWithMappings(
                materialReferences,
                AvatarCompressorPreferences.EnableLogging
            );

            // Only save to disk if we are building the actual asset bundle (VRC upload / build).
            // In Unity Play Mode, in-memory assets work directly without disk serialization.
            bool saveToDisk = !EditorApplication.isPlayingOrWillChangePlaymode;
            const string tempFolder = "Assets/AvatarCompressorTemp";

            if (saveToDisk && (processedTextures.Count > 0 || clonedMaterials.Count > 0))
            {
                if (!AssetDatabase.IsValidFolder(tempFolder))
                {
                    AssetDatabase.CreateFolder("Assets", "AvatarCompressorTemp");
                }

                foreach (var tex in processedTextures.Values)
                {
                    if (tex != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex)))
                    {
                        var path = $"{tempFolder}/Tex_{Guid.NewGuid():N}.asset";
                        AssetDatabase.CreateAsset(tex, path);
                    }
                }

                foreach (var mat in clonedMaterials.Values)
                {
                    if (mat != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mat)))
                    {
                        var path = $"{tempFolder}/Mat_{Guid.NewGuid():N}.mat";
                        AssetDatabase.CreateAsset(mat, path);
                    }
                }

                AssetDatabase.SaveAssets();

                EditorApplication.delayCall += () =>
                {
                    if (AssetDatabase.IsValidFolder(tempFolder))
                    {
                        AssetDatabase.DeleteAsset(tempFolder);
                    }
                };
            }

            // Update animation curves with replaced materials and textures
            UpdateAnimationReferences(avatarRoot, processedTextures, clonedMaterials);

            // Destroy orphaned bakes
            lilToonBaker?.DestroyOrphanedBakes(clonedMaterials.Values);

            // Cleanup components so they don't upload to VRChat
            CleanupComponents(components);
        }

        private static void ValidateComponents(TextureCompressor[] components)
        {
            if (components == null || components.Length == 0)
                return;

            if (components.Length > 1)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Multiple TextureCompressor components found ({components.Length}). "
                        + "Only the first component's settings will be used.",
                    components[0]
                );
            }
        }

        private static void UpdateAnimationReferences(
            GameObject avatarRoot,
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials
        )
        {
            if (avatarRoot == null) return;
            if ((processedTextures == null || processedTextures.Count == 0) &&
                (clonedMaterials == null || clonedMaterials.Count == 0))
                return;

            try
            {
                var clips = new HashSet<AnimationClip>();

                var animators = avatarRoot.GetComponentsInChildren<Animator>(true);
                foreach (var anim in animators)
                {
                    if (anim != null && anim.runtimeAnimatorController != null)
                    {
                        foreach (var c in MaterialCollector.GetAllAnimationClips(anim.runtimeAnimatorController))
                        {
                            if (c != null) clips.Add(c);
                        }
                    }
                }

                var descriptors = avatarRoot.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && (c.GetType().Name == "VRCAvatarDescriptor" || c is VRC.SDKBase.VRC_AvatarDescriptor));
                foreach (var desc in descriptors)
                {
                    MaterialCollector.CollectClipsFromAvatarDescriptor(desc, clips);
                }

                var legacyAnims = avatarRoot.GetComponentsInChildren<Animation>(true);
                foreach (var anim in legacyAnims)
                {
                    if (anim != null)
                    {
                        foreach (AnimationState state in anim)
                        {
                            if (state.clip != null) clips.Add(state.clip);
                        }
                    }
                }

                foreach (var clip in clips)
                {
                    if (clip == null) continue;

                    var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                    foreach (var binding in bindings)
                    {
                        var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (keyframes == null || keyframes.Length == 0) continue;

                        bool modified = false;
                        for (int i = 0; i < keyframes.Length; i++)
                        {
                            if (keyframes[i].value is Material m && clonedMaterials != null && clonedMaterials.TryGetValue(m, out var clonedMat))
                            {
                                keyframes[i].value = clonedMat;
                                modified = true;
                            }
                            else if (keyframes[i].value is Texture2D t && processedTextures != null && processedTextures.TryGetValue(t, out var compressedTex))
                            {
                                keyframes[i].value = compressedTex;
                                modified = true;
                            }
                        }

                        if (modified)
                        {
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Failed to update animation references: {ex.Message}. "
                        + "Some animation curves may reference original materials/textures."
                );
            }
        }

        private static void CleanupComponents(TextureCompressor[] components)
        {
            if (components == null) return;
            foreach (var component in components)
            {
                if (component != null)
                {
                    ComponentUtils.SafeDestroy(component);
                }
            }
        }

        [MenuItem("Tools/Avatar Compressor/Compress Selected Avatar", false, 100)]
        public static void CompressSelectedAvatar()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
            {
                EditorUtility.DisplayDialog("Avatar Compressor", "Please select an avatar GameObject in the hierarchy.", "OK");
                return;
            }

            var comp = selected.GetComponentInChildren<TextureCompressor>(true);
            if (comp == null)
            {
                EditorUtility.DisplayDialog("Avatar Compressor", "The selected GameObject (or its children) does not have a TextureCompressor component.", "OK");
                return;
            }

            if (EditorUtility.DisplayDialog("Avatar Compressor", $"Compress textures for '{selected.name}'?\n\nNote: This will clone materials and compress textures on the instance in scene.", "Compress", "Cancel"))
            {
                Process(selected);
                EditorUtility.DisplayDialog("Avatar Compressor", "Texture compression complete!", "OK");
            }
        }
    }
}
#endif
