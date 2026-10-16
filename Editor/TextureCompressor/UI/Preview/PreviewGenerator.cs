using System.Collections.Generic;
using System.Linq;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor.ui;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture.ui
{
    /// <summary>
    /// Generates preview data for texture compression.
    /// </summary>
    internal sealed class PreviewGenerator
    {
        private static LruCache<
            (string guid, Hash128 contentHash, int analysisHash, bool isNormalMap, bool isEmission),
            TextureAnalysisResult
        > AnalysisCache = new(256);

        [InitializeOnLoadMethod]
        private static void ClearCacheOnDomainReload()
        {
            AnalysisCache = new(256);
        }

        /// <summary>
        /// Number of textures that will be processed.
        /// </summary>
        public int ProcessedCount { get; private set; }

        /// <summary>
        /// Number of frozen textures.
        /// </summary>
        public int FrozenCount { get; private set; }

        /// <summary>
        /// Number of skipped textures.
        /// </summary>
        public int SkippedCount { get; private set; }

        /// <summary>
        /// Generates preview data for the given configuration.
        /// </summary>
        // No defaults for the backend preferences: they feed ComputeSettingsHash's
        // staleness pair, and a defaulted call site would silently desync the two.
        public TexturePreviewData[] Generate(
            TextureCompressor config,
            AnalysisBackendPreference backendPreference,
            ResizeBackendPreference resizeBackendPreference,
            GameObject avatarRoot = null
        )
        {
            // The avatar is the component's object, except during a build, where the settings sit
            // on a holder outside the avatar copy
            var root = avatarRoot != null ? avatarRoot : config.gameObject;

            // As in the build: frozen textures that apply to slot swaps pass their settings to
            // the textures in their slots
            var followedBy = new Dictionary<string, string>();
            var frozenTextures = FrozenSlotResolver.Expand(
                config.FrozenTextures,
                root,
                followedBy
            );
            var frozenLookup = FrozenTextureSettings.BuildLookup(frozenTextures);

            var collector = new TextureCollector(
                config.MinSourceSize,
                config.SkipIfSmallerThan,
                config.ProcessMainTextures,
                config.ProcessNormalMaps,
                config.ProcessEmissionMaps,
                config.ProcessOtherTextures,
                config.SkipUnknownUncompressedTextures,
                config.ExcludedPaths,
                config.ExcludedTextures,
                frozenTextures,
                config.RespectImportMaxSize
                || !TextureCompressorPreferences.UseSourceFiles
                || !config.SizeFiltersUseSourceSize
                    ? null
                    : texture => GetBaseSize(texture, config),
                config.ReformatFilteredTextures
            );

            var processor = new TextureProcessor(
                config.MinResolution,
                config.MaxResolution,
                config.ForcePowerOfTwo,
                resizeBackendPreference
            );

            var complexityCalc = new ComplexityCalculator(
                config.HighComplexityThreshold,
                config.LowComplexityThreshold,
                config.MinDivisor,
                config.MaxDivisor
            );

            var formatOnlySelector = new TextureFormatSelector(
                config.TargetPlatform,
                config.UseHighQualityFormatForHighComplexity,
                config.HighComplexityThreshold,
                respectImportFormat: false
            );

            var formatSelector = new TextureFormatSelector(
                config.TargetPlatform,
                config.UseHighQualityFormatForHighComplexity,
                config.HighComplexityThreshold,
                config.RespectImportFormat
            );

            var allTextures = collector.CollectAll(root);

            // Collect additional materials from animations and components
            var additionalMaterialRefs = new List<MaterialReference>();
            additionalMaterialRefs.AddRange(
                MaterialCollector.CollectFromAnimator(root)
            );
            additionalMaterialRefs.AddRange(
                MaterialCollector.CollectFromComponents(root)
            );

            var additionalMaterials = MaterialCollector.GetDistinctMaterials(
                additionalMaterialRefs
            );
            if (additionalMaterials.Any())
            {
                collector.CollectFromMaterials(additionalMaterials, allTextures, collectAll: true);
            }

            if (allTextures.Count == 0)
            {
                ProcessedCount = 0;
                FrozenCount = 0;
                SkippedCount = 0;
                return new TexturePreviewData[0];
            }

            var processedTextures = new Dictionary<Texture2D, TextureInfo>();
            foreach (var kvp in allTextures)
            {
                if (!kvp.Value.IsProcessed)
                    continue;

                processedTextures[kvp.Key] = kvp.Value;
            }

            var analyzer = new TextureAnalyzer(
                config.Strategy,
                config.FastWeight,
                config.HighAccuracyWeight,
                config.PerceptualWeight,
                processor,
                backendPreference
            );

            // Use cache to avoid re-analyzing unchanged textures
            int analysisHash = ComputeAnalysisHash(config, backendPreference);
            var analysisResults = new Dictionary<Texture2D, TextureAnalysisResult>();

            var assetInfoCache = new Dictionary<Texture2D, (string guid, Hash128 contentHash)>();

            if (processedTextures.Count > 0)
            {
                var needsAnalysis = new Dictionary<Texture2D, TextureInfo>();

                foreach (var kvp in processedTextures)
                {
                    string path = AssetDatabase.GetAssetPath(kvp.Key);
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    var contentHash = AssetDatabase.GetAssetDependencyHash(path);
                    assetInfoCache[kvp.Key] = (guid, contentHash);
                    var cacheKey = (
                        guid,
                        contentHash,
                        analysisHash,
                        kvp.Value.IsNormalMap,
                        kvp.Value.IsEmission
                    );

                    if (
                        !string.IsNullOrEmpty(guid)
                        && AnalysisCache.TryGetValue(cacheKey, out var cached)
                    )
                    {
                        analysisResults[kvp.Key] = cached;
                    }
                    else
                    {
                        needsAnalysis[kvp.Key] = kvp.Value;
                    }
                }

                if (needsAnalysis.Count > 0)
                {
                    var rawScores = analyzer.AnalyzeBatch(needsAnalysis);
                    // Iterate inputs, not backend output — see ITextureAnalysisBackend contract.
                    foreach (var kvp in needsAnalysis)
                    {
                        var texture = kvp.Key;
                        var texInfo = kvp.Value;
                        if (!rawScores.TryGetValue(texture, out var score))
                            continue;

                        var baseSize = GetBaseSize(texture, config);
                        var result = AnalysisResultHelper.BuildResult(
                            score,
                            baseSize.x,
                            baseSize.y,
                            texInfo.IsEmission,
                            texInfo.IsNormalMap,
                            complexityCalc,
                            processor
                        );
                        analysisResults[texture] = result;

                        if (
                            assetInfoCache.TryGetValue(texture, out var assetInfo)
                            && !string.IsNullOrEmpty(assetInfo.guid)
                        )
                        {
                            AnalysisCache.Set(
                                (
                                    assetInfo.guid,
                                    assetInfo.contentHash,
                                    analysisHash,
                                    texInfo.IsNormalMap,
                                    texInfo.IsEmission
                                ),
                                result
                            );
                        }
                    }
                }
            }

            var densities = config.LimitByOnAvatarSize
                ? TexelDensityAnalyzer.Analyze(
                    root.GetComponentsInChildren<Renderer>(true)
                        .Where(r => !ComponentUtils.IsEditorOnly(r.gameObject)),
                    processedTextures,
                    config.FaceViewDistance,
                    config.BodyViewDistance
                )
                : new Dictionary<Texture2D, TexelDensityInfo>();

            // As in the build: alpha and banding are checked on the resized source file when
            // source files are used. Unity's import is already compressed, and its blocks change
            // which formats look clean, so checking it picks different formats than the build.
            var sourceCache = TextureCompressorPreferences.UseSourceFiles
                ? new TextureSourceCache()
                : null;
            sourceCache?.Prepare(processedTextures.Keys);

            var processedList = new List<TexturePreviewData>();
            var frozenList = new List<TexturePreviewData>();
            var skippedList = new List<TexturePreviewData>();

            foreach (var kvp in allTextures)
            {
                var tex = kvp.Key;
                var info = kvp.Value;
                // Format and size on the platform being compressed for (see PlatformImport)
                var import = PlatformImport.Get(tex, config.TargetPlatform);

                string guid;
                if (assetInfoCache.TryGetValue(tex, out var cachedAssetInfo))
                {
                    guid = cachedAssetInfo.guid;
                }
                else
                {
                    string assetPath = AssetDatabase.GetAssetPath(tex);
                    guid = AssetDatabase.AssetPathToGUID(assetPath);
                }

                // Skip textures with invalid GUID (e.g., runtime-generated textures)
                if (string.IsNullOrEmpty(guid))
                    continue;

                // Check if texture is frozen
                frozenLookup.TryGetValue(guid, out var frozenSettings);
                bool isFrozen = frozenSettings != null;

                if (info.IsProcessed && analysisResults.TryGetValue(tex, out var analysis))
                {
                    long originalMemory = MemoryCalculator.CalculateCompressedMemory(
                        import.Size.x,
                        import.Size.y,
                        import.Format,
                        import.MipCount
                    );
                    bool isNormalMap = info.IsNormalMap;

                    int divisor;
                    Vector2Int recommendedSize;
                    TextureFormat targetFormat;
                    bool hasAlpha;
                    var baseSize = GetBaseSize(tex, config);
                    var selector = info.FormatOnly ? formatOnlySelector : formatSelector;
                    var formatOnlySize = new Vector2Int(
                        TextureProcessor.EnsureMultipleOf4(import.Size.x),
                        TextureProcessor.EnsureMultipleOf4(import.Size.y)
                    );

                    if (isFrozen && !frozenSettings.Skip)
                    {
                        divisor = frozenSettings.Divisor;
                        recommendedSize = processor.CalculateNewDimensions(
                            baseSize.x,
                            baseSize.y,
                            divisor
                        );
                        // Match the build pipeline: frozen textures always flow through
                        // the resize/copy path before alpha detection, even when dimensions
                        // are unchanged.
                        var frozenAnalysis = new TextureAnalysisResult(
                            AnalysisConstants.DefaultComplexityScore,
                            divisor,
                            recommendedSize
                        );
                        recommendedSize = processor.CalculateTargetSize(baseSize, frozenAnalysis);
                        if (info.FormatOnly)
                        {
                            recommendedSize = formatOnlySize;
                            divisor = 1;
                        }
                        var resizedTex = ResizeFromSource(processor, sourceCache, tex, recommendedSize, isNormalMap);
                        try
                        {
                            hasAlpha = TextureFormatSelector.HasSignificantAlpha(
                                resizedTex != null ? resizedTex : tex
                            );
                            targetFormat = selector.ResolveTargetFormat(
                                import.Format,
                                isNormalMap,
                                AnalysisConstants.DefaultComplexityScore,
                                hasAlpha,
                                frozenSettings.Format
                            );
                            targetFormat = AvoidArtifacts(
                                config,
                                selector,
                                tex,
                                import.Format,
                                resizedTex,
                                isNormalMap,
                                targetFormat,
                                frozenSettings.Format
                            );
                        }
                        finally
                        {
                            if (resizedTex != null)
                                Object.DestroyImmediate(resizedTex);
                        }
                    }
                    else
                    {
                        if (!info.FormatOnly && densities.TryGetValue(tex, out var density))
                        {
                            analysis = TexelDensityAnalyzer.ApplyLimit(
                                analysis,
                                baseSize,
                                density,
                                processor
                            );
                            if (config.KeepVisibleDetail)
                            {
                                analysis = TexelDensityAnalyzer.ApplyFloor(
                                    analysis,
                                    baseSize,
                                    density,
                                    config.MinDivisor,
                                    config.LowComplexityThreshold,
                                    processor
                                );
                            }
                        }
                        divisor = analysis.RecommendedDivisor;
                        recommendedSize = processor.CalculateTargetSize(baseSize, analysis);
                        if (info.FormatOnly)
                        {
                            recommendedSize = formatOnlySize;
                            divisor = 1;
                        }
                        else if (!config.RespectImportMaxSize)
                        {
                            recommendedSize = UpscaleGate.Apply(
                                recommendedSize,
                                import.Size,
                                analysis.NormalizedComplexity,
                                config.LowComplexityThreshold,
                                densities.TryGetValue(tex, out var measuredDensity)
                                    ? measuredDensity
                                    : (TexelDensityInfo?)null,
                                out _
                            );
                        }
                        // Match the build pipeline: detect alpha on the resized texture
                        // so that alpha regions lost during downscaling don't cause an
                        // unnecessary alpha-capable format (e.g. BC7 instead of DXT1).
                        var resizedTex = ResizeFromSource(processor, sourceCache, tex, recommendedSize, isNormalMap);
                        try
                        {
                            hasAlpha = TextureFormatSelector.HasSignificantAlpha(
                                resizedTex != null ? resizedTex : tex
                            );
                            targetFormat = selector.ResolveTargetFormat(
                                import.Format,
                                isNormalMap,
                                analysis.NormalizedComplexity,
                                hasAlpha,
                                null
                            );
                            targetFormat = AvoidArtifacts(
                                config,
                                selector,
                                tex,
                                import.Format,
                                resizedTex,
                                isNormalMap,
                                targetFormat,
                                null
                            );
                        }
                        finally
                        {
                            if (resizedTex != null)
                                Object.DestroyImmediate(resizedTex);
                        }
                    }

                    Vector2Int? maxResolutionWanted = null;
                    if (
                        !isFrozen
                        && !info.FormatOnly
                        && densities.TryGetValue(tex, out var limitDensity)
                        && TexelDensityAnalyzer.IsLimitedByMaxResolution(
                            recommendedSize,
                            GetFullSize(tex, config),
                            config.MaxResolution,
                            limitDensity,
                            analysis.NormalizedComplexity,
                            config.LowComplexityThreshold,
                            out var wanted
                        )
                    )
                    {
                        maxResolutionWanted = wanted;
                    }

                    long estimatedMemory = MemoryCalculator.CalculateCompressedMemory(
                        recommendedSize.x,
                        recommendedSize.y,
                        targetFormat,
                        import.MipCount
                    );

                    var previewData = new TexturePreviewData
                    {
                        Texture = tex,
                        Guid = guid,
                        Complexity = analysis.NormalizedComplexity,
                        RecommendedDivisor = divisor,
                        OriginalSize = import.Size,
                        RecommendedSize = recommendedSize,
                        TextureType = info.TextureType,
                        IsProcessed = true,
                        SkipReason = SkipReason.None,
                        OriginalMemory = originalMemory,
                        EstimatedMemory = estimatedMemory,
                        IsNormalMap = isNormalMap,
                        PredictedFormat = targetFormat,
                        HasAlpha = hasAlpha,
                        IsFrozen = isFrozen,
                        FrozenSettings = frozenSettings,
                        FollowsGuid = followedBy.TryGetValue(guid, out var follows) ? follows : null,
                        Density = densities.TryGetValue(tex, out var measured)
                            ? measured
                            : (TexelDensityInfo?)null,
                        BaseSize = baseSize,
                        MaxResolutionWanted = maxResolutionWanted,
                    };

                    if (isFrozen)
                    {
                        frozenList.Add(previewData);
                    }
                    else
                    {
                        processedList.Add(previewData);
                    }
                }
                else
                {
                    long originalMemory = MemoryCalculator.CalculateCompressedMemory(
                        import.Size.x,
                        import.Size.y,
                        import.Format,
                        import.MipCount
                    );

                    skippedList.Add(
                        new TexturePreviewData
                        {
                            Texture = tex,
                            Guid = guid,
                            Complexity = 0f,
                            RecommendedDivisor = 1,
                            OriginalSize = import.Size,
                            RecommendedSize = import.Size,
                            TextureType = info.TextureType,
                            IsProcessed = false,
                            SkipReason = info.SkipReason,
                            OriginalMemory = originalMemory,
                            EstimatedMemory = originalMemory,
                            IsNormalMap = info.IsNormalMap,
                            PredictedFormat = null,
                            HasAlpha = false,
                            IsFrozen = isFrozen,
                            FrozenSettings = frozenSettings,
                            FollowsGuid = followedBy.TryGetValue(guid, out var skippedFollows)
                                ? skippedFollows
                                : null,
                        }
                    );
                }
            }

            ProcessedCount = processedList.Count;
            FrozenCount = frozenList.Count;
            SkippedCount = skippedList.Count;

            // Sort by file path
            var pathCache = new Dictionary<string, string>();
            foreach (var data in processedList.Concat(frozenList).Concat(skippedList))
            {
                if (!string.IsNullOrEmpty(data.Guid) && !pathCache.ContainsKey(data.Guid))
                    pathCache[data.Guid] = AssetDatabase.GUIDToAssetPath(data.Guid);
            }

            System.Comparison<TexturePreviewData> pathComparison = (a, b) =>
                string.Compare(
                    pathCache.TryGetValue(a.Guid, out var pathA) ? pathA : "",
                    pathCache.TryGetValue(b.Guid, out var pathB) ? pathB : "",
                    System.StringComparison.Ordinal
                );

            processedList.Sort(pathComparison);
            frozenList.Sort(pathComparison);
            skippedList.Sort(pathComparison);

            var allPreviewData = new List<TexturePreviewData>(
                processedList.Count + frozenList.Count + skippedList.Count
            );
            allPreviewData.AddRange(processedList);
            allPreviewData.AddRange(frozenList);
            allPreviewData.AddRange(skippedList);

            return allPreviewData.ToArray();
        }

        /// <summary>
        /// Computes a hash of analysis-affecting settings only (Strategy, Weights, Thresholds, Resolution).
        /// Used as the cache key for analysis results.
        /// </summary>
        /// <summary>
        /// Mirrors the build's base size: the imported size, or the source file's size fit to
        /// Max Resolution when not respecting the import Max Size. Read from the importer, so the
        /// preview never imports anything.
        /// </summary>
        /// <summary>
        /// Mirrors the build: the largest image available, the source file (up to the size the
        /// source cache reads) when not respecting the import Max Size, else the imported size.
        /// </summary>
        /// <summary>
        /// Resizes the texture's source file when one is cached, as the build does, otherwise
        /// the imported texture. The caller destroys the result.
        /// </summary>
        private static Texture2D ResizeFromSource(
            TextureProcessor processor,
            TextureSourceCache sourceCache,
            Texture2D texture,
            Vector2Int size,
            bool isNormalMap
        )
        {
            var source = sourceCache?.Load(texture);
            try
            {
                return processor.ResizeSingle(source != null ? source : texture, size, isNormalMap);
            }
            finally
            {
                if (source != null)
                    Object.DestroyImmediate(source);
            }
        }

        private static Vector2Int GetFullSize(Texture2D texture, TextureCompressor config)
        {
            var imported = PlatformImport.Get(texture, config.TargetPlatform).Size;
            if (
                config.RespectImportMaxSize
                || !TextureCompressorPreferences.UseSourceFiles
                || !TextureSourceCache.TryGetSourceSize(texture, out var sourceSize)
            )
            {
                return imported;
            }
            return TextureSourceCache.FitWithin(sourceSize, TextureSourceCache.MaxSourceSize);
        }

        /// <summary>
        /// As <see cref="TextureSourceCache.GetPlannedBaseSize"/>, with the imported size taken
        /// from the platform being compressed for.
        /// </summary>
        private static Vector2Int GetBaseSize(Texture2D texture, TextureCompressor config)
        {
            if (
                !config.RespectImportMaxSize
                && TextureCompressorPreferences.UseSourceFiles
                && TextureSourceCache.TryGetSourceSize(texture, out var sourceSize)
            )
            {
                return TextureSourceCache.FitWithin(sourceSize, config.MaxResolution);
            }
            return PlatformImport.Get(texture, config.TargetPlatform).Size;
        }

        /// <summary>
        /// Mirrors the build: steps a low-bitrate format the compressor picked up to the
        /// cheapest format whose test compression of the clean areas shows no banding or
        /// blocky edges.
        /// </summary>
        private static TextureFormat AvoidArtifacts(
            TextureCompressor config,
            TextureFormatSelector formatSelector,
            Texture2D texture,
            TextureFormat importFormat,
            Texture2D resized,
            bool isNormalMap,
            TextureFormat targetFormat,
            FrozenTextureFormat? frozenFormat
        )
        {
            if (
                resized != null
                && !isNormalMap
                && config.AvoidCompressionArtifacts
                && formatSelector.ChoosesFormat(importFormat, frozenFormat)
            )
            {
                return CompressionArtifactDetector.ChooseCleanFormat(resized, targetFormat);
            }

            return targetFormat;
        }

        private static int ComputeAnalysisHash(
            TextureCompressor config,
            AnalysisBackendPreference backendPreference
        )
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + config.Strategy.GetHashCode();
                hash = hash * 31 + config.FastWeight.GetHashCode();
                hash = hash * 31 + config.HighAccuracyWeight.GetHashCode();
                hash = hash * 31 + config.PerceptualWeight.GetHashCode();
                hash = hash * 31 + config.HighComplexityThreshold.GetHashCode();
                hash = hash * 31 + config.LowComplexityThreshold.GetHashCode();
                hash = hash * 31 + config.MinDivisor;
                hash = hash * 31 + config.MaxDivisor;
                hash = hash * 31 + config.MaxResolution;
                hash = hash * 31 + config.MinResolution;
                hash = hash * 31 + config.ForcePowerOfTwo.GetHashCode();
                hash = hash * 31 + config.RespectImportMaxSize.GetHashCode();
                hash = hash * 31 + TextureCompressorPreferences.UseSourceFiles.GetHashCode();
                hash = hash * 31 + backendPreference.GetHashCode();
                return hash;
            }
        }

        /// <summary>
        /// Computes a hash of the configuration for change detection.
        /// </summary>
        public static int ComputeSettingsHash(
            TextureCompressor config,
            AnalysisBackendPreference backendPreference,
            ResizeBackendPreference resizeBackendPreference
        )
        {
            unchecked
            {
                // Start from analysis hash (Strategy, Weights, Thresholds, Resolution)
                int hash = ComputeAnalysisHash(config, backendPreference);
                hash = hash * 31 + resizeBackendPreference.GetHashCode();
                hash = hash * 31 + config.Preset.GetHashCode();
                hash = hash * 31 + config.ProcessMainTextures.GetHashCode();
                hash = hash * 31 + config.ProcessNormalMaps.GetHashCode();
                hash = hash * 31 + config.ProcessEmissionMaps.GetHashCode();
                hash = hash * 31 + config.ProcessOtherTextures.GetHashCode();
                hash = hash * 31 + config.SkipUnknownUncompressedTextures.GetHashCode();
                hash = hash * 31 + config.MinSourceSize;
                hash = hash * 31 + config.SkipIfSmallerThan;
                hash = hash * 31 + config.ExcludedPaths.Count;
                foreach (var path in config.ExcludedPaths)
                {
                    hash = hash * 31 + (path?.GetHashCode() ?? 0);
                }
                hash = hash * 31 + config.ExcludedTextures.Count;
                foreach (var tex in config.ExcludedTextures)
                {
                    hash = hash * 31 + (tex != null ? tex.GetInstanceID() : 0);
                }
                hash = hash * 31 + config.TargetPlatform.GetHashCode();
                hash = hash * 31 + config.UseHighQualityFormatForHighComplexity.GetHashCode();
                hash = hash * 31 + config.RespectImportFormat.GetHashCode();
                hash = hash * 31 + config.AvoidCompressionArtifacts.GetHashCode();
                hash = hash * 31 + config.RespectImportMaxSize.GetHashCode();
                hash = hash * 31 + TextureCompressorPreferences.UseSourceFiles.GetHashCode();
                hash = hash * 31 + config.gameObject.GetInstanceID();
                hash = hash * 31 + config.LimitByOnAvatarSize.GetHashCode();
                hash = hash * 31 + config.SizeFiltersUseSourceSize.GetHashCode();
                hash = hash * 31 + config.ReformatFilteredTextures.GetHashCode();
                hash = hash * 31 + config.KeepVisibleDetail.GetHashCode();
                hash = hash * 31 + config.FaceViewDistance.GetHashCode();
                hash = hash * 31 + config.BodyViewDistance.GetHashCode();

                // Include frozen textures in hash
                foreach (var frozen in config.FrozenTextures)
                {
                    hash = hash * 31 + (frozen.TextureGuid?.GetHashCode() ?? 0);
                    hash = hash * 31 + frozen.Divisor;
                    hash = hash * 31 + frozen.Format.GetHashCode();
                    hash = hash * 31 + frozen.Skip.GetHashCode();
                    hash = hash * 31 + frozen.ApplyToSlotSwaps.GetHashCode();
                    hash = hash * 31 + (frozen.Slots?.Count ?? 0);
                }

                return hash;
            }
        }
    }
}
