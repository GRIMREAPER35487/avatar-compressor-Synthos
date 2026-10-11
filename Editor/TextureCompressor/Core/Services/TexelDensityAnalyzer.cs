using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// How many texture pixels a texture needs, given the size of the avatar surface it covers
    /// and how closely that surface can be looked at.
    /// </summary>
    internal readonly struct TexelDensityInfo
    {
        /// <summary>
        /// Pixel count (width x height) at which the demanding parts of the surface reach
        /// <see cref="TargetDensity"/>.
        /// </summary>
        public readonly double RequiredPixels;

        /// <summary>Pixels per cm wanted on the surface that sets the requirement.</summary>
        public readonly float TargetDensity;

        /// <summary>True if the requirement comes from the head (face, eyes, hair).</summary>
        public readonly bool FromHead;

        public TexelDensityInfo(double requiredPixels, float targetDensity, bool fromHead)
        {
            RequiredPixels = requiredPixels;
            TargetDensity = targetDensity;
            FromHead = fromHead;
        }

        /// <summary>Pixels per cm the demanding surface gets at the given texture size.</summary>
        public float DensityAt(Vector2Int size)
        {
            return (float)(TargetDensity * Math.Sqrt((double)size.x * size.y / RequiredPixels));
        }

        /// <summary>
        /// Largest power-of-two divisor of <paramref name="baseSize"/> that still meets the
        /// target density (1 when the texture is not larger than needed).
        /// </summary>
        public int GetDivisor(Vector2Int baseSize)
        {
            double scale = Math.Sqrt(RequiredPixels / ((double)baseSize.x * baseSize.y));
            int divisor = 1;
            while (divisor < TexelDensityAnalyzer.MaxDivisor && scale * divisor * 2 <= 1.0)
                divisor *= 2;
            return divisor;
        }
    }

    /// <summary>
    /// Measures how densely each texture is laid out on the avatar's meshes, so textures far
    /// sharper than anyone could see (a 2048 texture on an earring) can be made smaller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every triangle using a texture is measured on its own: its UV area (times the material
    /// tiling) against its surface area in the avatar's current pose. That gives the pixel count
    /// the texture needs for that triangle to reach the target density, which is set from how
    /// closely the surface can be viewed: the head (face, eyes, hair, anything moved by the Head
    /// bone) is sized for a face in a mirror, the rest for close-up viewing of the body.
    /// </para>
    /// <para>
    /// The texture's requirement is a high area-weighted percentile per region, so a few
    /// stretched or hidden triangles do not decide it, while a small face on a shared atlas
    /// still does. Measuring per triangle also makes mirrored and overlapping UVs harmless.
    /// </para>
    /// <para>
    /// Only textures whose every use is a known, UV0-mapped slot on a mesh in the avatar are
    /// measured. Textures sampled some other way (matcaps, ramps, gradients, flipbooks,
    /// panospheres, decals, noise) or used by materials that only appear through animations
    /// get no density limit.
    /// </para>
    /// </remarks>
    internal static class TexelDensityAnalyzer
    {
        /// <summary>
        /// Display resolution assumed for the viewer, in pixels per degree of view. Current
        /// headsets are roughly 20-25; desktop screens are lower.
        /// </summary>
        public const float PixelsPerDegree = 25f;

        /// <summary>Default viewing distance for the head, in cm (a face in a mirror).</summary>
        public const float DefaultFaceViewDistance = 15f;

        /// <summary>Default viewing distance for the rest of the avatar, in cm.</summary>
        public const float DefaultBodyViewDistance = 30f;

        /// <summary>Largest divisor the density limit applies on its own.</summary>
        public const int MaxDivisor = 16;

        /// <summary>
        /// Area-weighted percentile of per-triangle requirements used per region. Triangles
        /// above it (at most 3% of the region's surface) are treated as outliers.
        /// </summary>
        internal const float Percentile = 0.97f;

        /// <summary>UV areas below this (a fraction of the texture) are collapsed UVs.</summary>
        private const double MinUvArea = 1e-10;

        /// <summary>Triangles smaller than this (cm²) are degenerate.</summary>
        private const double MinSurfaceArea = 1e-8;

        /// <summary>
        /// Property name parts of textures not mapped by UV0 across the surface. Checked
        /// case-insensitively.
        /// </summary>
        private static readonly string[] NonUvPropertyParts =
        {
            "matcap",
            "ramp",
            "grad",
            "lut",
            "flipbook",
            "panosphere",
            "cube",
            "decal",
            "noise",
            "glitter",
            "audiolink",
        };

        /// <summary>
        /// Pixels per cm needed for one pixel of texture per display pixel at the given viewing
        /// distance.
        /// </summary>
        public static float TargetDensity(float viewDistanceCm)
        {
            float distance = Mathf.Max(1f, viewDistanceCm);
            return PixelsPerDegree * Mathf.Rad2Deg * Mathf.Atan(1f / distance);
        }

        /// <summary>
        /// Measures the textures used by <paramref name="renderers"/>. Textures with no
        /// measurable use are left out of the result.
        /// </summary>
        public static Dictionary<Texture2D, TexelDensityInfo> Analyze(
            IEnumerable<Renderer> renderers,
            Dictionary<Texture2D, TextureInfo> textures,
            float faceViewDistanceCm,
            float bodyViewDistanceCm
        )
        {
            var result = new Dictionary<Texture2D, TexelDensityInfo>();
            if (renderers == null || textures == null || textures.Count == 0)
                return result;

            var slots = CollectSlots(renderers);
            var meshes = new Dictionary<Renderer, MeshData>();
            float faceTarget = TargetDensity(faceViewDistanceCm);
            float bodyTarget = TargetDensity(bodyViewDistanceCm);

            try
            {
                foreach (var kvp in textures)
                {
                    var info = Measure(kvp.Value, slots, meshes, faceTarget, bodyTarget);
                    if (info.HasValue)
                        result[kvp.Key] = info.Value;
                }
            }
            finally
            {
                foreach (var data in meshes.Values)
                    data?.Dispose();
            }

            return result;
        }

        /// <summary>
        /// Returns <paramref name="analysis"/> with its divisor raised to the density limit's,
        /// when that is larger.
        /// </summary>
        public static TextureAnalysisResult ApplyLimit(
            TextureAnalysisResult analysis,
            Vector2Int baseSize,
            TexelDensityInfo density,
            TextureProcessor processor
        )
        {
            int divisor = density.GetDivisor(baseSize);
            if (divisor <= analysis.RecommendedDivisor)
                return analysis;

            return new TextureAnalysisResult(
                analysis.NormalizedComplexity,
                divisor,
                processor.CalculateNewDimensions(baseSize.x, baseSize.y, divisor)
            );
        }

        /// <summary>
        /// Returns <paramref name="analysis"/> with its divisor lowered (down to
        /// <paramref name="minDivisor"/>) while the result would be below the target density.
        /// Only for textures with real detail: on a near-flat texture, blur does not show.
        /// </summary>
        /// <remarks>
        /// Smooth surfaces like skin score low on detail, yet they cover most of the avatar and
        /// are looked at up close. When such a surface is already less sharp than it needs to
        /// be, shrinking it further only makes visible blur worse.
        /// </remarks>
        public static TextureAnalysisResult ApplyFloor(
            TextureAnalysisResult analysis,
            Vector2Int baseSize,
            TexelDensityInfo density,
            int minDivisor,
            float lowComplexityThreshold,
            TextureProcessor processor
        )
        {
            if (analysis.NormalizedComplexity < lowComplexityThreshold)
                return analysis;

            int floor = Math.Max(1, minDivisor);
            int divisor = analysis.RecommendedDivisor;
            while (
                divisor > floor
                && density.DensityAt(new Vector2Int(baseSize.x / divisor, baseSize.y / divisor))
                    < density.TargetDensity
            )
            {
                divisor = Math.Max(floor, divisor / 2);
            }

            if (divisor == analysis.RecommendedDivisor)
                return analysis;

            return new TextureAnalysisResult(
                analysis.NormalizedComplexity,
                divisor,
                processor.CalculateNewDimensions(baseSize.x, baseSize.y, divisor)
            );
        }

        /// <summary>
        /// Returns true if a texture assigned to <paramref name="propertyName"/> on
        /// <paramref name="material"/> is sampled with UV0 across the surface.
        /// </summary>
        internal static bool IsUvMapped(Material material, string propertyName)
        {
            string lower = propertyName.ToLowerInvariant();
            foreach (var part in NonUvPropertyParts)
            {
                if (lower.Contains(part))
                    return false;
            }

            // lilToon (_UVMode) and Poiyomi (UV) per-texture UV channel / mapping selectors:
            // anything but 0 is another UV set or a view-based mapping.
            return !UsesOtherMapping(material, propertyName + "_UVMode")
                && !UsesOtherMapping(material, propertyName + "UV");
        }

        private static bool UsesOtherMapping(Material material, string selector)
        {
            return material.HasProperty(selector) && Mathf.Abs(material.GetFloat(selector)) > 0.5f;
        }

        private static TexelDensityInfo? Measure(
            TextureInfo info,
            Dictionary<Material, List<(Renderer Renderer, int Submesh)>> slots,
            Dictionary<Renderer, MeshData> meshes,
            float faceTarget,
            float bodyTarget
        )
        {
            var head = new List<(double Required, double Area)>();
            var body = new List<(double Required, double Area)>();
            var seen = new HashSet<(Renderer, int, Material, string)>();

            foreach (var reference in info.References)
            {
                var material = reference.Material;
                if (
                    material == null
                    || !slots.TryGetValue(material, out var uses)
                    || !IsUvMapped(material, reference.PropertyName)
                )
                {
                    // Used somewhere we cannot measure: no limit for this texture.
                    return null;
                }

                var tiling = material.GetTextureScale(reference.PropertyName);
                double tilingArea = Math.Abs((double)tiling.x * tiling.y);
                if (tilingArea <= 0)
                    return null;

                foreach (var (renderer, submesh) in uses)
                {
                    if (!seen.Add((renderer, submesh, material, reference.PropertyName)))
                        continue;

                    if (!meshes.TryGetValue(renderer, out var data))
                    {
                        data = MeshData.Create(renderer);
                        meshes[renderer] = data;
                    }
                    if (data == null)
                        return null;

                    data.AddSamples(submesh, tilingArea, faceTarget, bodyTarget, head, body);
                }
            }

            double headRequired = WeightedPercentile(head);
            double bodyRequired = WeightedPercentile(body);
            if (headRequired <= 0 && bodyRequired <= 0)
                return null;

            return headRequired >= bodyRequired
                ? new TexelDensityInfo(headRequired, faceTarget, true)
                : new TexelDensityInfo(bodyRequired, bodyTarget, false);
        }

        private static Dictionary<Material, List<(Renderer, int)>> CollectSlots(
            IEnumerable<Renderer> renderers
        )
        {
            var slots = new Dictionary<Material, List<(Renderer, int)>>();
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;

                var mesh = GetMesh(renderer);
                if (mesh == null)
                    continue;

                var materials = renderer.sharedMaterials;
                // Extra material slots render the last submesh again.
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null)
                        continue;
                    if (!slots.TryGetValue(material, out var list))
                    {
                        list = new List<(Renderer, int)>();
                        slots[material] = list;
                    }
                    list.Add((renderer, Mathf.Min(i, mesh.subMeshCount - 1)));
                }
            }
            return slots;
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

        /// <summary>
        /// Area-weighted percentile of requirements. Collapsed-UV triangles count with zero
        /// requirement: a single texel colors them.
        /// </summary>
        internal static double WeightedPercentile(List<(double Required, double Area)> samples)
        {
            if (samples.Count == 0)
                return 0;

            samples.Sort((a, b) => a.Required.CompareTo(b.Required));
            double total = 0;
            foreach (var s in samples)
                total += s.Area;

            double threshold = total * Percentile;
            double cumulative = 0;
            foreach (var s in samples)
            {
                cumulative += s.Area;
                if (cumulative >= threshold)
                    return s.Required;
            }
            return samples[samples.Count - 1].Required;
        }

        /// <summary>
        /// A renderer's mesh in its current pose and scale, with which vertices the head moves.
        /// </summary>
        private sealed class MeshData : IDisposable
        {
            private readonly Mesh _mesh;
            private readonly Vector3[] _vertices;
            private readonly Vector2[] _uvs;
            private readonly bool[] _headVertex;
            private readonly bool _allHead;
            private readonly Mesh _baked;

            private MeshData(
                Mesh mesh,
                Vector3[] vertices,
                Vector2[] uvs,
                bool[] headVertex,
                bool allHead,
                Mesh baked
            )
            {
                _mesh = mesh;
                _vertices = vertices;
                _uvs = uvs;
                _headVertex = headVertex;
                _allHead = allHead;
                _baked = baked;
            }

            public static MeshData Create(Renderer renderer)
            {
                var mesh = GetMesh(renderer);
                // Mesh data stays readable in the editor even with Read/Write off.
                if (mesh == null)
                    return null;

                var uvs = mesh.uv;
                if (uvs == null || uvs.Length != mesh.vertexCount)
                    return null;

                var head = FindHead(renderer);
                Vector3[] vertices;
                Mesh baked = null;
                bool[] headVertex = null;
                bool allHead = head == null;

                if (renderer is SkinnedMeshRenderer skinned)
                {
                    // Current pose with blendshapes, in the renderer's local space (useScale:
                    // exact, so the renderer's transform below gives world positions).
                    baked = new Mesh();
                    skinned.BakeMesh(baked, true);
                    vertices = baked.vertices;
                    if (vertices.Length != mesh.vertexCount)
                    {
                        Object.DestroyImmediate(baked);
                        return null;
                    }

                    if (head != null)
                        headVertex = FindHeadVertices(skinned, mesh, head, out allHead);
                }
                else
                {
                    vertices = mesh.vertices;
                    if (head != null)
                        allHead = renderer.transform.IsChildOf(head);
                }

                // Translation does not change areas, so directions are enough.
                var matrix = renderer.transform.localToWorldMatrix;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = matrix.MultiplyVector(vertices[i]);

                return new MeshData(mesh, vertices, uvs, headVertex, allHead, baked);
            }

            public void AddSamples(
                int submesh,
                double tilingArea,
                float faceTarget,
                float bodyTarget,
                List<(double Required, double Area)> head,
                List<(double Required, double Area)> body
            )
            {
                if (submesh < 0 || submesh >= _mesh.subMeshCount)
                    return;
                if (_mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    return;

                var indices = _mesh.GetTriangles(submesh);
                double faceTarget2 = (double)faceTarget * faceTarget;
                double bodyTarget2 = (double)bodyTarget * bodyTarget;

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];

                    // m² to cm²
                    double surface =
                        Vector3
                            .Cross(_vertices[b] - _vertices[a], _vertices[c] - _vertices[a])
                            .magnitude
                        * 0.5
                        * 10000.0;
                    if (surface < MinSurfaceArea)
                        continue;

                    var uvA = _uvs[a];
                    var uvB = _uvs[b];
                    var uvC = _uvs[c];
                    double uvArea =
                        Math.Abs(
                            (double)(uvB.x - uvA.x) * (uvC.y - uvA.y)
                                - (double)(uvC.x - uvA.x) * (uvB.y - uvA.y)
                        )
                        * 0.5
                        * tilingArea;

                    bool isHead =
                        _allHead
                        || (
                            _headVertex != null
                            && (_headVertex[a] || _headVertex[b] || _headVertex[c])
                        );
                    double target2 = isHead ? faceTarget2 : bodyTarget2;

                    // density² = uvArea * pixels / surface  =>  pixels = target² * surface / uvArea
                    double required = uvArea < MinUvArea ? 0 : target2 * surface / uvArea;
                    (isHead ? head : body).Add((required, surface));
                }
            }

            public void Dispose()
            {
                if (_baked != null)
                    Object.DestroyImmediate(_baked);
            }

            private static Transform FindHead(Renderer renderer)
            {
                var animators = renderer.GetComponentsInParent<Animator>(true);
                foreach (var animator in animators)
                {
                    if (animator.avatar == null || !animator.avatar.isHuman)
                        continue;
                    var head = animator.GetBoneTransform(HumanBodyBones.Head);
                    if (head != null)
                        return head;
                }
                return null;
            }

            private static bool[] FindHeadVertices(
                SkinnedMeshRenderer skinned,
                Mesh mesh,
                Transform head,
                out bool allHead
            )
            {
                allHead = false;
                var bones = skinned.bones;
                var weights = mesh.boneWeights;
                if (bones == null || bones.Length == 0 || weights.Length != mesh.vertexCount)
                {
                    // Unskinned: moved by the renderer's root bone or transform.
                    var root = skinned.rootBone != null ? skinned.rootBone : skinned.transform;
                    allHead = root.IsChildOf(head);
                    return null;
                }

                var headBone = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    headBone[i] = bones[i] != null && bones[i].IsChildOf(head);

                // Bone weights are sorted by weight: index 0 is the dominant bone.
                var result = new bool[weights.Length];
                for (int i = 0; i < weights.Length; i++)
                {
                    int bone = weights[i].boneIndex0;
                    result[i] = bone >= 0 && bone < headBone.Length && headBone[bone];
                }
                return result;
            }
        }
    }
}
