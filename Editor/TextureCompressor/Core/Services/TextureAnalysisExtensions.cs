using System.Collections.Generic;
using Synthos.TextureAnalysis;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Lets the compressor pass its own texture info to the shared analysis.
    /// </summary>
    internal static class TextureAnalysisExtensions
    {
        public static Dictionary<Texture2D, TextureAnalysisInput> ToAnalysisInput(
            this Dictionary<Texture2D, TextureInfo> textures
        )
        {
            var inputs = new Dictionary<Texture2D, TextureAnalysisInput>(textures.Count);
            foreach (var kvp in textures)
                inputs[kvp.Key] = new TextureAnalysisInput(
                    kvp.Value.IsNormalMap,
                    AlphaIsTransparency(kvp.Value)
                );
            return inputs;
        }

        /// <summary>
        /// True when any material shows the texture's alpha as see-through.
        /// </summary>
        public static bool AlphaIsTransparency(TextureInfo info)
        {
            foreach (var reference in info.References)
            {
                if (AlphaCoverage.IsCoverage(reference.Material, reference.PropertyName))
                    return true;
            }
            return false;
        }

        public static Dictionary<Texture2D, float> AnalyzeBatch(
            this TextureAnalyzer analyzer,
            Dictionary<Texture2D, TextureInfo> textures
        )
        {
            return analyzer.AnalyzeBatch(textures.ToAnalysisInput());
        }

        public static Dictionary<Texture2D, float> AnalyzeBatch(
            this ITextureAnalysisBackend backend,
            Dictionary<Texture2D, TextureInfo> textures
        )
        {
            return backend.AnalyzeBatch(textures.ToAnalysisInput());
        }
    }
}
