using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Frozen settings that follow a material slot: a frozen texture with "Apply to swaps in this
    /// slot" passes its settings to any other texture that ends up in the same mesh, material slot
    /// and texture property, such as an outfit's swapped-in material.
    /// </summary>
    internal static class FrozenSlotResolver
    {
        private const string MaterialsBindingPrefix = "m_Materials.Array.data[";

        /// <summary>
        /// Every material that can sit in a material slot of the avatar: the renderers' materials
        /// and the materials animations put into a slot (toggles that swap materials).
        /// </summary>
        public static IEnumerable<(Renderer Renderer, int Index, Material Material)> SlotMaterials(
            GameObject root
        )
        {
            if (root == null)
                yield break;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (ComponentUtils.IsEditorOnly(renderer.gameObject))
                    continue;
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null)
                        yield return (renderer, i, materials[i]);
                }
            }

            foreach (var clip in AvatarAnimators.GetClips(root))
            {
                if (clip == null)
                    continue;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (
                        !typeof(Renderer).IsAssignableFrom(binding.type)
                        || !TryParseMaterialIndex(binding.propertyName, out int index)
                    )
                        continue;

                    var target =
                        binding.path.Length == 0 ? root.transform : root.transform.Find(binding.path);
                    var renderer = target != null ? target.GetComponent(binding.type) as Renderer : null;
                    if (renderer == null)
                        continue;

                    var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keyframes == null)
                        continue;
                    foreach (var keyframe in keyframes)
                    {
                        if (keyframe.value is Material material && material != null)
                            yield return (renderer, index, material);
                    }
                }
            }
        }

        /// <summary>
        /// The slots where the texture with <paramref name="textureGuid"/> is used on the avatar.
        /// </summary>
        public static List<FrozenTextureSlot> FindSlots(string textureGuid, GameObject root)
        {
            var slots = new List<FrozenTextureSlot>();
            if (string.IsNullOrEmpty(textureGuid))
                return slots;

            var seen = new HashSet<(Mesh, int, string)>();
            foreach (var (renderer, index, material) in SlotMaterials(root))
            {
                var mesh = GetMesh(renderer);
                if (mesh == null || material.shader == null)
                    continue;

                int count = ShaderUtil.GetPropertyCount(material.shader);
                for (int p = 0; p < count; p++)
                {
                    if (ShaderUtil.GetPropertyType(material.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv)
                        continue;
                    string property = ShaderUtil.GetPropertyName(material.shader, p);
                    if (GetGuid(material.GetTexture(property) as Texture2D) != textureGuid)
                        continue;
                    if (!seen.Add((mesh, index, property)))
                        continue;

                    var slot = new FrozenTextureSlot
                    {
                        MeshName = mesh.name,
                        MaterialIndex = index,
                        PropertyName = property,
                    };
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long fileId))
                    {
                        slot.MeshGuid = guid;
                        slot.MeshFileId = fileId;
                    }
                    slots.Add(slot);
                }
            }
            return slots;
        }

        /// <summary>
        /// The frozen settings with, for each entry that applies to slot swaps, a copy of its
        /// settings for every other texture found in one of its slots on the avatar. Textures
        /// with their own frozen settings keep them. <paramref name="followedBy"/> receives, for
        /// each added texture GUID, the GUID of the frozen texture it follows.
        /// </summary>
        public static List<FrozenTextureSettings> Expand(
            IEnumerable<FrozenTextureSettings> frozenTextures,
            GameObject root,
            Dictionary<string, string> followedBy = null
        )
        {
            var result = new List<FrozenTextureSettings>();
            var sources = new List<FrozenTextureSettings>();
            var own = new HashSet<string>();
            if (frozenTextures != null)
            {
                foreach (var frozen in frozenTextures)
                {
                    if (frozen == null)
                        continue;
                    result.Add(frozen);
                    if (!string.IsNullOrEmpty(frozen.TextureGuid))
                        own.Add(frozen.TextureGuid);
                    if (frozen.ApplyToSlotSwaps && frozen.Slots != null && frozen.Slots.Count > 0)
                        sources.Add(frozen);
                }
            }
            if (sources.Count == 0 || root == null)
                return result;

            var added = new Dictionary<string, FrozenTextureSettings>();
            foreach (var (renderer, index, material) in SlotMaterials(root))
            {
                var mesh = GetMesh(renderer);
                if (mesh == null)
                    continue;

                foreach (var source in sources)
                {
                    foreach (var slot in source.Slots)
                    {
                        if (
                            slot == null
                            || slot.MaterialIndex != index
                            || !material.HasProperty(slot.PropertyName)
                            || !Matches(slot, mesh)
                        )
                            continue;

                        string guid = GetGuid(material.GetTexture(slot.PropertyName) as Texture2D);
                        if (guid == null || own.Contains(guid) || added.ContainsKey(guid))
                            continue;

                        added[guid] = new FrozenTextureSettings(
                            guid,
                            source.Divisor,
                            source.Format,
                            source.Skip
                        );
                        if (followedBy != null)
                            followedBy[guid] = source.TextureGuid;
                    }
                }
            }

            result.AddRange(added.Values);
            return result;
        }

        // Where build steps save their copies: VRCFury (e.g. a mesh with blendshapes baked or
        // removed) and this compressor. Such a copy keeps its original's name.
        private static readonly string[] BuildCopyFolders = { "Packages/com.vrcfury.temp/", BuildAssetStore.RootFolder };

        /// <summary>
        /// True if <paramref name="mesh"/> is the slot's mesh: the same project asset, or for a
        /// copy made during the build (no asset, or one saved by a build step), the same name.
        /// </summary>
        internal static bool Matches(FrozenTextureSlot slot, Mesh mesh)
        {
            var original = TextureCollector.ResolveOriginalObject(mesh) as Mesh ?? mesh;
            if (!IsBuildCopy(original) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out string guid, out long fileId))
            {
                return !string.IsNullOrEmpty(slot.MeshGuid) && guid == slot.MeshGuid && fileId == slot.MeshFileId;
            }
            return !string.IsNullOrEmpty(slot.MeshName) && BaseName(original.name) == BaseName(slot.MeshName);
        }

        private static bool IsBuildCopy(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path))
                return true;
            foreach (string folder in BuildCopyFolders)
            {
                if (path.StartsWith(folder))
                    return true;
            }
            return false;
        }

        private static string BaseName(string name)
        {
            name = name.Trim();
            while (name.EndsWith("(Clone)"))
                name = name.Substring(0, name.Length - "(Clone)".Length).TrimEnd();
            return name;
        }

        private static bool TryParseMaterialIndex(string propertyName, out int index)
        {
            index = -1;
            if (!propertyName.StartsWith(MaterialsBindingPrefix) || !propertyName.EndsWith("]"))
                return false;
            string number = propertyName.Substring(
                MaterialsBindingPrefix.Length,
                propertyName.Length - MaterialsBindingPrefix.Length - 1
            );
            return int.TryParse(number, out index) && index >= 0;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer skinned:
                    return skinned.sharedMesh;
                case MeshRenderer _:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
                default:
                    return null;
            }
        }

        private static string GetGuid(Texture2D texture)
        {
            if (texture == null)
                return null;
            string path = AssetDatabase.GetAssetPath(TextureCollector.ResolveOriginalObject(texture));
            if (string.IsNullOrEmpty(path))
                return null;
            string guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }
    }
}
