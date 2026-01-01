using UnityEngine;

namespace dev.limitex.avatar.compressor.common
{
    /// <summary>
    /// Common mathematical utility methods.
    /// </summary>
    public static class MathUtils
    {
        /// <summary>
        /// Normalizes a value using expected percentile range.
        /// Values below lowPercentile return 0, above highPercentile return 1.
        /// </summary>
        public static float NormalizeWithPercentile(float value, float lowPercentile, float highPercentile)
        {
            if (value <= lowPercentile) return 0f;
            if (value >= highPercentile) return 1f;
            return (value - lowPercentile) / (highPercentile - lowPercentile);
        }

        /// <summary>
        /// Clamps and normalizes a value to 0-1 range.
        /// </summary>
        public static float Normalize01(float value, float min, float max)
        {
            if (max <= min) return 0f;
            return Mathf.Clamp01((value - min) / (max - min));
        }

        /// <summary>
        /// Calculates the next power of two greater than or equal to the value.
        /// </summary>
        public static int NextPowerOfTwo(int value)
        {
            return Mathf.NextPowerOfTwo(value);
        }

        /// <summary>
        /// Calculates the closest power of two to the value.
        /// </summary>
        public static int ClosestPowerOfTwo(int value)
        {
            return Mathf.ClosestPowerOfTwo(value);
        }
    }
}
