using System;
using System.Collections.Generic;
using System.Linq;
using dev.limitex.avatar.compressor.editor;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Service for collecting materials from various sources in an avatar hierarchy.
    /// </summary>
    internal static class MaterialCollector
    {
        /// <summary>
        /// Collects all materials from Renderer components in the hierarchy.
        /// </summary>
        /// <remarks>
        /// This method is kept separate from <see cref="CollectFromComponents"/> for performance reasons.
        /// Direct access to <see cref="Renderer.sharedMaterials"/> is significantly faster than
        /// iterating through all properties via <see cref="UnityEditor.SerializedObject"/>.
        /// </remarks>
        /// <param name="root">Root GameObject of the hierarchy</param>
        /// <returns>List of material references from Renderers</returns>
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
        /// Collects materials referenced by animation object curves (material swaps) on the avatar:
        /// avatar descriptor playable layers, Animator components and legacy Animation components.
        /// </summary>
        /// <param name="root">Avatar root GameObject</param>
        /// <returns>List of material references from animations</returns>
        public static List<MaterialReference> CollectFromAnimator(GameObject root)
        {
            var references = new List<MaterialReference>();

            foreach (var clip in AvatarAnimators.GetClips(root))
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
        /// <remarks>
        /// Renderer components are skipped because they are handled by <see cref="CollectFromRenderers"/>,
        /// which provides faster direct access to materials. This avoids duplicate collection and
        /// leverages the performance benefit of direct property access over SerializedObject iteration.
        /// </remarks>
        /// <param name="root">Root GameObject of the hierarchy</param>
        /// <returns>List of material references from components</returns>
        public static List<MaterialReference> CollectFromComponents(GameObject root)
        {
            var references = new List<MaterialReference>();
            var allComponents = root.GetComponentsInChildren<Component>(true);

            foreach (var component in allComponents)
            {
                if (component == null)
                    continue;
                if (ComponentUtils.IsEditorOnly(component.gameObject))
                    continue;

                // Skip Renderer components (handled separately)
                if (component is Renderer)
                    continue;

                try
                {
                    using var serializedObject = new SerializedObject(component);
                    var iterator = serializedObject.GetIterator();

                    while (iterator.NextVisible(true))
                    {
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            var obj = iterator.objectReferenceValue;
                            if (obj is Material material && material != null)
                            {
                                references.Add(
                                    MaterialReference.FromComponent(material, component)
                                );
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore errors from components that can't be serialized
                }
            }

            return references;
        }

        /// <summary>
        /// Collects all materials from all sources (Renderers, Animations, Components).
        /// </summary>
        /// <param name="root">Root GameObject of the hierarchy</param>
        /// <returns>List of all material references</returns>
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
        /// <param name="references">List of material references</param>
        /// <returns>Distinct materials</returns>
        public static IEnumerable<Material> GetDistinctMaterials(
            IEnumerable<MaterialReference> references
        )
        {
            return references.Where(r => r?.Material != null).Select(r => r.Material).Distinct();
        }
    }
}
