using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Records which material properties are touched by <em>any</em> animation in the avatar's
    /// animator hierarchy, plus every texture referenced by an animation object (PPtr) curve.
    /// The <see cref="AnimatedProperties"/> list is handed to an <see cref="IUnusedSlotOptimizer"/>
    /// so a slot whose feature toggle is driven by animation is never cleared; the animated-texture
    /// set protects textures that ship with the avatar through an animation curve regardless of slot state.
    /// </summary>
    internal sealed class AnimationUsageMap
    {
        private const string MaterialBindingPrefix = "material.";

        private readonly HashSet<string> _animatedMaterialProperties;
        private readonly HashSet<Texture2D> _animatedTextures;

        /// <summary>
        /// Constructs a map from an explicit set of animated material property names
        /// (already stripped of the <c>material.</c> prefix) and optionally the textures
        /// referenced by animation object curves. Primarily intended for tests.
        /// </summary>
        public AnimationUsageMap(
            IEnumerable<string> animatedMaterialProperties,
            IEnumerable<Texture2D> animatedTextures = null
        )
        {
            _animatedMaterialProperties = new HashSet<string>();
            if (animatedMaterialProperties != null)
            {
                foreach (var prop in animatedMaterialProperties)
                {
                    if (!string.IsNullOrEmpty(prop))
                        _animatedMaterialProperties.Add(prop);
                }
            }

            _animatedTextures = new HashSet<Texture2D>();
            if (animatedTextures != null)
            {
                foreach (var texture in animatedTextures)
                {
                    if (texture != null)
                        _animatedTextures.Add(texture);
                }
            }
        }

        /// <summary>
        /// An empty map: nothing is animated. Detection still runs but never vetoes on animation.
        /// </summary>
        public static AnimationUsageMap Empty { get; } =
            new AnimationUsageMap(Array.Empty<string>());

        /// <summary>
        /// The distinct animated material property names (without the <c>material.</c> prefix).
        /// Passed to external optimizers (e.g. lilToon) that take an animated-property list.
        /// </summary>
        public IReadOnlyCollection<string> AnimatedProperties => _animatedMaterialProperties;

        /// <summary>
        /// Returns true if the given texture is referenced by an animation object (PPtr) curve
        /// anywhere in the avatar.
        /// </summary>
        public bool IsTextureAnimated(Texture2D texture)
        {
            return texture != null && _animatedTextures.Contains(texture);
        }

        /// <summary>
        /// Builds the map from the avatar's animator hierarchy.
        /// Returns <c>null</c> if scanning fails — callers treat a <c>null</c> map as
        /// "cannot prove anything unused" and skip detection.
        /// </summary>
        public static AnimationUsageMap Build(GameObject avatarRoot)
        {
            if (avatarRoot == null)
                return null;

            var properties = new HashSet<string>();
            var textures = new HashSet<Texture2D>();

            try
            {
                var clips = new HashSet<AnimationClip>();

                // 1. Scan Animators
                var animators = avatarRoot.GetComponentsInChildren<Animator>(true);
                foreach (var animator in animators)
                {
                    if (animator != null && animator.runtimeAnimatorController != null)
                    {
                        foreach (var clip in MaterialCollector.GetAllAnimationClips(animator.runtimeAnimatorController))
                        {
                            if (clip != null)
                                clips.Add(clip);
                        }
                    }
                }

                // 2. Scan VRCAvatarDescriptor controllers
                var descriptors = avatarRoot.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && (c.GetType().Name == "VRCAvatarDescriptor" || c is VRC.SDKBase.VRC_AvatarDescriptor));
                foreach (var desc in descriptors)
                {
                    MaterialCollector.CollectClipsFromAvatarDescriptor(desc, clips);
                }

                // 3. Scan legacy Animation components
                var legacyAnims = avatarRoot.GetComponentsInChildren<Animation>(true);
                foreach (var anim in legacyAnims)
                {
                    if (anim != null)
                    {
                        foreach (AnimationState state in anim)
                        {
                            if (state.clip != null)
                                clips.Add(state.clip);
                        }
                    }
                }

                foreach (var clip in clips)
                {
                    if (clip == null)
                        continue;

                    CollectMaterialProperties(AnimationUtility.GetCurveBindings(clip), properties);
                    CollectFromObjectCurves(clip, properties, textures);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Avatar Compressor] Failed to scan animations for unused-slot detection: {ex.Message}. "
                        + "Unused-slot detection will be skipped (all slots treated as used)."
                );
                return null;
            }

            return new AnimationUsageMap(properties, textures);
        }

        private static void CollectFromObjectCurves(
            AnimationClip clip,
            HashSet<string> properties,
            HashSet<Texture2D> textures
        )
        {
            var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in bindings)
            {
                CollectMaterialProperty(binding, properties);

                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null)
                    continue;

                foreach (var keyframe in keyframes)
                {
                    if (keyframe.value is Texture2D texture && texture != null)
                        textures.Add(texture);
                }
            }
        }

        private static void CollectMaterialProperties(
            IEnumerable<EditorCurveBinding> bindings,
            HashSet<string> properties
        )
        {
            foreach (var binding in bindings)
                CollectMaterialProperty(binding, properties);
        }

        private static void CollectMaterialProperty(
            EditorCurveBinding binding,
            HashSet<string> properties
        )
        {
            string name = binding.propertyName;
            if (string.IsNullOrEmpty(name) || !name.StartsWith(MaterialBindingPrefix))
                return;

            string prop = name.Substring(MaterialBindingPrefix.Length);

            // Strip vector/color component suffixes (e.g. "_Color.r" -> "_Color")
            int dot = prop.IndexOf('.');
            if (dot > 0)
                prop = prop.Substring(0, dot);

            if (prop.Length > 0)
                properties.Add(prop);
        }
    }
}
