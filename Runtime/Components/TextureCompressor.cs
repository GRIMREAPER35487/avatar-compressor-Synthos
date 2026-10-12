using System.Collections.Generic;
using Synthos.TextureAnalysis;
using UnityEngine;
using VRC.SDKBase;

namespace dev.limitex.avatar.compressor
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Compressor/LAC Texture Compressor")]
    internal sealed class TextureCompressor : MonoBehaviour, IEditorOnly
    {
        [Header("Preset")]
        [Tooltip("Quick preset selection for common use cases")]
        public CompressorPreset Preset = CompressorPreset.Balanced;

        [Tooltip("Custom preset asset for saving and loading custom settings")]
        public CustomTextureCompressorPreset CustomPresetAsset;

        [Header("Analysis Strategy")]
        [Tooltip("Complexity analysis method")]
        public AnalysisStrategyType Strategy = AnalysisStrategyType.Combined;

        [Header("Combined Strategy Weights")]
        [Range(0f, 1f)]
        public float FastWeight = 0.3f;

        [Range(0f, 1f)]
        public float HighAccuracyWeight = 0.5f;

        [Range(0f, 1f)]
        public float PerceptualWeight = 0.2f;

        [Header("Complexity Thresholds")]
        [Tooltip("Textures with complexity above this will use minimal compression")]
        [Range(0f, 1f)]
        public float HighComplexityThreshold = 0.7f;

        [Tooltip("Textures with complexity below this will use maximum compression")]
        [Range(0f, 1f)]
        public float LowComplexityThreshold = 0.2f;

        [Header("Resolution Settings")]
        [Tooltip("Minimum resolution divisor (1 = no reduction)")]
        [Range(1, 4)]
        public int MinDivisor = 1;

        [Tooltip("Maximum resolution divisor")]
        [Range(2, 16)]
        public int MaxDivisor = 8;

        [Tooltip("Maximum output resolution")]
        public int MaxResolution = 2048;

        [Tooltip("Minimum output resolution")]
        public int MinResolution = 64;

        [Tooltip("Force output to power of 2 dimensions")]
        public bool ForcePowerOfTwo = true;

        [Header("Texture Filters")]
        [Tooltip("Process main textures (_MainTex, _BaseMap, etc.)")]
        public bool ProcessMainTextures = true;

        [Tooltip("Process normal maps")]
        public bool ProcessNormalMaps = true;

        [Tooltip("Process emission textures")]
        public bool ProcessEmissionMaps = true;

        [Tooltip("Process other textures (metallic, roughness, etc.)")]
        public bool ProcessOtherTextures = true;

        [Tooltip(
            "Skip uncompressed textures assigned to unknown shader properties. "
                + "Uncompressed textures on unrecognized properties may contain non-visual data "
                + "(e.g., masks, LUTs) that compression could corrupt."
        )]
        public bool SkipUnknownUncompressedTextures = true;

        [Header("Unused Texture Detection")]
        [Tooltip(
            "Clear texture slots whose lilToon feature toggle (e.g. _UseEmission) is off and "
                + "not animated, dropping any texture that becomes unreferenced from the upload. "
                + "Feature toggles driven by animation are always treated as used, so "
                + "menu-toggled features are never broken. Frozen textures and textures "
                + "referenced by animation curves are never removed. Acts per material: "
                + "exclusion filters control compression only and do not keep an unused slot's "
                + "texture in the upload. Only runs during the avatar build and is not reflected "
                + "in the inspector preview."
        )]
        public bool DetectUnusedTextures = true;

        [Header("lilToon Texture Baking")]
        [Tooltip(
            "Bake lilToon texture adjustments during the avatar build: main-color adjustments "
                + "(hue/saturation/gradation and active 2nd/3rd layers) and the alpha mask into "
                + "the main texture, and outline tone correction into the outline texture — so "
                + "the adjusted textures ship instead of the adjustment textures and per-pixel "
                + "shader work. Any bake whose input property is driven by animation is skipped "
                + "entirely, and colors (_Color, _OutlineColor) are never baked, so color-change "
                + "animations keep working. Frozen textures and textures referenced by animation "
                + "curves are never baked or consumed, and excluded textures are never baked. "
                + "Requires lilToon; does nothing when it is not installed."
        )]
        public bool BakeLilToonTextures = true;

        [Header("Source Textures")]
        [Tooltip(
            "Use the texture's import Max Size as the starting size. When off, the full-quality "
                + "source file (fit to Max Resolution) is the starting size, so textures the "
                + "creator imported smaller can come out larger."
        )]
        public bool RespectImportMaxSize = true;

        [Tooltip(
            "With Respect import Max Size off, the size filters (Min Source Size, Skip If Smaller "
                + "Than) check the source file's size, so a texture imported small from a large "
                + "file is still processed. When off, they check the imported size, so such "
                + "textures are skipped and left exactly as imported."
        )]
        public bool SizeFiltersUseSourceSize = true;

        [Tooltip(
            "Keep the compression format the texture was imported with for this platform. When "
                + "off, the format is chosen from each texture's detail and alpha, which can make "
                + "detailed textures larger and simple ones smaller."
        )]
        public bool RespectImportFormat = true;

        [Tooltip(
            "When the compressor picks DXT1/DXT5 (ASTC 6x6/8x8 on mobile), test-compress the "
                + "texture's smooth and clean areas first. If it would show stair-stepping or "
                + "blocky color edges, use BC7 (ASTC 4x4) instead."
        )]
        public bool AvoidCompressionArtifacts = true;

        [Header("On-Avatar Size")]
        [Tooltip(
            "Shrink textures that are sharper than anyone could see, judged from how large the "
                + "surface they cover is on the avatar (a 2048 texture on an earring). Only ever "
                + "makes textures smaller than the detail analysis would, never larger."
        )]
        public bool LimitByOnAvatarSize = true;

        [Tooltip(
            "Do not shrink a detailed texture whose surface is already less sharp than the view "
                + "distance needs (typically skin and clothing that cover most of the avatar). "
                + "Min Divisor still applies. Low-detail textures such as AO or flat colors are "
                + "still reduced."
        )]
        public bool KeepVisibleDetail = true;

        [Tooltip(
            "How closely the head (face, eyes, hair) can be looked at, in cm. Smaller keeps "
                + "more detail. 15 cm is a face pressed up to a mirror."
        )]
        [Min(1f)]
        public float FaceViewDistance = 15f;

        [Tooltip(
            "How closely the rest of the avatar can be looked at, in cm. Smaller keeps more "
                + "detail. 30 cm is looking at your hands or a close-up photo."
        )]
        [Min(1f)]
        public float BodyViewDistance = 30f;

        [Header("Size Filters")]
        [Tooltip("Only process textures larger than this size")]
        public int MinSourceSize = 256;

        [Tooltip("Skip textures smaller than or equal to this size")]
        public int SkipIfSmallerThan = 128;

        [Tooltip(
            "Textures caught by the size filters keep their imported size but still get their "
                + "compression format chosen (from detail and alpha, with the banding check), "
                + "even when Respect import format is on. When off, they are left exactly as "
                + "imported."
        )]
        public bool ReformatFilteredTextures = false;

        [Header("Excluded Textures")]
        [Tooltip("Specific textures to exclude from compression")]
        public List<Texture2D> ExcludedTextures = new List<Texture2D>();

        [Header("Path Exclusions")]
        [Tooltip("Texture paths starting with these prefixes will be excluded from compression")]
        public List<string> ExcludedPaths = new List<string>(ExcludedPathPresets.GetDefaultPaths());

        [Header("Compression Format")]
        [Tooltip(
            "Target platform for compression format selection (Auto detects from build target)"
        )]
        public CompressionPlatform TargetPlatform = CompressionPlatform.Auto;

        [Tooltip("Use BC7/ASTC_4x4 format for high complexity textures (highest quality)")]
        public bool UseHighQualityFormatForHighComplexity = true;

        [Header("Frozen Textures")]
        [Tooltip("Textures with manually specified compression settings")]
        public List<FrozenTextureSettings> FrozenTextures = new List<FrozenTextureSettings>();

        /// <summary>
        /// Gets frozen settings for a texture by its GUID.
        /// </summary>
        public FrozenTextureSettings GetFrozenSettings(string guid)
        {
            return FrozenTextures.Find(f => f.TextureGuid == guid);
        }

        /// <summary>
        /// Checks if a texture is frozen by its GUID.
        /// </summary>
        public bool IsFrozen(string guid)
        {
            return FrozenTextures.Exists(f => f.TextureGuid == guid);
        }

        /// <summary>
        /// Adds or updates frozen settings for a texture.
        /// Validates that divisor is a valid power of 2 (1, 2, 4, 8, or 16).
        /// </summary>
        public void SetFrozenSettings(string guid, FrozenTextureSettings settings)
        {
            // Validate divisor is a valid power of 2
            if (!IsValidDivisor(settings.Divisor))
            {
                Debug.LogWarning(
                    $"[TextureCompressor] Invalid divisor {settings.Divisor} for frozen texture. Using closest valid value."
                );
                settings.Divisor = GetClosestValidDivisor(settings.Divisor);
            }

            var existingIndex = FrozenTextures.FindIndex(f => f.TextureGuid == guid);
            if (existingIndex >= 0)
                FrozenTextures[existingIndex] = settings;
            else
                FrozenTextures.Add(settings);
        }

        /// <summary>
        /// Checks if a divisor value is valid (1, 2, 4, 8, or 16).
        /// </summary>
        public static bool IsValidDivisor(int divisor)
        {
            return divisor == 1 || divisor == 2 || divisor == 4 || divisor == 8 || divisor == 16;
        }

        /// <summary>
        /// Gets the closest valid divisor for an invalid value.
        /// </summary>
        public static int GetClosestValidDivisor(int divisor)
        {
            int[] validDivisors = { 1, 2, 4, 8, 16 };
            int closest = 1;
            int minDiff = int.MaxValue;

            foreach (int valid in validDivisors)
            {
                int diff = System.Math.Abs(divisor - valid);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = valid;
                }
            }

            return closest;
        }

        /// <summary>
        /// Removes frozen settings for a texture by its GUID.
        /// </summary>
        public void UnfreezeTexture(string guid)
        {
            FrozenTextures.RemoveAll(f => f.TextureGuid == guid);
        }

        /// <summary>
        /// Called by Unity when the component is first added or reset.
        /// Applies the default preset settings.
        /// </summary>
        private void Reset()
        {
            ApplyPreset(Preset);
        }

        /// <summary>
        /// Applies preset settings to this component.
        /// </summary>
        public void ApplyPreset(CompressorPreset preset)
        {
            Preset = preset;

            switch (preset)
            {
                case CompressorPreset.HighQuality:
                    ApplyHighQualityPreset();
                    break;
                case CompressorPreset.Quality:
                    ApplyQualityPreset();
                    break;
                case CompressorPreset.Balanced:
                    ApplyBalancedPreset();
                    break;
                case CompressorPreset.Aggressive:
                    ApplyAggressivePreset();
                    break;
                case CompressorPreset.Maximum:
                    ApplyMaximumPreset();
                    break;
                case CompressorPreset.Custom:
                    ApplyCustomPreset();
                    break;
            }
        }

        private void ApplyHighQualityPreset()
        {
            Strategy = AnalysisStrategyType.Combined;
            FastWeight = 0.1f;
            HighAccuracyWeight = 0.5f;
            PerceptualWeight = 0.4f;
            HighComplexityThreshold = 0.3f;
            LowComplexityThreshold = 0.1f;
            MinDivisor = 1;
            MaxDivisor = 2;
            MaxResolution = 2048;
            MinResolution = 256;
            ForcePowerOfTwo = true;
            MinSourceSize = 1024;
            SkipIfSmallerThan = 512;
            TargetPlatform = CompressionPlatform.Auto;
            UseHighQualityFormatForHighComplexity = true;
        }

        private void ApplyQualityPreset()
        {
            Strategy = AnalysisStrategyType.Combined;
            FastWeight = 0.2f;
            HighAccuracyWeight = 0.5f;
            PerceptualWeight = 0.3f;
            HighComplexityThreshold = 0.5f;
            LowComplexityThreshold = 0.15f;
            MinDivisor = 1;
            MaxDivisor = 4;
            MaxResolution = 2048;
            MinResolution = 128;
            ForcePowerOfTwo = true;
            MinSourceSize = 512;
            SkipIfSmallerThan = 256;
            TargetPlatform = CompressionPlatform.Auto;
            UseHighQualityFormatForHighComplexity = true;
        }

        private void ApplyBalancedPreset()
        {
            Strategy = AnalysisStrategyType.Combined;
            FastWeight = 0.3f;
            HighAccuracyWeight = 0.5f;
            PerceptualWeight = 0.2f;
            HighComplexityThreshold = 0.7f;
            LowComplexityThreshold = 0.2f;
            MinDivisor = 1;
            MaxDivisor = 8;
            MaxResolution = 2048;
            MinResolution = 64;
            ForcePowerOfTwo = true;
            MinSourceSize = 256;
            SkipIfSmallerThan = 128;
            TargetPlatform = CompressionPlatform.Auto;
            UseHighQualityFormatForHighComplexity = true;
        }

        private void ApplyAggressivePreset()
        {
            Strategy = AnalysisStrategyType.Combined;
            FastWeight = 0.4f;
            HighAccuracyWeight = 0.4f;
            PerceptualWeight = 0.2f;
            HighComplexityThreshold = 0.70f;
            LowComplexityThreshold = 0.20f;
            MinDivisor = 2;
            MaxDivisor = 8;
            MaxResolution = 2048;
            MinResolution = 32;
            ForcePowerOfTwo = true;
            MinSourceSize = 128;
            SkipIfSmallerThan = 64;
            TargetPlatform = CompressionPlatform.Auto;
            UseHighQualityFormatForHighComplexity = false;
        }

        private void ApplyMaximumPreset()
        {
            Strategy = AnalysisStrategyType.Combined;
            FastWeight = 0.5f;
            HighAccuracyWeight = 0.3f;
            PerceptualWeight = 0.2f;
            HighComplexityThreshold = 0.70f;
            LowComplexityThreshold = 0.15f;
            MinDivisor = 4;
            MaxDivisor = 16;
            MaxResolution = 2048;
            MinResolution = 32;
            ForcePowerOfTwo = true;
            MinSourceSize = 64;
            SkipIfSmallerThan = 32;
            TargetPlatform = CompressionPlatform.Auto;
            UseHighQualityFormatForHighComplexity = false;
        }

        private void ApplyCustomPreset()
        {
            if (CustomPresetAsset != null)
            {
                CustomPresetAsset.ApplyTo(this);
            }
        }
    }

    internal enum CompressorPreset
    {
        HighQuality,
        Quality,
        Balanced,
        Aggressive,
        Maximum,
        Custom,
    }
}
