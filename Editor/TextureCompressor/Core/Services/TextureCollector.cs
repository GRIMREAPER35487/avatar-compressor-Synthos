using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Service for collecting textures from avatar hierarchy.
    /// </summary>
    internal sealed class TextureCollector
    {
        private readonly int _minSourceSize;
        private readonly int _skipIfSmallerThan;

        // Size the size filters check. Defaults to the imported size; when not respecting the
        // import Max Size it is the source size, so a texture imported small from a large file is
        // not filtered out before the toggle can apply.
        private readonly Func<Texture2D, Vector2Int> _filterSize;
        private readonly bool _processMainTextures;
        private readonly bool _processNormalMaps;
        private readonly bool _processEmissionMaps;
        private readonly bool _processOtherTextures;
        private readonly bool _skipUnknownUncompressedTextures;
        private readonly List<string> _excludedPathPrefixes;
        private readonly HashSet<string> _excludedTextureGuids;
        private readonly HashSet<string> _frozenSkipGuids;

        public TextureCollector(
            int minSourceSize,
            int skipIfSmallerThan,
            bool processMainTextures,
            bool processNormalMaps,
            bool processEmissionMaps,
            bool processOtherTextures,
            bool skipUnknownUncompressedTextures,
            IEnumerable<string> excludedPathPrefixes = null,
            IEnumerable<Texture2D> excludedTextures = null,
            IEnumerable<FrozenTextureSettings> frozenTextures = null,
            Func<Texture2D, Vector2Int> filterSize = null
        )
        {
            _filterSize = filterSize;
            _minSourceSize = minSourceSize;
            _skipIfSmallerThan = skipIfSmallerThan;
            _processMainTextures = processMainTextures;
            _processNormalMaps = processNormalMaps;
            _processEmissionMaps = processEmissionMaps;
            _processOtherTextures = processOtherTextures;
            _skipUnknownUncompressedTextures = skipUnknownUncompressedTextures;
            _excludedPathPrefixes =
                excludedPathPrefixes != null
                    ? new List<string>(
                        excludedPathPrefixes.Where(p => !string.IsNullOrWhiteSpace(p))
                    )
                    : new List<string>();
            _excludedTextureGuids =
                excludedTextures != null
                    ? new HashSet<string>(ToAssetGuids(excludedTextures))
                    : new HashSet<string>();
            _frozenSkipGuids =
                frozenTextures != null
                    ? new HashSet<string>(
                        frozenTextures
                            .Where(f => f.Skip && !string.IsNullOrEmpty(f.TextureGuid))
                            .Select(f => f.TextureGuid)
                    )
                    : new HashSet<string>();
        }

        /// <summary>
        /// Resolves the original asset for an object via the build's replacement registry.
        /// Build-time replacements (e.g. baked textures) map back to the asset they came from.
        /// </summary>
        internal static Object ResolveOriginalObject(Object obj)
        {
            return ReplacedObjectRegistry.Resolve(obj);
        }

        /// <summary>
        /// Returns true if the texture (or the asset a build-time copy came from) is imported
        /// with Texture Type "Normal map".
        /// </summary>
        internal static bool IsImportedAsNormalMap(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(ResolveOriginalObject(texture));
            if (string.IsNullOrEmpty(path))
                return false;

            return AssetImporter.GetAtPath(path) is TextureImporter importer
                && importer.textureType == TextureImporterType.NormalMap;
        }

        /// <summary>
        /// Collects textures that should be processed from the avatar hierarchy.
        /// </summary>
        public Dictionary<Texture2D, TextureInfo> Collect(GameObject root)
        {
            return CollectInternal(root, collectAll: false);
        }

        /// <summary>
        /// Collects all textures from the avatar hierarchy, including skipped ones.
        /// Skipped textures will have IsProcessed=false and SkipReason set.
        /// </summary>
        public Dictionary<Texture2D, TextureInfo> CollectAll(GameObject root)
        {
            return CollectInternal(root, collectAll: true);
        }

        private Dictionary<Texture2D, TextureInfo> CollectInternal(GameObject root, bool collectAll)
        {
            var textures = new Dictionary<Texture2D, TextureInfo>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                // Skip EditorOnly tagged objects (stripped from build)
                if (ComponentUtils.IsEditorOnly(renderer.gameObject))
                    continue;

                var materials = renderer.sharedMaterials;
                foreach (var material in materials)
                {
                    if (material == null)
                        continue;
                    CollectFromMaterial(material, renderer, textures, collectAll);
                }
            }

            return textures;
        }

        /// <summary>
        /// Collects textures from a list of materials (e.g., from animations).
        /// Call this after Collect() to add additional materials to the same dictionary.
        /// </summary>
        /// <param name="materials">The materials to collect textures from.</param>
        /// <param name="textures">The texture dictionary to add to (typically from Collect()).</param>
        /// <param name="collectAll">If true, collects all textures including skipped ones (for preview).</param>
        public void CollectFromMaterials(
            IEnumerable<Material> materials,
            Dictionary<Texture2D, TextureInfo> textures,
            bool collectAll = false
        )
        {
            if (materials == null || textures == null)
                return;

            foreach (var material in materials.Distinct())
            {
                if (material == null)
                    continue;
                CollectFromMaterial(material, null, textures, collectAll);
            }
        }

        private void CollectFromMaterial(
            Material material,
            Renderer renderer,
            Dictionary<Texture2D, TextureInfo> textures,
            bool collectAll = false
        )
        {
            var shader = material.shader;
            int propertyCount = ShaderUtil.GetPropertyCount(shader);

            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    continue;

                string propertyName = ShaderUtil.GetPropertyName(shader, i);
                var texture = material.GetTexture(propertyName) as Texture2D;

                if (texture == null)
                    continue;

                var category = TexturePropertyDefinitions.GetCategory(propertyName);

                // Shaders with unlisted property names still get correct normal-map handling
                // when the texture is imported as a normal map.
                if (category != TexturePropertyCategory.Normal && IsImportedAsNormalMap(texture))
                    category = TexturePropertyCategory.Normal;

                bool isNormalMap = category == TexturePropertyCategory.Normal;
                bool isEmission = category == TexturePropertyCategory.Emission;

                if (!textures.TryGetValue(texture, out var info))
                {
                    info = new TextureInfo
                    {
                        TextureType = category,
                        IsNormalMap = isNormalMap,
                        IsEmission = isEmission,
                    };
                    EvaluateProcessability(info, texture, propertyName);

                    if (!collectAll && !info.IsProcessed)
                        continue;

                    textures[texture] = info;
                }
                else
                {
                    // If the same texture is used as both normal map and other type,
                    // prioritize normal map classification to ensure proper analysis
                    if (isNormalMap && !info.IsNormalMap)
                    {
                        info.IsNormalMap = true;
                        info.TextureType = TexturePropertyCategory.Normal;
                    }
                    // Similarly for emission
                    if (isEmission && !info.IsEmission)
                    {
                        info.IsEmission = true;
                    }
                    // Per-texture checks (path, size, frozen) don't change between properties,
                    // but property-dependent skip reasons can be upgraded when a subsequent
                    // property reference satisfies the required conditions.
                    if (
                        !info.IsProcessed
                        && IsPropertyDependentReason(info.SkipReason)
                        && GetPropertyDependentSkipReason(texture, propertyName) == null
                    )
                    {
                        info.IsProcessed = true;
                        info.SkipReason = SkipReason.None;
                    }
                }

                info.References.Add(
                    new MaterialTextureReference
                    {
                        Material = material,
                        PropertyName = propertyName,
                        Renderer = renderer,
                    }
                );
            }
        }

        /// <summary>
        /// Returns true if a texture assigned to the given property would be collected for
        /// processing under the current filter settings. Lets earlier pipeline steps (e.g.
        /// lilToon texture baking) avoid replacing a texture whose replacement the pipeline
        /// would then refuse to compress.
        /// </summary>
        public bool WouldProcess(Texture2D texture, string propertyName)
        {
            if (texture == null)
                return false;

            var info = new TextureInfo();
            EvaluateProcessability(info, texture, propertyName);
            return info.IsProcessed;
        }

        /// <summary>
        /// Evaluates whether a texture should be processed and populates the TextureInfo accordingly.
        /// Resolves the original asset via the replacement registry for build-time replacement textures.
        /// </summary>
        private void EvaluateProcessability(
            TextureInfo info,
            Texture2D texture,
            string propertyName
        )
        {
            // Resolve original asset via the replacement registry.
            // A build-time replacement (e.g. a lilToon bake) has no asset path;
            // the registry follows the chain back to the original asset.
            Object resolvedObj = ResolveOriginalObject(texture);

            string assetPath = AssetDatabase.GetAssetPath(resolvedObj);

            // Skip runtime-generated textures (no asset path).
            // These are dynamically created during build and may use RGB values for non-visual data
            // (e.g., depth, deformation vectors), which compression would corrupt.
            if (string.IsNullOrEmpty(assetPath))
            {
                info.IsProcessed = false;
                info.SkipReason = SkipReason.RuntimeGenerated;
                return;
            }

            // Skip textures in excluded paths
            foreach (var prefix in _excludedPathPrefixes)
            {
                if (assetPath.StartsWith(prefix))
                {
                    info.IsProcessed = false;
                    info.SkipReason = SkipReason.ExcludedPath;
                    return;
                }
            }

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            info.AssetGuid = guid ?? string.Empty;

            // Check excluded textures using GUID for reliable comparison
            if (!string.IsNullOrEmpty(guid) && _excludedTextureGuids.Contains(guid))
            {
                info.IsProcessed = false;
                info.SkipReason = SkipReason.ExcludedTexture;
                return;
            }

            if (!string.IsNullOrEmpty(guid) && _frozenSkipGuids.Contains(guid))
            {
                info.IsProcessed = false;
                info.SkipReason = SkipReason.FrozenSkip;
                return;
            }

            var filterSize =
                _filterSize != null
                    ? _filterSize(texture)
                    : new Vector2Int(texture.width, texture.height);
            int maxDim = Mathf.Max(filterSize.x, filterSize.y);
            if (maxDim < _minSourceSize || maxDim <= _skipIfSmallerThan)
            {
                info.IsProcessed = false;
                info.SkipReason = SkipReason.TooSmall;
                return;
            }

            var propertySkipReason = GetPropertyDependentSkipReason(texture, propertyName);
            if (propertySkipReason != null)
            {
                info.IsProcessed = false;
                info.SkipReason = propertySkipReason.Value;
                return;
            }

            info.IsProcessed = true;
            info.SkipReason = SkipReason.None;
        }

        /// <summary>
        /// Converts a list of Texture2D references to their asset GUIDs, filtering nulls and invalid entries.
        /// </summary>
        private static IEnumerable<string> ToAssetGuids(IEnumerable<Texture2D> textures)
        {
            if (textures == null)
                return Enumerable.Empty<string>();

            return textures
                .Where(t => t != null)
                .Select(t => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t)))
                .Where(g => !string.IsNullOrEmpty(g));
        }

        /// <summary>
        /// Returns the property-dependent skip reason for the given texture and property,
        /// or null if the property passes all property-dependent checks.
        /// This is the single source of truth for property-dependent skip logic, used both
        /// during initial evaluation and when upgrading a previously skipped texture.
        /// </summary>
        private SkipReason? GetPropertyDependentSkipReason(Texture2D texture, string propertyName)
        {
            if (!IsTypeEnabled(propertyName))
                return SkipReason.FilteredByType;

            // Sub-check within the Other category: skip uncompressed textures on unknown
            // shader properties to avoid corrupting non-visual data (e.g., SPS bake data,
            // masks, LUTs). This mirrors the UI hierarchy where the toggle is nested under
            // the Other filter and disabled when Other is off.
            // Already-compressed textures (DXT, BC, ASTC, etc.) are not skipped —
            // they were intentionally compressed upstream.
            if (
                _skipUnknownUncompressedTextures
                && !TextureFormatInfo.IsCompressed(texture.format)
                && !TexturePropertyDefinitions.IsKnownTextureProperty(propertyName)
            )
                return SkipReason.UnknownUncompressedProperty;

            return null;
        }

        /// <summary>
        /// Returns true if the skip reason depends on the property name and can potentially
        /// be resolved by a different property referencing the same texture.
        /// Per-texture reasons (path, size, frozen, runtime-generated) are never upgradeable.
        /// </summary>
        private static bool IsPropertyDependentReason(SkipReason reason)
        {
            return reason == SkipReason.FilteredByType
                || reason == SkipReason.UnknownUncompressedProperty;
        }

        private bool IsTypeEnabled(string propertyName)
        {
            switch (TexturePropertyDefinitions.GetCategory(propertyName))
            {
                case TexturePropertyCategory.Main:
                    return _processMainTextures;
                case TexturePropertyCategory.Normal:
                    return _processNormalMaps;
                case TexturePropertyCategory.Emission:
                    return _processEmissionMaps;
                default:
                    return _processOtherTextures;
            }
        }
    }
}
