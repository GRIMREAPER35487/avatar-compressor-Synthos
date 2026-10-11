using System;
using System.Collections.Generic;
using System.Linq;
using dev.limitex.avatar.compressor.editor.ui;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Service that handles texture compression logic.
    /// Implements ICompressor for consistency with other compressors.
    /// </summary>
    internal sealed class TextureCompressorService : ICompressor
    {
        public string Name => "Texture Compressor";

        private readonly TextureCompressor _config;
        private readonly TextureCollector _collector;
        private readonly TextureProcessor _processor;
        private readonly TextureFormatSelector _formatSelector;

        // Chooses formats for textures kept only for a format change (never keeps the import).
        private readonly TextureFormatSelector _formatOnlySelector;
        private readonly ComplexityCalculator _complexityCalc;
        private readonly TextureAnalyzer _analyzer;
        private readonly NormalMapPreprocessor _normalMapPreprocessor;
        private readonly Dictionary<string, FrozenTextureSettings> _frozenLookup;

        private readonly AnimationUsageMap _animationUsageMap;
        private readonly IUnusedSlotOptimizer _unusedSlotOptimizer;
        private readonly bool _unusedDetectionEnabled;
        private readonly ILilToonBaker _lilToonBaker;
        private readonly bool _lilToonBakeEnabled;
        private readonly TextureSourceCache _sourceCache;

        // Flag to avoid repeating the same warning for every texture (per-build instance)
        private bool _streamingMipmapsWarningShown;

        // Flag to avoid repeating build context warning (per-build instance)
        private bool _buildContextWarningShown;

        // No defaults for the backend preferences: production call sites resolve
        // them from TextureCompressorPreferences, and tests must pick deterministically.
        public TextureCompressorService(
            TextureCompressor config,
            AnalysisBackendPreference backendPreference,
            ResizeBackendPreference resizeBackendPreference,
            AnimationUsageMap animationUsageMap = null,
            IUnusedSlotOptimizer unusedSlotOptimizer = null,
            ILilToonBaker lilToonBaker = null,
            TextureSourceCache sourceCache = null
        )
        {
            _config = config;
            _sourceCache = sourceCache;

            _frozenLookup = FrozenTextureSettings.BuildLookup(config.FrozenTextures);

            _animationUsageMap = animationUsageMap;
            _unusedSlotOptimizer = unusedSlotOptimizer;
            _unusedDetectionEnabled =
                config.DetectUnusedTextures
                && animationUsageMap != null
                && unusedSlotOptimizer != null
                && unusedSlotOptimizer.IsAvailable;

            _lilToonBaker = lilToonBaker;
            _lilToonBakeEnabled =
                config.BakeLilToonTextures
                && animationUsageMap != null
                && lilToonBaker != null
                && lilToonBaker.IsAvailable;

            _collector = new TextureCollector(
                config.MinSourceSize,
                config.SkipIfSmallerThan,
                config.ProcessMainTextures,
                config.ProcessNormalMaps,
                config.ProcessEmissionMaps,
                config.ProcessOtherTextures,
                config.SkipUnknownUncompressedTextures,
                config.ExcludedPaths,
                config.ExcludedTextures,
                config.FrozenTextures,
                config.RespectImportMaxSize
                || sourceCache == null
                || !config.SizeFiltersUseSourceSize
                    ? null
                    : texture =>
                        TextureSourceCache.GetPlannedBaseSize(
                            texture,
                            false,
                            true,
                            config.MaxResolution
                        ),
                config.ReformatFilteredTextures
            );

            _processor = new TextureProcessor(
                config.MinResolution,
                config.MaxResolution,
                config.ForcePowerOfTwo,
                resizeBackendPreference
            );

            _formatOnlySelector = new TextureFormatSelector(
                config.TargetPlatform,
                config.UseHighQualityFormatForHighComplexity,
                config.HighComplexityThreshold,
                respectImportFormat: false
            );

            _formatSelector = new TextureFormatSelector(
                config.TargetPlatform,
                config.UseHighQualityFormatForHighComplexity,
                config.HighComplexityThreshold,
                config.RespectImportFormat
            );

            _complexityCalc = new ComplexityCalculator(
                config.HighComplexityThreshold,
                config.LowComplexityThreshold,
                config.MinDivisor,
                config.MaxDivisor
            );

            _analyzer = new TextureAnalyzer(
                config.Strategy,
                config.FastWeight,
                config.HighAccuracyWeight,
                config.PerceptualWeight,
                _processor,
                backendPreference
            );

            _normalMapPreprocessor = new NormalMapPreprocessor();
        }

        /// <summary>
        /// Compresses textures in the avatar hierarchy (ICompressor interface).
        /// Only processes materials on Renderers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>WARNING:</b> This method modifies the Renderer components on the provided GameObject
        /// by replacing their material references with cloned materials. While the original
        /// material asset files (.mat) are NOT modified, the scene will be marked as dirty
        /// if called outside of the avatar build.
        /// </para>
        /// <para>
        /// For production use, prefer the VRChat SDK build hook (TextureCompressorBuildHook) which
        /// operates on a cloned avatar and properly handles animation-referenced materials.
        /// </para>
        /// </remarks>
        public void Compress(GameObject root, bool enableLogging)
        {
            // Warn if materials are linked to asset files (indicates non-build context usage)
            WarnIfNotInBuildContext(root);

            // Collect only Renderer materials for ICompressor interface
            var references = MaterialCollector.CollectFromRenderers(root);
            CompressWithMappings(references, enableLogging);
        }

        /// <summary>
        /// Warns if materials appear to be asset files, which indicates usage outside the build hook.
        /// </summary>
        private void WarnIfNotInBuildContext(GameObject root)
        {
            if (_buildContextWarningShown)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                        continue;

                    string assetPath = AssetDatabase.GetAssetPath(material);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        _buildContextWarningShown = true;
                        Debug.LogWarning(
                            $"[{Name}] Material '{material.name}' is an asset file ({assetPath}). "
                                + "This suggests usage outside the avatar build. "
                                + "While original asset files will NOT be modified, the Renderer's material "
                                + "references will be changed. For a non-destructive workflow, let the avatar build run the compressor."
                        );
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Compresses textures from the given material references and returns mapping information.
        /// </summary>
        /// <param name="materialReferences">Material references to process (from Renderers, animations, components, etc.)</param>
        /// <param name="enableLogging">Whether to log progress</param>
        /// <returns>Tuple containing original-to-compressed texture mappings and original-to-cloned material mappings</returns>
        public (
            Dictionary<Texture2D, Texture2D> ProcessedTextures,
            Dictionary<Material, Material> ClonedMaterials
        ) CompressWithMappings(
            IEnumerable<MaterialReference> materialReferences,
            bool enableLogging
        )
        {
            var referenceList = materialReferences.ToList();

            // Clone all materials and update Renderer references
            var clonedMaterials = MaterialCloner.CloneAndReplace(referenceList);

            var clonedMaterialList = clonedMaterials.Values.ToList();

            // Bake lilToon color adjustments into main textures before slot pruning and
            // collection so the baked texture (not its source) is what gets analyzed and
            // compressed. Consumed input slots (gradation maps, 2nd/3rd layers, alpha masks)
            // are cleared by the baker itself; the pruning below only sweeps leftovers on
            // layers the bake turned off (e.g. a still-assigned dissolve mask).
            if (_lilToonBakeEnabled)
            {
                BakeLilToonAdjustments(clonedMaterialList, enableLogging);
            }

            // Must run before texture collection so the collector only sees surviving bindings
            // (see UnusedSlotPruner remarks).
            if (_unusedDetectionEnabled)
            {
                PruneUnusedSlots(clonedMaterialList, enableLogging);
            }

            // Collect textures from cloned materials
            var textures = new Dictionary<Texture2D, TextureInfo>();
            _collector.CollectFromMaterials(clonedMaterialList, textures);

            if (textures.Count == 0)
            {
                if (enableLogging)
                {
                    Debug.Log($"[{Name}] No textures found to process.");
                }
                return (new Dictionary<Texture2D, Texture2D>(), clonedMaterials);
            }

            if (enableLogging)
            {
                Debug.Log($"[{Name}] Processing {textures.Count} textures...");
            }

            // Make full-quality copies of the source files before analysis: the sizes the
            // analysis scales from depend on them.
            _sourceCache?.Prepare(textures.Keys);
            var baseSizes = new Dictionary<Texture2D, Vector2Int>();
            foreach (var texture in textures.Keys)
                baseSizes[texture] = GetBaseSize(texture);

            var densities = _config.LimitByOnAvatarSize
                ? TexelDensityAnalyzer.Analyze(
                    GetRenderers(referenceList),
                    textures,
                    _config.FaceViewDistance,
                    _config.BodyViewDistance
                )
                : null;

            var rawScores = _analyzer.AnalyzeBatch(textures);

            // Iterate inputs, not backend output — see ITextureAnalysisBackend contract.
            var analysisResults = new Dictionary<Texture2D, TextureAnalysisResult>();
            foreach (var kvp in textures)
            {
                var texture = kvp.Key;
                var info = kvp.Value;
                if (!rawScores.TryGetValue(texture, out var score))
                    continue;

                analysisResults[texture] = AnalysisResultHelper.BuildResult(
                    score,
                    baseSizes[texture].x,
                    baseSizes[texture].y,
                    info.IsEmission,
                    info.IsNormalMap,
                    _complexityCalc,
                    _processor
                );
            }

            // Process each texture through the full pipeline (resize → preprocess → compress → register)
            // one at a time to keep peak memory at O(1) intermediate textures.
            var processedTextures = new Dictionary<Texture2D, Texture2D>();

            foreach (var kvp in textures)
            {
                var originalTexture = kvp.Key;
                var textureInfo = kvp.Value;

                var baseSize = baseSizes[originalTexture];
                var resolved = ResolveAnalysis(
                    originalTexture,
                    textureInfo,
                    analysisResults,
                    baseSize,
                    enableLogging
                );
                if (resolved == null)
                    continue;

                var analysis = resolved.Value.Analysis;
                var formatOverride = resolved.Value.FormatOverride;

                bool isFrozen = formatOverride != null;
                bool formatOnly = textureInfo.FormatOnly;
                var formatSelector = formatOnly ? _formatOnlySelector : _formatSelector;
                TexelDensityInfo? density =
                    formatOnly ? null
                    : densities != null && densities.TryGetValue(originalTexture, out var measured)
                        ? measured
                    : (TexelDensityInfo?)null;

                // Frozen textures keep their explicit divisor.
                if (!isFrozen && density.HasValue)
                {
                    var limited = TexelDensityAnalyzer.ApplyLimit(
                        analysis,
                        baseSize,
                        density.Value,
                        _processor
                    );
                    if (enableLogging && limited.RecommendedDivisor != analysis.RecommendedDivisor)
                    {
                        Debug.Log(
                            $"[{Name}] {originalTexture.name}: "
                                + $"{density.Value.DensityAt(baseSize):F0} px/cm on the "
                                + $"{(density.Value.FromHead ? "head" : "body")} (needs "
                                + $"{density.Value.TargetDensity:F0}), divisor "
                                + $"{analysis.RecommendedDivisor}x -> {limited.RecommendedDivisor}x"
                        );
                    }
                    analysis = limited;

                    if (_config.KeepVisibleDetail)
                    {
                        var floored = TexelDensityAnalyzer.ApplyFloor(
                            analysis,
                            baseSize,
                            density.Value,
                            _config.MinDivisor,
                            _config.LowComplexityThreshold,
                            _processor
                        );
                        if (
                            enableLogging
                            && floored.RecommendedDivisor != analysis.RecommendedDivisor
                        )
                        {
                            var shrunk = new Vector2Int(
                                baseSize.x / analysis.RecommendedDivisor,
                                baseSize.y / analysis.RecommendedDivisor
                            );
                            Debug.Log(
                                $"[{Name}] {originalTexture.name}: divisor "
                                    + $"{analysis.RecommendedDivisor}x would leave "
                                    + $"{density.Value.DensityAt(shrunk):F0} px/cm on the "
                                    + $"{(density.Value.FromHead ? "head" : "body")} (needs "
                                    + $"{density.Value.TargetDensity:F0}), keeping detail: "
                                    + $"divisor {floored.RecommendedDivisor}x"
                            );
                        }
                        analysis = floored;
                    }
                }
                var sourceFormat = originalTexture.format;
                bool isNormalMap = textureInfo.IsNormalMap;
                var importedSize = new Vector2Int(originalTexture.width, originalTexture.height);
                var targetSize = _processor.CalculateTargetSize(baseSize, analysis);

                // From a larger source file, the import size is still the default: only grow
                // past it when the extra pixels would be visible.
                // Caught by the size filters: keep the imported size, only the format changes.
                if (formatOnly)
                {
                    targetSize = new Vector2Int(
                        TextureProcessor.EnsureMultipleOf4(importedSize.x),
                        TextureProcessor.EnsureMultipleOf4(importedSize.y)
                    );
                }

                string upscaleNote = null;
                if (!isFrozen && !formatOnly && !_config.RespectImportMaxSize)
                {
                    targetSize = UpscaleGate.Apply(
                        targetSize,
                        importedSize,
                        analysis.NormalizedComplexity,
                        _config.LowComplexityThreshold,
                        density,
                        out var decision
                    );
                    upscaleNote = UpscaleGate.Describe(decision, importedSize, density);
                }
                string densityNote = density.HasValue
                    ? $" | On avatar: {density.Value.DensityAt(targetSize):F0} px/cm on the "
                        + $"{(density.Value.FromHead ? "head" : "body")}, needs "
                        + $"{density.Value.TargetDensity:F0}"
                    : "";
                if (upscaleNote != null)
                    densityNote += " | " + upscaleNote;

                if (
                    !isFrozen
                    && density.HasValue
                    && TexelDensityAnalyzer.IsLimitedByMaxResolution(
                        targetSize,
                        GetFullSize(originalTexture),
                        _config.MaxResolution,
                        density.Value,
                        analysis.NormalizedComplexity,
                        _config.LowComplexityThreshold,
                        out var wanted
                    )
                )
                {
                    Debug.LogWarning(
                        $"[{Name}] {originalTexture.name}: Max Resolution {_config.MaxResolution} "
                            + $"keeps it at {targetSize.x}x{targetSize.y}, "
                            + $"{density.Value.DensityAt(targetSize):F0} px/cm on the "
                            + $"{(density.Value.FromHead ? "head" : "body")} (needs "
                            + $"{density.Value.TargetDensity:F0}). {wanted.x}x{wanted.y} would give "
                            + $"{density.Value.DensityAt(wanted):F0} px/cm for about "
                            + $"{(float)wanted.x * wanted.y / ((float)targetSize.x * targetSize.y):F0}x "
                            + "the memory; raise Max Resolution to keep more detail."
                    );
                }

                // Unity's import is already a single compression of the source file. If the
                // result would have the same size and format, keep it: recompressing would only
                // lose quality. Normal maps always run through the layout conversion, which also
                // repairs normal maps imported with the wrong texture type.
                if (
                    !isNormalMap
                    && targetSize == importedSize
                    && formatSelector.KeepsImportFormat(sourceFormat, formatOverride)
                )
                {
                    LogUnchanged(originalTexture, enableLogging, densityNote);
                    continue;
                }

                // Resize from the full-quality source when available (lock acquired and released
                // inside ResizeSingle). The normal-map layout is read from the same pixels.
                var source = _sourceCache?.Load(originalTexture);
                bool fromSource = source != null;
                NormalMapPreprocessor.SourceLayout sourceLayout;
                Texture2D resizedTexture;
                try
                {
                    sourceLayout = isNormalMap
                        ? ResolveNormalLayout(originalTexture, source, sourceFormat)
                        : NormalMapPreprocessor.SourceLayout.Auto;
                    resizedTexture = _processor.ResizeSingle(
                        fromSource ? source : originalTexture,
                        targetSize,
                        isNormalMap
                    );
                }
                finally
                {
                    if (fromSource)
                        UnityEngine.Object.DestroyImmediate(source);
                }

                if (resizedTexture == null)
                    continue;

                // Always detect alpha on the resized texture (the actual data being compressed).
                // Original-resolution alpha regions may be lost during downscaling, so checking
                // the resized output avoids choosing an alpha-capable format (BC7 at 8bpp)
                // when the compressed texture has no meaningful alpha (DXT1 at 4bpp suffices).
                bool hasAlpha = TextureFormatSelector.HasSignificantAlpha(resizedTexture);

                var targetFormat = formatSelector.ResolveTargetFormat(
                    sourceFormat,
                    isNormalMap,
                    analysis.NormalizedComplexity,
                    hasAlpha,
                    formatOverride
                );

                // A low-detail score can still mean a smooth gradient, which is exactly where
                // DXT1/ASTC 8x8 band. Test-compress the clean areas before committing to them,
                // stepping up to the cheapest format that stays clean.
                if (
                    !isNormalMap
                    && _config.AvoidCompressionArtifacts
                    && formatSelector.ChoosesFormat(sourceFormat, formatOverride)
                )
                {
                    var cleanFormat = CompressionArtifactDetector.ChooseCleanFormat(
                        resizedTexture,
                        targetFormat
                    );
                    if (cleanFormat != targetFormat && enableLogging)
                    {
                        Debug.Log(
                            $"[{Name}] {originalTexture.name}: {targetFormat} would band or show "
                                + $"blocky edges, using {cleanFormat}"
                        );
                    }
                    targetFormat = cleanFormat;
                }

                if (
                    !isNormalMap
                    && targetSize == importedSize
                    && targetFormat == sourceFormat
                    && TextureFormatInfo.IsCompressed(sourceFormat)
                )
                {
                    UnityEngine.Object.DestroyImmediate(resizedTexture);
                    LogUnchanged(originalTexture, enableLogging, densityNote);
                    continue;
                }

                bool preserveAlpha =
                    isNormalMap
                    && NormalMapPreprocessor.ShouldPreserveSemanticAlpha(
                        targetFormat,
                        sourceLayout,
                        hasAlpha
                    );

                // Save original pixels BEFORE destructive normal map preprocessing (for fallback restore)
                Color32[] originalPixels = null;
                // isReadable is always true for resizer output (new Texture2D); guard is defensive
                if (isNormalMap && resizedTexture.isReadable)
                {
                    originalPixels = resizedTexture.GetPixels32();
                }

                // Preprocess + Compress (both inside protection so PrepareForCompression
                // failures also trigger pixel restore and fallback)
                if (resizedTexture.format != targetFormat)
                {
                    if (
                        !TryPreprocessAndCompress(
                            resizedTexture,
                            sourceFormat,
                            targetFormat,
                            isNormalMap,
                            preserveAlpha,
                            sourceLayout
                        )
                    )
                    {
                        // Primary compression failed — restore pixels and attempt fallback
                        if (originalPixels != null)
                        {
                            resizedTexture.SetPixels32(originalPixels);
                            resizedTexture.Apply(resizedTexture.mipmapCount > 1);
                        }

                        if (
                            !ApplyFallbackCompression(
                                resizedTexture,
                                sourceFormat,
                                isNormalMap,
                                sourceLayout
                            )
                        )
                        {
                            UnityEngine.Object.DestroyImmediate(resizedTexture);
                            continue;
                        }
                    }
                }

                resizedTexture.name = originalTexture.name + "_compressed";

                if (enableLogging)
                {
                    var frozenInfo =
                        formatOverride.HasValue && formatOverride.Value != FrozenTextureFormat.Auto
                            ? " [FROZEN]"
                            : "";
                    if (formatOnly)
                        frozenInfo += " [FORMAT ONLY]";
                    Debug.Log(
                        $"[{Name}] {originalTexture.name}{(fromSource ? " [SOURCE]" : "")}: "
                            + $"{originalTexture.width}x{originalTexture.height} -> "
                            + $"{resizedTexture.width}x{resizedTexture.height} ({resizedTexture.format}){frozenInfo} "
                            + $"(Complexity: {analysis.NormalizedComplexity:P0}, "
                            + $"Divisor: {analysis.RecommendedDivisor}x){densityNote}"
                    );
                }

                // Enable mipmap streaming (VRChat SDK warns about textures without it)
                using var serializedTexture = new SerializedObject(resizedTexture);
                var streamingMipmaps = serializedTexture.FindProperty("m_StreamingMipmaps");
                if (streamingMipmaps != null)
                {
                    streamingMipmaps.boolValue = true;
                    serializedTexture.ApplyModifiedPropertiesWithoutUndo();
                }
                else if (!_streamingMipmapsWarningShown)
                {
                    _streamingMipmapsWarningShown = true;
                    Debug.LogWarning(
                        $"[{Name}] Could not enable streaming mipmaps: "
                            + "property 'm_StreamingMipmaps' not found. This may indicate a Unity version difference."
                    );
                }

                ReplacedObjectRegistry.Register(originalTexture, resizedTexture);

                foreach (var reference in textureInfo.References)
                {
                    reference.Material.SetTexture(reference.PropertyName, resizedTexture);
                }

                processedTextures[originalTexture] = resizedTexture;
            }

            if (enableLogging)
            {
                LogSummary(textures, processedTextures);
            }

            return (processedTextures, clonedMaterials);
        }

        /// <summary>
        /// Per-material decisions (lilToon check, no-op detection, animation veto) live in the
        /// baker (see <see cref="ILilToonBaker"/>); this drives it across the build and hands it
        /// the collector's filter and the protection pin so excluded/frozen-skipped textures are
        /// never baked and protected textures are never repainted or consumed.
        /// </summary>
        private void BakeLilToonAdjustments(List<Material> clonedMaterials, bool enableLogging)
        {
            int bakedSlots = 0;
            int skippedByAnimation = 0;

            foreach (var material in clonedMaterials)
            {
                try
                {
                    var result = _lilToonBaker.Bake(
                        material,
                        _animationUsageMap.AnimatedProperties,
                        _collector.WouldProcess,
                        IsProtectedTexture
                    );
                    bakedSlots += result.BakedSlots;
                    skippedByAnimation += result.SkippedByAnimation;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[{Name}] lilToon texture baking failed for material "
                            + $"'{material.name}': {ex.Message}. The material keeps its "
                            + "unbaked adjustments and the build continues."
                    );
                }
            }

            if (enableLogging && (bakedSlots > 0 || skippedByAnimation > 0))
            {
                Debug.Log(
                    $"[{Name}] lilToon texture baking: baked {bakedSlots} texture slot(s), "
                        + $"skipped {skippedByAnimation} bake(s) whose inputs are driven by "
                        + "animation."
                );
            }
        }

        /// <summary>
        /// Runs <see cref="UnusedSlotPruner"/> over the cloned materials with frozen textures
        /// protected, and logs what was removed.
        /// </summary>
        private void PruneUnusedSlots(List<Material> clonedMaterials, bool enableLogging)
        {
            var result = UnusedSlotPruner.Prune(
                _unusedSlotOptimizer,
                _animationUsageMap,
                clonedMaterials,
                IsFrozenTexture
            );

            if (enableLogging && (result.ClearedSlots > 0 || result.DroppedTextures > 0))
            {
                Debug.Log(
                    $"[{Name}] Unused-slot detection: cleared {result.ClearedSlots} unused texture "
                        + $"slot(s), dropped {result.DroppedTextures} now-unreferenced texture(s) "
                        + "from the build."
                );
            }
        }

        /// <summary>
        /// Textures the lilToon bake must not repaint or drop: frozen settings are an explicit
        /// user pin ("ship this texture exactly as configured"), and a texture referenced by an
        /// animation curve ships with the avatar regardless, so consuming its slot would only
        /// stop it from being collected and compressed (the same reasoning as the
        /// <see cref="UnusedSlotPruner"/> restore path).
        /// </summary>
        private bool IsProtectedTexture(Texture2D texture)
        {
            return IsFrozenTexture(texture) || _animationUsageMap.IsTextureAnimated(texture);
        }

        /// <summary>
        /// Frozen settings are an explicit user pin ("ship this texture exactly as configured"),
        /// so unused-slot detection must never remove a frozen texture from the avatar.
        /// </summary>
        private bool IsFrozenTexture(Texture2D texture)
        {
            if (_frozenLookup.Count == 0 || texture == null)
                return false;

            string assetPath = AssetDatabase.GetAssetPath(
                TextureCollector.ResolveOriginalObject(texture)
            );
            if (string.IsNullOrEmpty(assetPath))
                return false;

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            return !string.IsNullOrEmpty(guid) && _frozenLookup.ContainsKey(guid);
        }

        /// <summary>
        /// The largest image available for a texture: its source file (up to the size the
        /// source cache reads) when resizing from sources without respecting the import Max
        /// Size, otherwise the imported texture.
        /// </summary>
        private Vector2Int GetFullSize(Texture2D texture)
        {
            var imported = new Vector2Int(texture.width, texture.height);
            if (
                _config.RespectImportMaxSize
                || _sourceCache == null
                || !_sourceCache.TryGetCachedSize(texture, out var sourceSize)
            )
            {
                return imported;
            }
            return sourceSize;
        }

        private static IEnumerable<Renderer> GetRenderers(List<MaterialReference> references)
        {
            var renderers = new HashSet<Renderer>();
            foreach (var reference in references)
            {
                if (
                    reference.SourceType == MaterialSourceType.Renderer
                    && reference.SourceObject is Renderer renderer
                    && renderer != null
                )
                {
                    renderers.Add(renderer);
                }
            }
            return renderers;
        }

        /// <summary>
        /// Returns the size a texture is scaled from: its imported size, or — when not respecting
        /// the import Max Size and a full-quality source exists — the source size fit to
        /// Max Resolution. Never larger than the pixels actually available.
        /// </summary>
        private Vector2Int GetBaseSize(Texture2D texture)
        {
            var imported = new Vector2Int(texture.width, texture.height);
            if (
                _config.RespectImportMaxSize
                || _sourceCache == null
                || !_sourceCache.TryGetCachedSize(texture, out var sourceSize)
            )
            {
                return imported;
            }

            return TextureSourceCache.FitWithin(sourceSize, _config.MaxResolution);
        }

        /// <summary>
        /// Resolves the channel layout of a normal map's pixels: from the source copy when one
        /// is used, otherwise from the imported texture's format.
        /// </summary>
        private static NormalMapPreprocessor.SourceLayout ResolveNormalLayout(
            Texture2D imported,
            Texture2D source,
            TextureFormat importedFormat
        )
        {
            if (source == null)
                return NormalMapSourceLayoutDetector.Resolve(imported, importedFormat);

            return TextureSourceCache.IsRawNormalSource(imported)
                ? NormalMapPreprocessor.SourceLayout.RGB
                : NormalMapSourceLayoutDetector.DetectDXTnmLike(source);
        }

        private void LogUnchanged(Texture2D texture, bool enableLogging, string note = "")
        {
            if (enableLogging)
            {
                Debug.Log(
                    $"[{Name}] {texture.name}: {texture.width}x{texture.height} ({texture.format}) "
                        + $"already optimal, kept as imported{note}"
                );
            }
        }

        /// <summary>
        /// Resolves analysis settings for a texture (frozen override or analysis result).
        /// </summary>
        /// <returns>Resolved analysis and format override, or null if the texture should be skipped.</returns>
        private (
            TextureAnalysisResult Analysis,
            FrozenTextureFormat? FormatOverride
        )? ResolveAnalysis(
            Texture2D originalTexture,
            TextureInfo textureInfo,
            Dictionary<Texture2D, TextureAnalysisResult> analysisResults,
            Vector2Int baseSize,
            bool enableLogging
        )
        {
            string guid = textureInfo.AssetGuid;

            // Check if texture is frozen (non-skipped frozen textures are still in collection)
            if (
                !string.IsNullOrEmpty(guid)
                && _frozenLookup.TryGetValue(guid, out var frozenSettings)
                && !frozenSettings.Skip
            )
            {
                int divisor = frozenSettings.Divisor;
                Vector2Int resolution = _processor.CalculateNewDimensions(
                    baseSize.x,
                    baseSize.y,
                    divisor
                );

                // Alpha detection deferred to after resize (resized textures are always readable)
                var analysis = new TextureAnalysisResult(
                    AnalysisConstants.DefaultComplexityScore,
                    divisor,
                    resolution
                );

                if (enableLogging)
                {
                    Debug.Log(
                        $"[{Name}] Using frozen settings for '{originalTexture.name}': "
                            + $"Divisor={divisor}, Format={frozenSettings.Format}"
                    );
                }

                return (analysis, frozenSettings.Format);
            }

            if (!analysisResults.TryGetValue(originalTexture, out var analysisResult))
            {
                Debug.LogWarning(
                    $"[{Name}] Skipping texture '{originalTexture.name}': analysis failed"
                );
                return null;
            }

            return (analysisResult, null);
        }

        /// <summary>
        /// Applies normal map preprocessing (if applicable) and compresses the texture.
        /// Both steps are protected so that a failure in either triggers fallback.
        /// </summary>
        /// <returns>True if preprocessing and compression both succeeded, false otherwise.</returns>
        private bool TryPreprocessAndCompress(
            Texture2D texture,
            TextureFormat sourceFormat,
            TextureFormat targetFormat,
            bool isNormalMap,
            bool preserveAlpha,
            NormalMapPreprocessor.SourceLayout sourceLayout
        )
        {
            try
            {
                if (isNormalMap)
                {
                    _normalMapPreprocessor.PrepareForCompression(
                        texture,
                        sourceFormat,
                        targetFormat,
                        preserveAlpha,
                        sourceLayout
                    );
                }

                EditorUtility.CompressTexture(
                    texture,
                    targetFormat,
                    TextureCompressionQuality.Best
                );
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(
                    $"[{Name}] Failed to compress texture to {targetFormat}: {e.Message}. "
                        + "Attempting fallback."
                );
                return false;
            }
        }

        /// <summary>
        /// Applies fallback compression when primary compression fails.
        /// </summary>
        private bool ApplyFallbackCompression(
            Texture2D texture,
            TextureFormat sourceFormat,
            bool isNormalMap,
            NormalMapPreprocessor.SourceLayout sourceLayout
        )
        {
            try
            {
                var platform = TextureFormatSelector.ResolvePlatform(_config.TargetPlatform);
                var fallbackFormat =
                    platform == CompressionPlatform.Mobile
                        ? TextureFormat.ASTC_6x6
                        : TextureFormat.DXT5;

                if (isNormalMap)
                {
                    // Fallback formats (DXT5/ASTC) never preserve alpha; only BC7 can.
                    _normalMapPreprocessor.PrepareForCompression(
                        texture,
                        sourceFormat,
                        fallbackFormat,
                        preserveAlpha: false,
                        sourceLayout
                    );
                }

                EditorUtility.CompressTexture(
                    texture,
                    fallbackFormat,
                    TextureCompressionQuality.Normal
                );
                Debug.Log($"[{Name}] Fallback compression to {fallbackFormat} succeeded.");
                return true;
            }
            catch (System.Exception fallbackEx)
            {
                Debug.LogError(
                    $"[{Name}] Fallback compression also failed: {fallbackEx.Message}. "
                        + "Texture will remain uncompressed."
                );
                return false;
            }
        }

        private static long GetUploadedSize(Texture2D texture)
        {
            return MemoryCalculator.CalculateCompressedMemory(
                texture.width,
                texture.height,
                texture.format,
                texture.mipmapCount
            );
        }

        private void LogSummary(
            Dictionary<Texture2D, TextureInfo> original,
            Dictionary<Texture2D, Texture2D> processed
        )
        {
            // Same calculation as the preview: the size the textures take once uploaded. The
            // editor's runtime memory figure counts CPU-side copies too and reads about double.
            long originalSize = 0;
            long compressedSize = 0;

            foreach (var kvp in original)
            {
                var origTex = kvp.Key;
                long before = GetUploadedSize(origTex);
                originalSize += before;

                // Textures kept as imported still ship at their original size.
                compressedSize += processed.TryGetValue(origTex, out var compTex)
                    ? GetUploadedSize(compTex)
                    : before;
            }

            float savings = originalSize > 0 ? 1f - (float)compressedSize / originalSize : 0f;

            Debug.Log(
                $"[{Name}] Complete: "
                    + $"{originalSize / 1024f / 1024f:F2}MB -> {compressedSize / 1024f / 1024f:F2}MB "
                    + $"({savings:P0} reduction)"
            );
        }
    }
}
