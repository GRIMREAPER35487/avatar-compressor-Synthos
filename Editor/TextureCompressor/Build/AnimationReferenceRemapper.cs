using System.Collections.Generic;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Points animation object curves (material swaps, texture swaps) at the cloned materials
    /// and compressed textures, without ever modifying a project asset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses copy-on-write: an asset is only copied when something inside it must change, and
    /// only if the build does not already own it (see <see cref="BuildAssetStore.IsBuildOwned"/>).
    /// When VRCFury has run, the playable-layer controllers are already VRCFury build copies and
    /// are edited in place; their clips may still be the user's originals and are copied.
    /// </para>
    /// <para>
    /// Without this step, a material swap animation would put the original, uncompressed
    /// material back on the avatar and both versions of its textures would ship in the upload.
    /// </para>
    /// </remarks>
    internal sealed class AnimationReferenceRemapper
    {
        private readonly Dictionary<Texture2D, Texture2D> _textures;
        private readonly Dictionary<Material, Material> _materials;
        private readonly BuildAssetStore _store;

        private readonly Dictionary<Motion, Motion> _motionCache = new Dictionary<Motion, Motion>();
        private readonly Dictionary<
            RuntimeAnimatorController,
            RuntimeAnimatorController
        > _controllerCache = new Dictionary<RuntimeAnimatorController, RuntimeAnimatorController>();

        public AnimationReferenceRemapper(
            Dictionary<Texture2D, Texture2D> processedTextures,
            Dictionary<Material, Material> clonedMaterials,
            BuildAssetStore store
        )
        {
            _textures = processedTextures;
            _materials = clonedMaterials;
            _store = store;
        }

        /// <summary>
        /// Rewrites every animation on the avatar that references a replaced material or texture.
        /// </summary>
        public void Apply(GameObject avatarRoot)
        {
            if (avatarRoot == null || (_textures.Count == 0 && _materials.Count == 0))
                return;

            foreach (
                var descriptor in avatarRoot.GetComponentsInChildren<VRCAvatarDescriptor>(true)
            )
            {
                descriptor.baseAnimationLayers = RemapLayers(descriptor.baseAnimationLayers);
                descriptor.specialAnimationLayers = RemapLayers(descriptor.specialAnimationLayers);
            }

            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                var remapped = RemapController(controller);
                if (remapped != controller)
                    animator.runtimeAnimatorController = remapped;
            }

            foreach (var animation in avatarRoot.GetComponentsInChildren<Animation>(true))
                RemapLegacyAnimation(animation);
        }

        private VRCAvatarDescriptor.CustomAnimLayer[] RemapLayers(
            VRCAvatarDescriptor.CustomAnimLayer[] layers
        )
        {
            if (layers == null)
                return null;

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].isDefault || layers[i].animatorController == null)
                    continue;
                layers[i].animatorController = RemapController(layers[i].animatorController);
            }

            return layers;
        }

        private RuntimeAnimatorController RemapController(RuntimeAnimatorController controller)
        {
            if (controller == null)
                return null;
            if (_controllerCache.TryGetValue(controller, out var cached))
                return cached;

            var result = controller;
            if (NeedsRemap(controller))
            {
                switch (controller)
                {
                    case AnimatorOverrideController overrideController:
                        result = RemapOverrideController(overrideController);
                        break;
                    case AnimatorController animatorController:
                        result = RemapAnimatorController(animatorController);
                        break;
                    default:
                        Debug.LogWarning(
                            $"[Avatar Compressor] Unsupported controller type '{controller.GetType().Name}' "
                                + $"on '{controller.name}'. Its animations keep the original materials.",
                            controller
                        );
                        break;
                }
            }

            _controllerCache[controller] = result;
            return result;
        }

        private RuntimeAnimatorController RemapAnimatorController(AnimatorController controller)
        {
            var target = GetWritable(controller);
            if (target == null)
                return controller;

            var layers = target.layers;
            bool layersChanged = false;

            foreach (var layer in layers)
                RemapStateMachine(layer.stateMachine);

            // Synced layers store their per-state motions on the layer itself.
            for (int i = 0; i < layers.Length; i++)
            {
                int source = layers[i].syncedLayerIndex;
                if (source < 0 || source >= layers.Length)
                    continue;

                foreach (var state in GetAllStates(layers[source].stateMachine))
                {
                    var motion = layers[i].GetOverrideMotion(state);
                    var remapped = RemapMotion(motion);
                    if (remapped != motion)
                    {
                        layers[i].SetOverrideMotion(state, remapped);
                        layersChanged = true;
                    }
                }
            }

            if (layersChanged)
                target.layers = layers;

            EditorUtility.SetDirty(target);
            return target;
        }

        private RuntimeAnimatorController RemapOverrideController(
            AnimatorOverrideController controller
        )
        {
            var target = GetWritable(controller);
            if (target == null)
                return controller;

            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(
                target.overridesCount
            );
            target.GetOverrides(overrides);

            bool changed = false;
            for (int i = 0; i < overrides.Count; i++)
            {
                var effective = overrides[i].Value != null ? overrides[i].Value : overrides[i].Key;
                if (effective == null)
                    continue;

                if (RemapMotion(effective) is AnimationClip remapped && remapped != effective)
                {
                    overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(
                        overrides[i].Key,
                        remapped
                    );
                    changed = true;
                }
            }

            if (changed)
            {
                target.ApplyOverrides(overrides);
                EditorUtility.SetDirty(target);
            }

            return target;
        }

        private void RemapStateMachine(AnimatorStateMachine stateMachine)
        {
            foreach (var state in GetAllStates(stateMachine))
            {
                var remapped = RemapMotion(state.motion);
                if (remapped != state.motion)
                    state.motion = remapped;
            }
        }

        private static IEnumerable<AnimatorState> GetAllStates(AnimatorStateMachine stateMachine)
        {
            if (stateMachine == null)
                yield break;

            foreach (var child in stateMachine.states)
            {
                if (child.state != null)
                    yield return child.state;
            }

            foreach (var child in stateMachine.stateMachines)
            {
                foreach (var state in GetAllStates(child.stateMachine))
                    yield return state;
            }
        }

        private Motion RemapMotion(Motion motion)
        {
            if (motion == null)
                return null;
            if (_motionCache.TryGetValue(motion, out var cached))
                return cached;

            Motion result = motion;
            switch (motion)
            {
                case AnimationClip clip when HasReplacedReferences(clip):
                    result = RemapClip(clip);
                    break;
                case BlendTree blendTree:
                    result = RemapBlendTree(blendTree);
                    break;
            }

            _motionCache[motion] = result;
            return result;
        }

        private AnimationClip RemapClip(AnimationClip clip)
        {
            var target = clip;
            if (!_store.IsBuildOwned(clip))
            {
                target = Object.Instantiate(clip);
                target.name = clip.name;
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(target))
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(target, binding);
                if (keyframes == null)
                    continue;

                bool modified = false;
                for (int i = 0; i < keyframes.Length; i++)
                {
                    var replacement = GetReplacement(keyframes[i].value);
                    if (replacement != null)
                    {
                        keyframes[i].value = replacement;
                        modified = true;
                    }
                }

                if (modified)
                    AnimationUtility.SetObjectReferenceCurve(target, binding, keyframes);
            }

            _store.Persist(target);
            EditorUtility.SetDirty(target);
            return target;
        }

        private Motion RemapBlendTree(BlendTree blendTree)
        {
            var children = blendTree.children;
            bool changed = false;

            for (int i = 0; i < children.Length; i++)
            {
                var remapped = RemapMotion(children[i].motion);
                if (remapped != children[i].motion)
                {
                    children[i].motion = remapped;
                    changed = true;
                }
            }

            if (!changed)
                return blendTree;

            var target = blendTree;
            if (!_store.IsBuildOwned(blendTree))
            {
                target = Object.Instantiate(blendTree);
                target.name = blendTree.name;
            }

            target.children = children;
            _store.Persist(target);
            EditorUtility.SetDirty(target);
            return target;
        }

        private void RemapLegacyAnimation(Animation animation)
        {
            var states = new List<(string Name, AnimationClip Clip)>();
            foreach (AnimationState state in animation)
            {
                if (state != null && state.clip != null)
                    states.Add((state.name, state.clip));
            }

            foreach (var (name, clip) in states)
            {
                if (!(RemapMotion(clip) is AnimationClip remapped) || remapped == clip)
                    continue;

                bool wasDefault = animation.clip == clip;
                animation.RemoveClip(name);
                animation.AddClip(remapped, name);
                if (wasDefault)
                    animation.clip = remapped;
            }
        }

        private T GetWritable<T>(T controller)
            where T : RuntimeAnimatorController
        {
            if (_store.IsBuildOwned(controller))
                return controller;

            var copy = _store.CopyAsset(controller);
            if (copy == null)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Could not copy controller '{controller.name}'. "
                        + "Its animations keep the original materials and textures.",
                    controller
                );
            }

            return copy;
        }

        private bool NeedsRemap(RuntimeAnimatorController controller)
        {
            foreach (var clip in AvatarAnimators.GetClips(controller))
            {
                if (HasReplacedReferences(clip))
                    return true;
            }

            return false;
        }

        private bool HasReplacedReferences(AnimationClip clip)
        {
            if (clip == null)
                return false;

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null)
                    continue;

                foreach (var keyframe in keyframes)
                {
                    if (GetReplacement(keyframe.value) != null)
                        return true;
                }
            }

            return false;
        }

        private Object GetReplacement(Object value)
        {
            if (value is Material material && _materials.TryGetValue(material, out var clone))
                return clone;
            if (value is Texture2D texture && _textures.TryGetValue(texture, out var compressed))
                return compressed;
            return null;
        }
    }
}
