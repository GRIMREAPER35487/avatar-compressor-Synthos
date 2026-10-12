using UnityEngine;

namespace Synthos.TextureAnalysis
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
}
