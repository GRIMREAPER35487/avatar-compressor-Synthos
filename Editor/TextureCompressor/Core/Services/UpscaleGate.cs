using Synthos.TextureAnalysis;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Decides whether a texture may come out larger than the size it was imported at, which
    /// only happens when the import Max Size is not respected and the source file is larger.
    /// </summary>
    /// <remarks>
    /// The creator's import size is the default. Going above it has to be visible: the texture
    /// needs real detail (a flat color looks the same at any size), and the surface it covers
    /// on the avatar must be below the target density at the imported size (an earring is
    /// already sharper than anyone can see at 512). When the density could not be measured,
    /// detail alone decides.
    /// </remarks>
    internal static class UpscaleGate
    {
        internal enum Decision
        {
            /// <summary>The target is not larger than the imported size.</summary>
            NotLarger,

            /// <summary>Larger than imported: detailed, and the surface can show it.</summary>
            Allowed,

            /// <summary>Kept at the imported size: too little detail to gain from more pixels.</summary>
            LowDetail,

            /// <summary>Kept at the imported size: already at the target density.</summary>
            AlreadySharp,
        }

        /// <summary>
        /// Returns <paramref name="target"/>, or the imported size when growing past it is not
        /// worth it.
        /// </summary>
        public static Vector2Int Apply(
            Vector2Int target,
            Vector2Int imported,
            float complexity,
            float lowComplexityThreshold,
            TexelDensityInfo? density,
            out Decision decision
        )
        {
            if (target.x <= imported.x && target.y <= imported.y)
            {
                decision = Decision.NotLarger;
                return target;
            }

            if (complexity < lowComplexityThreshold)
                decision = Decision.LowDetail;
            else if (
                density.HasValue
                && density.Value.DensityAt(imported) >= density.Value.TargetDensity
            )
                decision = Decision.AlreadySharp;
            else
            {
                decision = Decision.Allowed;
                return target;
            }

            return new Vector2Int(
                Mathf.Min(target.x, TextureProcessor.EnsureMultipleOf4(imported.x)),
                Mathf.Min(target.y, TextureProcessor.EnsureMultipleOf4(imported.y))
            );
        }

        /// <summary>Short log text explaining a decision, or null when nothing grew.</summary>
        public static string Describe(
            Decision decision,
            Vector2Int imported,
            TexelDensityInfo? density
        )
        {
            string importedText = $"{imported.x}x{imported.y}";
            switch (decision)
            {
                case Decision.Allowed:
                    return density.HasValue
                        ? $"larger than the imported {importedText}: detailed, and "
                            + $"{importedText} gives only {density.Value.DensityAt(imported):F0} "
                            + $"px/cm (needs {density.Value.TargetDensity:F0})"
                        : $"larger than the imported {importedText}: detailed (on-avatar size "
                            + "not measured)";
                case Decision.LowDetail:
                    return $"kept at the imported {importedText}: too little detail to gain "
                        + "from the larger source";
                case Decision.AlreadySharp:
                    return $"kept at the imported {importedText}: already "
                        + $"{density.Value.DensityAt(imported):F0} px/cm on the avatar (needs "
                        + $"{density.Value.TargetDensity:F0})";
                default:
                    return null;
            }
        }
    }
}
