using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace dev.limitex.avatar.compressor.editor
{
    /// <summary>
    /// Tracks build-time replacement objects (cloned materials, resized textures, baked textures)
    /// back to the original asset they were derived from.
    /// </summary>
    /// <remarks>
    /// Replacements created during a build have no asset path. Services that need the original
    /// asset (e.g. to read its import settings or GUID) resolve through this registry.
    /// Entries only live for the duration of a <see cref="Scope"/>; outside a scope every
    /// object resolves to itself.
    /// </remarks>
    internal static class ReplacedObjectRegistry
    {
        private static readonly Dictionary<Object, Object> s_originals =
            new Dictionary<Object, Object>();

        /// <summary>
        /// Records that <paramref name="replacement"/> stands in for <paramref name="original"/>.
        /// Chains collapse, so a replacement of a replacement resolves to the first original.
        /// </summary>
        public static void Register(Object original, Object replacement)
        {
            if (original == null || replacement == null || original == replacement)
                return;

            s_originals[replacement] = Resolve(original);
        }

        /// <summary>
        /// Returns the original object for a registered replacement, or the object itself.
        /// </summary>
        public static Object Resolve(Object obj)
        {
            if (obj == null)
                return obj;

            return s_originals.TryGetValue(obj, out var original) && original != null
                ? original
                : obj;
        }

        /// <summary>
        /// Starts a fresh registry scope. Disposing the scope discards all entries.
        /// </summary>
        public static Scope BeginScope()
        {
            return new Scope();
        }

        internal sealed class Scope : IDisposable
        {
            internal Scope()
            {
                s_originals.Clear();
            }

            public void Dispose()
            {
                s_originals.Clear();
            }
        }
    }
}
