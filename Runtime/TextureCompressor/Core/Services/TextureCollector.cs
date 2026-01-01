using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace dev.limitex.avatar.compressor.texture
{
    /// <summary>
    /// Service for collecting textures from avatar hierarchy.
    /// </summary>
    public class TextureCollector
    {
        private static readonly HashSet<string> MainTextureProperties = new HashSet<string>
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_AlbedoMap",
            "_Diffuse", "_DiffuseMap", "_Color", "_ColorMap"
        };

        private static readonly HashSet<string> NormalMapProperties = new HashSet<string>
        {
            "_BumpMap", "_NormalMap", "_Normal", "_DetailNormalMap"
        };

        private static readonly HashSet<string> EmissionProperties = new HashSet<string>
        {
            "_EmissionMap", "_EmissiveMap", "_Emission", "_EmissiveColor"
        };

        private readonly int _minSourceSize;
        private readonly int _skipIfSmallerThan;
        private readonly bool _processMainTextures;
        private readonly bool _processNormalMaps;
        private readonly bool _processEmissionMaps;
        private readonly bool _processOtherTextures;

        public TextureCollector(
            int minSourceSize,
            int skipIfSmallerThan,
            bool processMainTextures,
            bool processNormalMaps,
            bool processEmissionMaps,
            bool processOtherTextures)
        {
            _minSourceSize = minSourceSize;
            _skipIfSmallerThan = skipIfSmallerThan;
            _processMainTextures = processMainTextures;
            _processNormalMaps = processNormalMaps;
            _processEmissionMaps = processEmissionMaps;
            _processOtherTextures = processOtherTextures;
        }

        /// <summary>
        /// Collects all textures from the avatar hierarchy.
        /// </summary>
        public Dictionary<Texture2D, TextureInfo> Collect(GameObject root)
        {
            var textures = new Dictionary<Texture2D, TextureInfo>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                foreach (var material in materials)
                {
                    if (material == null) continue;
                    CollectFromMaterial(material, renderer, textures);
                }
            }

            return textures;
        }

        private void CollectFromMaterial(
            Material material,
            Renderer renderer,
            Dictionary<Texture2D, TextureInfo> textures)
        {
#if UNITY_EDITOR
            var shader = material.shader;
            int propertyCount = ShaderUtil.GetPropertyCount(shader);

            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    continue;

                string propertyName = ShaderUtil.GetPropertyName(shader, i);
                var texture = material.GetTexture(propertyName) as Texture2D;

                if (texture == null) continue;
                if (!ShouldProcess(texture, propertyName)) continue;

                string textureType = GetTextureType(propertyName);
                bool isNormalMap = NormalMapProperties.Contains(propertyName);
                bool isEmission = EmissionProperties.Contains(propertyName);

                if (!textures.TryGetValue(texture, out var info))
                {
                    info = new TextureInfo
                    {
                        TextureType = textureType,
                        PropertyName = propertyName,
                        IsNormalMap = isNormalMap,
                        IsEmission = isEmission
                    };
                    textures[texture] = info;
                }
                else
                {
                    // If the same texture is used as both normal map and other type,
                    // prioritize normal map classification to ensure proper analysis
                    if (isNormalMap && !info.IsNormalMap)
                    {
                        info.IsNormalMap = true;
                        info.TextureType = "Normal";
                    }
                    // Similarly for emission
                    if (isEmission && !info.IsEmission)
                    {
                        info.IsEmission = true;
                    }
                }

                info.References.Add(new MaterialTextureReference
                {
                    Material = material,
                    PropertyName = propertyName,
                    Renderer = renderer
                });
            }
#endif
        }

        private bool ShouldProcess(Texture2D texture, string propertyName)
        {
            int maxDim = Mathf.Max(texture.width, texture.height);
            if (maxDim < _minSourceSize) return false;
            if (maxDim <= _skipIfSmallerThan) return false;

            if (MainTextureProperties.Contains(propertyName))
                return _processMainTextures;

            if (NormalMapProperties.Contains(propertyName))
                return _processNormalMaps;

            if (EmissionProperties.Contains(propertyName))
                return _processEmissionMaps;

            return _processOtherTextures;
        }

        private string GetTextureType(string propertyName)
        {
            if (MainTextureProperties.Contains(propertyName)) return "Main";
            if (NormalMapProperties.Contains(propertyName)) return "Normal";
            if (EmissionProperties.Contains(propertyName)) return "Emission";
            return "Other";
        }
    }
}
