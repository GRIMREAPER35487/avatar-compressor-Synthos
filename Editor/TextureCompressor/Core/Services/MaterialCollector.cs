using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using dev.limitex.avatar.compressor.editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Service for collecting materials from various sources in an avatar hierarchy.
    /// Decoupled from NDMF; scans Animators, VRCAvatarDescriptors, and Renderers directly.
    /// </summary>
    internal static class MaterialCollector
    {
        /// <summary>
        /// Collects all materials from Renderer components in the hierarchy.
        /// </summary>
        public static List<MaterialReference> CollectFromRenderers(GameObject root)
        {
            var references = new List<MaterialReference>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                if (ComponentUtils.IsEditorOnly(renderer.gameObject))
                    continue;

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                        continue;

                    references.Add(MaterialReference.FromRenderer(material, renderer));
                }
            }

            return references;
        }

        /// <summary>
        /// Collects materials referenced by animations across the entire avatar hierarchy,
        /// including Animator components, VRCAvatarDescriptor layers, and legacy Animation components.
        /// </summary>
        public static List<MaterialReference> CollectFromAnimator(GameObject root)
        {
            var references = new List<MaterialReference>();
            if (root == null)
                return references;

            var clips = new HashSet<AnimationClip>();

            // 1. Collect from all Animator components in hierarchy
            var animators = root.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators)
            {
                if (animator != null && animator.runtimeAnimatorController != null)
                {
                    foreach (var clip in GetAllAnimationClips(animator.runtimeAnimatorController))
                    {
                        if (clip != null)
                            clips.Add(clip);
                    }
                }
            }

            // 2. Collect from VRCAvatarDescriptor controllers (base and special layers)
            var descriptors = root.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && (c.GetType().Name == "VRCAvatarDescriptor" || c is VRC.SDKBase.VRC_AvatarDescriptor));
            foreach (var desc in descriptors)
            {
                CollectClipsFromAvatarDescriptor(desc, clips);
            }

            // 3. Collect from legacy Animation components
            var legacyAnims = root.GetComponentsInChildren<Animation>(true);
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

            // Extract material references from all found clips
            foreach (var clip in clips)
            {
                if (clip == null)
                    continue;

                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keyframes == null)
                        continue;

                    foreach (var keyframe in keyframes)
                    {
                        if (keyframe.value is Material material && material != null)
                        {
                            references.Add(MaterialReference.FromAnimation(material, clip));
                        }
                    }
                }
            }

            return references;
        }

        /// <summary>
        /// Collects materials referenced by components (e.g., MA MaterialSetter) in the hierarchy.
        /// </summary>
        public static List<MaterialReference> CollectFromComponents(GameObject root)
        {
            var references = new List<MaterialReference>();
            var components = root.GetComponentsInChildren<Component>(true);

            foreach (var component in components)
            {
                if (component == null)
                    continue;
                if (component is Renderer)
                    continue;
                if (component is Animator)
                    continue;
                if (component.GetType().Name == "TextureCompressor")
                    continue;
                if (ComponentUtils.IsEditorOnly(component.gameObject))
                    continue;

                CollectFromSerializedProperties(component, references);
            }

            return references;
        }

        private static void CollectFromSerializedProperties(
            Component component,
            List<MaterialReference> references
        )
        {
            SerializedObject so;
            try
            {
                so = new SerializedObject(component);
            }
            catch (Exception)
            {
                return;
            }

            using (so)
            {
                var iterator = so.GetIterator();
                while (iterator.NextVisible(true))
                {
                    if (
                        iterator.propertyType == SerializedPropertyType.ObjectReference
                        && iterator.objectReferenceValue is Material material
                        && material != null
                    )
                    {
                        references.Add(
                            MaterialReference.FromComponent(
                                material,
                                component,
                                iterator.propertyPath
                            )
                        );
                    }
                }
            }
        }

        /// <summary>
        /// Collects all materials from all sources (Renderers, Animations, Components).
        /// </summary>
        public static List<MaterialReference> CollectAll(GameObject root)
        {
            var references = new List<MaterialReference>();

            references.AddRange(CollectFromRenderers(root));
            references.AddRange(CollectFromAnimator(root));
            references.AddRange(CollectFromComponents(root));

            return references;
        }

        /// <summary>
        /// Gets distinct materials from a list of references.
        /// </summary>
        public static IEnumerable<Material> GetDistinctMaterials(
            IEnumerable<MaterialReference> references
        )
        {
            return references.Where(r => r?.Material != null).Select(r => r.Material).Distinct();
        }

        #region Animation Clip Helpers

        public static void CollectClipsFromAvatarDescriptor(Component descriptor, HashSet<AnimationClip> clips)
        {
            if (descriptor == null) return;
            var type = descriptor.GetType();
            var baseLayersProp = type.GetField("baseAnimationLayers") ?? (MemberInfo)type.GetProperty("baseAnimationLayers");
            CollectClipsFromLayers(descriptor, baseLayersProp, clips);
            var specialLayersProp = type.GetField("specialAnimationLayers") ?? (MemberInfo)type.GetProperty("specialAnimationLayers");
            CollectClipsFromLayers(descriptor, specialLayersProp, clips);
        }

        private static void CollectClipsFromLayers(object descriptor, MemberInfo member, HashSet<AnimationClip> clips)
        {
            if (member == null) return;
            object val = member is FieldInfo f ? f.GetValue(descriptor) : ((PropertyInfo)member).GetValue(descriptor);
            if (val is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    var ctrlField = item.GetType().GetField("animatorController") ?? (MemberInfo)item.GetType().GetProperty("animatorController");
                    if (ctrlField != null)
                    {
                        var ctrl = ctrlField is FieldInfo cf ? cf.GetValue(item) : ((PropertyInfo)ctrlField).GetValue(item);
                        if (ctrl is RuntimeAnimatorController rac)
                        {
                            foreach (var c in GetAllAnimationClips(rac))
                            {
                                if (c != null)
                                    clips.Add(c);
                            }
                        }
                    }
                }
            }
        }

        public static List<AnimationClip> GetAllAnimationClips(
            RuntimeAnimatorController controller
        )
        {
            var clips = new HashSet<AnimationClip>();
            if (controller == null)
                return clips.ToList();

            if (controller is AnimatorOverrideController overrideController)
            {
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(
                    overrideController.overridesCount
                );
                overrideController.GetOverrides(overrides);

                foreach (var pair in overrides)
                {
                    if (pair.Value != null)
                        clips.Add(pair.Value);
                    else if (pair.Key != null)
                        clips.Add(pair.Key);
                }

                if (overrideController.runtimeAnimatorController != null)
                {
                    foreach (
                        var clip in GetAllAnimationClips(
                            overrideController.runtimeAnimatorController
                        )
                    )
                    {
                        clips.Add(clip);
                    }
                }
            }
            else if (controller is AnimatorController animatorController)
            {
                foreach (var layer in animatorController.layers)
                {
                    CollectClipsFromStateMachine(layer.stateMachine, clips);
                }
            }
            else
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip != null)
                        clips.Add(clip);
                }
            }

            return clips.ToList();
        }

        private static void CollectClipsFromStateMachine(
            AnimatorStateMachine stateMachine,
            HashSet<AnimationClip> clips
        )
        {
            if (stateMachine == null)
                return;

            foreach (var state in stateMachine.states)
            {
                if (state.state?.motion is AnimationClip clip)
                {
                    clips.Add(clip);
                }
                else if (state.state?.motion is BlendTree blendTree)
                {
                    CollectClipsFromBlendTree(blendTree, clips);
                }
            }

            foreach (var subStateMachine in stateMachine.stateMachines)
            {
                CollectClipsFromStateMachine(subStateMachine.stateMachine, clips);
            }
        }

        private static void CollectClipsFromBlendTree(
            BlendTree blendTree,
            HashSet<AnimationClip> clips
        )
        {
            if (blendTree == null)
                return;

            foreach (var child in blendTree.children)
            {
                if (child.motion is AnimationClip clip)
                {
                    clips.Add(clip);
                }
                else if (child.motion is BlendTree childBlendTree)
                {
                    CollectClipsFromBlendTree(childBlendTree, clips);
                }
            }
        }

        #endregion
    }
}
