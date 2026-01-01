using System.Collections.Generic;
using dev.limitex.avatar.compressor.common;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(dev.limitex.avatar.compressor.texture.TextureCompressorPlugin))]

namespace dev.limitex.avatar.compressor.texture
{
    public class TextureCompressorPlugin : Plugin<TextureCompressorPlugin>
    {
        public override string DisplayName => "Texture Compressor";
        public override string QualifiedName => "dev.limitex.avatar-compressor.texture";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Compress Avatar Textures", BuildTask);
        }

        private void BuildTask(BuildContext ctx)
        {
            var compressors = ctx.AvatarRootObject.GetComponentsInChildren<TextureCompressor>(true);

            if (compressors.Length == 0) return;

            var config = compressors[0];

            var collector = new TextureCollector(
                config.MinSourceSize,
                config.SkipIfSmallerThan,
                config.ProcessMainTextures,
                config.ProcessNormalMaps,
                config.ProcessEmissionMaps,
                config.ProcessOtherTextures
            );

            var resizer = new TextureResizer(
                config.MinResolution,
                config.MaxResolution,
                config.ForcePowerOfTwo
            );

            var complexityCalc = new ComplexityCalculator(
                config.HighComplexityThreshold,
                config.LowComplexityThreshold,
                config.MinDivisor,
                config.MaxDivisor
            );

            MaterialCloner.CloneMaterials(ctx.AvatarRootObject);

            var textures = collector.Collect(ctx.AvatarRootObject);

            if (textures.Count == 0)
            {
                if (config.EnableLogging)
                {
                    Debug.Log("[TextureCompressor] No textures found to process.");
                }
                CleanupComponents(compressors);
                return;
            }

            if (config.EnableLogging)
            {
                Debug.Log($"[TextureCompressor] Processing {textures.Count} textures...");
            }

            var analyzer = new TextureAnalyzer(
                config.Strategy,
                config.FastWeight,
                config.HighAccuracyWeight,
                config.PerceptualWeight,
                resizer,
                complexityCalc
            );

            var analysisResults = analyzer.AnalyzeBatch(textures);

            var processedTextures = new Dictionary<Texture2D, Texture2D>();

            foreach (var kvp in textures)
            {
                var originalTexture = kvp.Key;
                var textureInfo = kvp.Value;

                if (processedTextures.ContainsKey(originalTexture)) continue;

                if (!analysisResults.TryGetValue(originalTexture, out var analysis))
                {
                    Debug.LogWarning($"[TextureCompressor] Skipping texture '{originalTexture.name}': analysis failed");
                    continue;
                }
                var compressedTexture = resizer.Resize(originalTexture, analysis, config.EnableLogging);
                compressedTexture.name = originalTexture.name + "_compressed";

                processedTextures[originalTexture] = compressedTexture;

                foreach (var reference in textureInfo.References)
                {
                    reference.Material.SetTexture(reference.PropertyName, compressedTexture);
                }
            }

            if (config.EnableLogging)
            {
                LogSummary(textures, processedTextures);
            }

            CleanupComponents(compressors);
        }

        private void LogSummary(
            Dictionary<Texture2D, TextureInfo> original,
            Dictionary<Texture2D, Texture2D> processed)
        {
            long originalSize = 0;
            long compressedSize = 0;

            foreach (var kvp in original)
            {
                var origTex = kvp.Key;
                originalSize += (long)origTex.width * origTex.height * 4;

                if (processed.TryGetValue(origTex, out var compTex))
                {
                    compressedSize += (long)compTex.width * compTex.height * 4;
                }
            }

            float savings = originalSize > 0 ? 1f - (float)compressedSize / originalSize : 0f;

            Debug.Log($"[TextureCompressor] Complete: " +
                      $"{originalSize / 1024f / 1024f:F2}MB → {compressedSize / 1024f / 1024f:F2}MB " +
                      $"({savings:P0} reduction)");
        }

        private void CleanupComponents(TextureCompressor[] compressors)
        {
            foreach (var compressor in compressors)
            {
                ComponentUtils.SafeDestroy(compressor);
            }
        }
    }
}
