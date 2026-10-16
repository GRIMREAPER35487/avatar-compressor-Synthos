using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Removes saved float, color and int properties that a material's shader does not declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A material keeps every property it ever had, so one switched from Poiyomi or lilToon to a
    /// VRChat Mobile shader still carries thousands of the old shader's values (about 120 KB per
    /// material). Unity drops undeclared texture slots when building the asset bundle but uploads
    /// all of these, and they count toward the uncompressed size limit.
    /// </para>
    /// <para>
    /// Only runs on the build's material copies. A shader cannot read a property it doesn't
    /// declare, so removing them changes nothing on the avatar, including properties animations
    /// set (an animated property the shader lacks has no effect either way).
    /// </para>
    /// </remarks>
    internal static class UnusedMaterialPropertyStripper
    {
        /// <summary>
        /// Kept even when the shader doesn't declare them: VRChat's shader fallback reads these
        /// when an avatar's shaders are hidden by safety settings.
        /// </summary>
        private static readonly string[] FallbackProperties =
        {
            "_Color",
            "_Cutoff",
            "_EmissionColor",
        };

        /// <summary>
        /// Strips the material's undeclared properties.
        /// </summary>
        /// <returns>The number of properties removed.</returns>
        public static int Strip(Material material)
        {
            if (material == null || material.shader == null)
                return 0;

            var keep = DeclaredProperties(material.shader);

            using var serialized = new SerializedObject(material);
            int removed =
                Prune(serialized.FindProperty("m_SavedProperties.m_Floats"), keep, PropertyKind.Float)
                + Prune(serialized.FindProperty("m_SavedProperties.m_Colors"), keep, PropertyKind.Color)
                + Prune(serialized.FindProperty("m_SavedProperties.m_Ints"), keep, PropertyKind.Int);
            if (removed > 0)
                serialized.ApplyModifiedPropertiesWithoutUndo();
            return removed;
        }

        /// <summary>
        /// How many bytes <see cref="Strip"/> would remove from the material in the uploaded
        /// bundle, without changing it.
        /// </summary>
        public static long RemovableBytes(Material material)
        {
            if (material == null || material.shader == null)
                return 0;
            var keep = DeclaredProperties(material.shader);
            using var serialized = new SerializedObject(material);
            return Removable(serialized.FindProperty("m_SavedProperties.m_Floats"), keep, 4)
                + Removable(serialized.FindProperty("m_SavedProperties.m_Colors"), keep, 16)
                + Removable(serialized.FindProperty("m_SavedProperties.m_Ints"), keep, 4);
        }

        private static long Removable(SerializedProperty array, HashSet<string> keep, int valueBytes)
        {
            if (array == null || !array.isArray || array.arraySize == 0)
                return 0;
            long bytes = 0;
            var element = array.GetArrayElementAtIndex(0);
            for (int index = 0; index < array.arraySize; index++)
            {
                string name = element.FindPropertyRelative("first").stringValue;
                // length-prefixed name padded to 4 bytes, then the value
                if (!keep.Contains(name))
                    bytes += 4 + ((System.Text.Encoding.UTF8.GetByteCount(name) + 3) & ~3) + valueBytes;
                if (index + 1 < array.arraySize)
                    element.Next(false);
            }
            return bytes;
        }

        private static HashSet<string> DeclaredProperties(Shader shader)
        {
            var keep = new HashSet<string>(FallbackProperties);
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                keep.Add(shader.GetPropertyName(i));
            return keep;
        }

        private enum PropertyKind
        {
            Float,
            Color,
            Int,
        }

        /// <summary>
        /// Rebuilds a saved-property array with only the kept entries, in their original order.
        /// Rewriting once is much faster than deleting thousands of elements one at a time.
        /// </summary>
        private static int Prune(SerializedProperty array, HashSet<string> keep, PropertyKind kind)
        {
            if (array == null || !array.isArray || array.arraySize == 0)
                return 0;

            int size = array.arraySize;
            var kept = new List<(string name, float f, Color c, int i)>(size);
            var element = array.GetArrayElementAtIndex(0);
            for (int index = 0; index < size; index++)
            {
                string name = element.FindPropertyRelative("first").stringValue;
                if (keep.Contains(name))
                {
                    var value = element.FindPropertyRelative("second");
                    kept.Add(
                        kind switch
                        {
                            PropertyKind.Float => (name, value.floatValue, default, 0),
                            PropertyKind.Color => (name, 0f, value.colorValue, 0),
                            _ => (name, 0f, default, value.intValue),
                        }
                    );
                }
                if (index + 1 < size)
                    element.Next(false);
            }

            int removed = size - kept.Count;
            if (removed == 0)
                return 0;

            array.arraySize = kept.Count;
            for (int index = 0; index < kept.Count; index++)
            {
                var entry = array.GetArrayElementAtIndex(index);
                entry.FindPropertyRelative("first").stringValue = kept[index].name;
                var value = entry.FindPropertyRelative("second");
                switch (kind)
                {
                    case PropertyKind.Float:
                        value.floatValue = kept[index].f;
                        break;
                    case PropertyKind.Color:
                        value.colorValue = kept[index].c;
                        break;
                    default:
                        value.intValue = kept[index].i;
                        break;
                }
            }
            return removed;
        }
    }
}
