#nullable enable
#if ENABLE_VRCFURY || ENABLE_VRCHAT_BASE || VRC_SDK_VRCSDK3

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor.texture;
using dev.limitex.avatar.compressor.editor.texture.integrations;

namespace dev.limitex.avatar.compressor.editor
{
    /// <summary>
    /// VRChat avatar preprocess hook that compresses textures non-destructively at build/upload time.
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

            // Create service and compress textures non-destructively
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

            // Update animation curves with replaced materials and textures (cloning any asset clips to preserve project files)
            UpdateAnimationReferences(avatarRoot, processedTextures, clonedMaterials, saveToDisk ? tempFolder : null);

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
            Dictionary<Material, Material> clonedMaterials,
            string? tempFolder
        )
        {
            if (avatarRoot == null) return;
            if ((processedTextures == null || processedTextures.Count == 0) &&
                (clonedMaterials == null || clonedMaterials.Count == 0))
                return;

            try
            {
                // Find all Animators
                var animators = avatarRoot.GetComponentsInChildren<Animator>(true);
                foreach (var anim in animators)
                {
                    if (anim != null && anim.runtimeAnimatorController != null)
                    {
                        var updatedController = ProcessControllerClips(anim.runtimeAnimatorController, processedTextures, clonedMaterials, tempFolder);
                        if (updatedController != null && updatedController != anim.runtimeAnimatorController)
                        {
                            anim.runtimeAnimatorController = updatedController;
                        }
                    }
                }

                // Find all VRCAvatarDescriptor controllers
                var descriptors = avatarRoot.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && (c.GetType().Name == "VRCAvatarDescriptor" || c is VRC.SDKBase.VRC_AvatarDescriptor));
                foreach (var desc in descriptors)
                {
                    UpdateDescriptorControllers(desc, processedTextures, clonedMaterials, tempFolder);
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

        private static RuntimeAnimatorController? ProcessControllerClips(
            RuntimeAnimatorController controller,
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials,
            string? tempFolder
        )
        {
            if (controller == null) return null;

            var clips = MaterialCollector.GetAllAnimationClips(controller);
            var clipsToRemap = new Dictionary<AnimationClip, AnimationClip>();

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                if (HasMaterialOrTextureReferences(clip, processedTextures, clonedMaterials))
                {
                    // Clone the clip so project asset .anim files on disk are NEVER mutated
                    bool isAsset = !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(clip));
                    AnimationClip workClip;

                    if (isAsset)
                    {
                        workClip = UnityEngine.Object.Instantiate(clip);
                        workClip.name = clip.name + "_Compressed";
                        if (!string.IsNullOrEmpty(tempFolder))
                        {
                            AssetDatabase.CreateAsset(workClip, $"{tempFolder}/Clip_{Guid.NewGuid():N}.anim");
                        }
                    }
                    else
                    {
                        workClip = clip;
                    }

                    RemapClipCurves(workClip, processedTextures, clonedMaterials);
                    if (isAsset)
                    {
                        clipsToRemap[clip] = workClip;
                    }
                }
            }

            if (clipsToRemap.Count == 0)
                return controller;

            // If the controller is a project asset, clone it so the project .controller asset is NEVER mutated
            RuntimeAnimatorController targetController = controller;
            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller)))
            {
                targetController = UnityEngine.Object.Instantiate(controller);
                targetController.name = controller.name + "_Compressed";
            }

            // Replace clips inside targetController
            if (targetController is AnimatorOverrideController overrideController)
            {
                foreach (var kvp in clipsToRemap)
                {
                    overrideController[kvp.Key] = kvp.Value;
                }
            }
            else if (targetController is AnimatorController animatorController)
            {
                foreach (var kvp in clipsToRemap)
                {
                    foreach (var layer in animatorController.layers)
                    {
                        ReplaceClipInStateMachine(layer.stateMachine, kvp.Key, kvp.Value);
                    }
                }
            }

            return targetController;
        }

        private static bool HasMaterialOrTextureReferences(
            AnimationClip clip,
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials
        )
        {
            var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in bindings)
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null) continue;

                foreach (var kf in keyframes)
                {
                    if (kf.value is Material m && clonedMaterials.ContainsKey(m))
                        return true;
                    if (kf.value is Texture2D t && processedTextures.ContainsKey(t))
                        return true;
                }
            }
            return false;
        }

        private static void RemapClipCurves(
            AnimationClip clip,
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials
        )
        {
            var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in bindings)
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null || keyframes.Length == 0) continue;

                bool modified = false;
                for (int i = 0; i < keyframes.Length; i++)
                {
                    if (keyframes[i].value is Material m && clonedMaterials.TryGetValue(m, out var clonedMat))
                    {
                        keyframes[i].value = clonedMat;
                        modified = true;
                    }
                    else if (keyframes[i].value is Texture2D t && processedTextures.TryGetValue(t, out var compressedTex))
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

        private static void ReplaceClipInStateMachine(AnimatorStateMachine sm, AnimationClip oldClip, AnimationClip newClip)
        {
            if (sm == null) return;
            foreach (var s in sm.states)
            {
                if (s.state != null)
                {
                    if (s.state.motion == oldClip)
                        s.state.motion = newClip;
                    else if (s.state.motion is BlendTree bt)
                        ReplaceClipInBlendTree(bt, oldClip, newClip);
                }
            }
            foreach (var sub in sm.stateMachines)
            {
                ReplaceClipInStateMachine(sub.stateMachine, oldClip, newClip);
            }
        }

        private static void ReplaceClipInBlendTree(BlendTree bt, AnimationClip oldClip, AnimationClip newClip)
        {
            if (bt == null) return;
            var children = bt.children;
            bool changed = false;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion == oldClip)
                {
                    children[i].motion = newClip;
                    changed = true;
                }
                else if (children[i].motion is BlendTree childBt)
                {
                    ReplaceClipInBlendTree(childBt, oldClip, newClip);
                }
            }
            if (changed)
                bt.children = children;
        }

        private static void UpdateDescriptorControllers(
            Component descriptor,
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials,
            string? tempFolder
        )
        {
            if (descriptor == null) return;
            var type = descriptor.GetType();
            RemapLayersInMember(descriptor, type.GetField("baseAnimationLayers"), processedTextures, clonedMaterials, tempFolder);
            RemapLayersInMember(descriptor, type.GetField("specialAnimationLayers"), processedTextures, clonedMaterials, tempFolder);
        }

        private static void RemapLayersInMember(
            object descriptor,
            System.Reflection.FieldInfo? field,
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials,
            string? tempFolder
        )
        {
            if (field == null) return;
            var val = field.GetValue(descriptor);
            if (val is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    var ctrlField = item.GetType().GetField("animatorController");
                    if (ctrlField != null)
                    {
                        var ctrl = ctrlField.GetValue(item) as RuntimeAnimatorController;
                        if (ctrl != null)
                        {
                            var updated = ProcessControllerClips(ctrl, processedTextures, clonedMaterials, tempFolder);
                            if (updated != null && updated != ctrl)
                            {
                                ctrlField.SetValue(item, updated);
                            }
                        }
                    }
                }
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

            if (EditorUtility.DisplayDialog(
                "Avatar Compressor",
                $"Create a non-destructive compressed copy of '{selected.name}'?\n\nThis duplicates your avatar in the scene and applies compression only to the copy. Your original avatar and all original project assets remain 100% untouched.",
                "Create Copy & Compress",
                "Cancel"))
            {
                var clone = UnityEngine.Object.Instantiate(selected, selected.transform.parent);
                clone.name = selected.name + "_Compressed";
                Undo.RegisterCreatedObjectUndo(clone, "Compress Avatar Copy");
                Process(clone);
                Selection.activeGameObject = clone;
                EditorUtility.DisplayDialog(
                    "Avatar Compressor",
                    $"Texture compression complete!\n\nA compressed avatar copy '{clone.name}' has been created in your scene.\nYour original avatar and source assets were NOT modified.",
                    "OK"
                );
            }
        }
    }
}
#endif
