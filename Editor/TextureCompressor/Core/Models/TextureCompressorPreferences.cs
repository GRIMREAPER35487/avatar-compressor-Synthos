using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Backend selection for texture analysis.
    /// </summary>
    internal enum AnalysisBackendPreference
    {
        /// <summary>Use GPU if available, otherwise fall back to CPU.</summary>
        Auto,

        /// <summary>Always use CPU backend.</summary>
        [InspectorName("Software")]
        CPU,
    }

    /// <summary>
    /// Backend selection for texture resize (Area Averaging).
    /// </summary>
    internal enum ResizeBackendPreference
    {
        /// <summary>Use GPU if available, otherwise fall back to CPU.</summary>
        Auto,

        /// <summary>Always use CPU backend.</summary>
        [InspectorName("Software")]
        CPU,
    }

    /// <summary>
    /// Texture Compressor editor preferences, shown as a section of the
    /// shared Avatar Compressor preferences window.
    /// </summary>
    internal static class TextureCompressorPreferences
    {
        private const string AnalysisBackendKey =
            AvatarCompressorPreferences.PrefsPrefix + "analysisBackend";
        private const string ResizeBackendKey =
            AvatarCompressorPreferences.PrefsPrefix + "resizeBackend";
        private const string UseSourceFilesKey =
            AvatarCompressorPreferences.PrefsPrefix + "useSourceFiles";

        /// <summary>
        /// Which backend to use for texture analysis.
        /// Auto prefers GPU when available, CPU forces CPU-only.
        /// </summary>
        public static AnalysisBackendPreference AnalysisBackend
        {
            get =>
                (AnalysisBackendPreference)
                    EditorPrefs.GetInt(AnalysisBackendKey, (int)AnalysisBackendPreference.Auto);
            set => EditorPrefs.SetInt(AnalysisBackendKey, (int)value);
        }

        /// <summary>
        /// Which backend to use for Area Averaging texture resize.
        /// Auto prefers GPU when available, CPU forces CPU-only.
        /// </summary>
        public static ResizeBackendPreference ResizeBackend
        {
            get =>
                (ResizeBackendPreference)
                    EditorPrefs.GetInt(ResizeBackendKey, (int)ResizeBackendPreference.Auto);
            set => EditorPrefs.SetInt(ResizeBackendKey, (int)value);
        }

        /// <summary>
        /// Whether to compress from full-quality copies of the source image files
        /// (cached in Library/AvatarCompressorCache) instead of Unity's compressed imports.
        /// </summary>
        public static bool UseSourceFiles
        {
            get => EditorPrefs.GetBool(UseSourceFilesKey, true);
            set => EditorPrefs.SetBool(UseSourceFilesKey, value);
        }
    }
}
