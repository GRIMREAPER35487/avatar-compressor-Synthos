using System.Collections.Generic;
using Synthos.TextureAnalysis;
using UnityEngine.Serialization;

namespace dev.limitex.avatar.compressor
{
    /// <summary>
    /// Settings for a frozen texture with user-specified compression overrides.
    /// </summary>
    [System.Serializable]
    internal sealed class FrozenTextureSettings
    {
        /// <summary>
        /// GUID of the texture asset (stable identifier that survives renames/moves).
        /// </summary>
        // TODO: Remove [FormerlySerializedAs] after users have migrated from TexturePath to TextureGuid
        [FormerlySerializedAs("TexturePath")]
        public string TextureGuid;

        /// <summary>
        /// Resolution divisor (1 = no reduction, 2 = half, 4 = quarter, etc.)
        /// </summary>
        public int Divisor = 1;

        /// <summary>
        /// Compression format override.
        /// </summary>
        public FrozenTextureFormat Format = FrozenTextureFormat.Auto;

        /// <summary>
        /// If true, completely skip compression for this texture.
        /// </summary>
        public bool Skip = false;

        public static Dictionary<string, FrozenTextureSettings> BuildLookup(
            IEnumerable<FrozenTextureSettings> frozenTextures
        )
        {
            var lookup = new Dictionary<string, FrozenTextureSettings>();
            foreach (var frozen in frozenTextures)
            {
                if (!string.IsNullOrEmpty(frozen.TextureGuid))
                    lookup[frozen.TextureGuid] = frozen;
            }
            return lookup;
        }

        public FrozenTextureSettings() { }

        public FrozenTextureSettings(string textureGuid)
        {
            TextureGuid = textureGuid;
        }

        public FrozenTextureSettings(
            string textureGuid,
            int divisor,
            FrozenTextureFormat format,
            bool skip
        )
        {
            TextureGuid = textureGuid;
            Divisor = divisor;
            Format = format;
            Skip = skip;
        }
    }
}
