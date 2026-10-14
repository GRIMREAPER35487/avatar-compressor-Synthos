using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture.ui
{
    /// <summary>
    /// Poiyomi (Thry) materials that are not locked yet. On upload Poiyomi locks them before the
    /// compressor runs and drops the textures of features that are turned off, so the preview of
    /// an unlocked material lists textures the upload will not contain. Poiyomi is optional: it
    /// is reached by reflection and nothing happens without it.
    /// </summary>
    internal static class PoiyomiLockState
    {
        private const string LockProperty = "_ShaderOptimizerEnabled";
        private const string OptimizerType = "Thry.ThryEditor.ShaderOptimizer";

        /// <summary>
        /// Unlocked Poiyomi materials on the avatar, including those animations swap in.
        /// </summary>
        public static List<Material> FindUnlocked(GameObject root)
        {
            var result = new List<Material>();
            var seen = new HashSet<Material>();
            foreach (var (_, _, material) in FrozenSlotResolver.SlotMaterials(root))
            {
                if (
                    seen.Add(material)
                    && material.HasProperty(LockProperty)
                    && material.GetFloat(LockProperty) < 0.5f
                )
                {
                    result.Add(material);
                }
            }
            return result;
        }

        /// <summary>True if Poiyomi's locking can be called.</summary>
        public static bool CanLock => FindLockMethod() != null;

        /// <summary>Locks the materials with Poiyomi, as its "Lock All" does.</summary>
        public static bool Lock(IEnumerable<Material> materials)
        {
            var method = FindLockMethod();
            if (method == null)
                return false;
            var result = method.Invoke(
                null,
                new object[] { materials.Where(m => m != null).ToList(), 1, true, false, true, null }
            );
            return result is bool locked && locked;
        }

        private static MethodInfo FindLockMethod()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(OptimizerType, false);
                if (type == null)
                    continue;
                return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "SetLockedForAllMaterials" && m.GetParameters().Length == 6
                    );
            }
            return null;
        }
    }
}
