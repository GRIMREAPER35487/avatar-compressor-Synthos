using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Enumerates the animator controllers and animation clips that ship with an avatar.
    /// </summary>
    /// <remarks>
    /// Sources are the <see cref="VRCAvatarDescriptor"/> playable layers (base and special),
    /// every <see cref="Animator"/> in the hierarchy, and legacy <see cref="Animation"/>
    /// components. Layers left on the VRChat defaults are skipped: the SDK substitutes its
    /// built-in controllers for them, which never animate avatar materials.
    /// </remarks>
    internal static class AvatarAnimators
    {
        /// <summary>
        /// Returns every distinct controller used by the avatar.
        /// </summary>
        public static List<RuntimeAnimatorController> GetControllers(GameObject root)
        {
            var controllers = new List<RuntimeAnimatorController>();
            if (root == null)
                return controllers;

            var seen = new HashSet<RuntimeAnimatorController>();

            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                AddLayerControllers(descriptor.baseAnimationLayers, seen, controllers);
                AddLayerControllers(descriptor.specialAnimationLayers, seen, controllers);
            }

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller != null && seen.Add(controller))
                    controllers.Add(controller);
            }

            return controllers;
        }

        /// <summary>
        /// Returns every distinct animation clip used by the avatar.
        /// </summary>
        public static HashSet<AnimationClip> GetClips(GameObject root)
        {
            var clips = new HashSet<AnimationClip>();
            if (root == null)
                return clips;

            foreach (var controller in GetControllers(root))
                clips.UnionWith(GetClips(controller));

            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
            {
                foreach (AnimationState state in animation)
                {
                    if (state != null && state.clip != null)
                        clips.Add(state.clip);
                }
            }

            return clips;
        }

        /// <summary>
        /// Returns every distinct animation clip reachable from a controller,
        /// including clips inside blend trees and override controller replacements.
        /// </summary>
        public static HashSet<AnimationClip> GetClips(RuntimeAnimatorController controller)
        {
            var clips = new HashSet<AnimationClip>();
            CollectClips(controller, clips);
            return clips;
        }

        private static void AddLayerControllers(
            VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<RuntimeAnimatorController> seen,
            List<RuntimeAnimatorController> controllers
        )
        {
            if (layers == null)
                return;

            foreach (var layer in layers)
            {
                if (layer.isDefault || layer.animatorController == null)
                    continue;
                if (seen.Add(layer.animatorController))
                    controllers.Add(layer.animatorController);
            }
        }

        private static void CollectClips(
            RuntimeAnimatorController controller,
            HashSet<AnimationClip> clips
        )
        {
            if (controller == null)
                return;

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
            }
            else if (controller is AnimatorController animatorController)
            {
                var layers = animatorController.layers;
                foreach (var layer in layers)
                    CollectClipsFromStateMachine(layer.stateMachine, clips);

                // Synced layers store their per-state motions on the layer itself.
                foreach (var layer in layers)
                {
                    int source = layer.syncedLayerIndex;
                    if (source < 0 || source >= layers.Length)
                        continue;
                    foreach (var state in GetAllStates(layers[source].stateMachine))
                        CollectClipsFromMotion(layer.GetOverrideMotion(state), clips);
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
        }

        private static void CollectClipsFromStateMachine(
            AnimatorStateMachine stateMachine,
            HashSet<AnimationClip> clips
        )
        {
            if (stateMachine == null)
                return;

            foreach (var state in stateMachine.states)
                CollectClipsFromMotion(state.state?.motion, clips);

            foreach (var subStateMachine in stateMachine.stateMachines)
                CollectClipsFromStateMachine(subStateMachine.stateMachine, clips);
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

        private static void CollectClipsFromMotion(Motion motion, HashSet<AnimationClip> clips)
        {
            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
            }
            else if (motion is BlendTree blendTree)
            {
                foreach (var child in blendTree.children)
                    CollectClipsFromMotion(child.motion, clips);
            }
        }
    }
}
