using System.Collections.Generic;
using System.Linq;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Service for cloning materials and updating references.
    /// </summary>
    internal static class MaterialCloner
    {
        /// <summary>
        /// Clones materials from the given references and updates Renderer and Component references.
        /// </summary>
        /// <param name="references">Material references to process</param>
        /// <returns>Dictionary mapping original materials to cloned materials</returns>
        public static Dictionary<Material, Material> CloneAndReplace(
            IEnumerable<MaterialReference> references
        )
        {
            var clonedMaterials = new Dictionary<Material, Material>();
            var referenceList = references.ToList();

            // First pass: clone all unique materials
            foreach (var reference in referenceList)
            {
                if (reference?.Material == null)
                    continue;
                GetOrCloneMaterial(reference.Material, clonedMaterials);
            }

            // Second pass: update Renderer and Component references
            UpdateRendererReferences(referenceList, clonedMaterials);
            UpdateComponentReferences(referenceList, clonedMaterials);

            return clonedMaterials;
        }

        private static void UpdateRendererReferences(
            IEnumerable<MaterialReference> references,
            Dictionary<Material, Material> clonedMaterials
        )
        {
            // Group references by Renderer for efficient batch updates
            var rendererGroups = references
                .Where(r =>
                    r != null
                    && r.SourceType == MaterialSourceType.Renderer
                    && r.SourceObject is Renderer
                )
                .GroupBy(r => (Renderer)r.SourceObject);

            foreach (var group in rendererGroups)
            {
                var renderer = group.Key;
                if (renderer == null)
                    continue;

                var originalMaterials = renderer.sharedMaterials;
                var newMaterials = new Material[originalMaterials.Length];
                bool hasChanges = false;

                for (int i = 0; i < originalMaterials.Length; i++)
                {
                    var originalMat = originalMaterials[i];
                    if (
                        originalMat != null
                        && clonedMaterials.TryGetValue(originalMat, out var clonedMat)
                    )
                    {
                        newMaterials[i] = clonedMat;
                        hasChanges = true;
                    }
                    else
                    {
                        newMaterials[i] = originalMat;
                    }
                }

                if (hasChanges)
                {
                    renderer.sharedMaterials = newMaterials;
                }
            }
        }

        private static void UpdateComponentReferences(
            IEnumerable<MaterialReference> references,
            Dictionary<Material, Material> clonedMaterials
        )
        {
            var components = references
                .Where(r =>
                    r != null
                    && r.SourceType == MaterialSourceType.Component
                    && r.SourceObject is Component
                )
                .Select(r => (Component)r.SourceObject)
                .Distinct();

            foreach (var component in components)
            {
                if (component == null)
                    continue;

                try
                {
                    using var serializedObject = new SerializedObject(component);
                    var iterator = serializedObject.GetIterator();
                    bool hasChanges = false;

                    while (iterator.NextVisible(true))
                    {
                        if (
                            iterator.propertyType == SerializedPropertyType.ObjectReference
                            && iterator.objectReferenceValue is Material material
                            && material != null
                            && clonedMaterials.TryGetValue(material, out var clonedMat)
                        )
                        {
                            iterator.objectReferenceValue = clonedMat;
                            hasChanges = true;
                        }
                    }

                    if (hasChanges)
                        serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
                catch
                {
                    // Ignore components that can't be serialized
                }
            }
        }

        private static Material GetOrCloneMaterial(
            Material originalMat,
            Dictionary<Material, Material> clonedMaterials
        )
        {
            if (clonedMaterials.TryGetValue(originalMat, out var clonedMat))
            {
                return clonedMat;
            }

            clonedMat = Object.Instantiate(originalMat);
            clonedMat.name = originalMat.name + "_clone";

            ReplacedObjectRegistry.Register(originalMat, clonedMat);
            clonedMaterials[originalMat] = clonedMat;

            return clonedMat;
        }
    }
}
