using System.Collections.Generic;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.tests
{
    [TestFixture]
    internal sealed class TexelDensityAnalyzerTests
    {
        private const string TestFolder = "Assets/_LAC_DENSITY_TMP";

        private readonly List<Object> _created = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "_LAC_DENSITY_TMP");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _created)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }
            _created.Clear();
            AssetDatabase.DeleteAsset(TestFolder);
        }

        [Test]
        public void TargetDensity_MatchesViewingDistance()
        {
            // 25 px/degree; 1 cm at 15 cm spans ~3.81 degrees.
            Assert.That(TexelDensityAnalyzer.TargetDensity(15f), Is.EqualTo(95.3f).Within(0.5f));
            Assert.That(TexelDensityAnalyzer.TargetDensity(30f), Is.EqualTo(47.7f).Within(0.5f));
        }

        [Test]
        public void Quad10cm_1024Texture_MeasuresAbout102PixelsPerCm()
        {
            var texture = CreateTexture(1024);
            var (renderer, material) = CreateQuad(0.1f, texture);

            var info = Analyze(renderer, material, texture);

            Assert.That(info.HasValue, Is.True);
            Assert.That(
                info.Value.DensityAt(new Vector2Int(1024, 1024)),
                Is.EqualTo(102.4f).Within(0.5f)
            );
            // No humanoid: everything uses the stricter face distance, which 1024 meets.
            Assert.That(info.Value.FromHead, Is.True);
            Assert.That(info.Value.GetDivisor(new Vector2Int(1024, 1024)), Is.EqualTo(1));
        }

        [Test]
        public void Quad1cm_1024Texture_CanBeDividedBy8()
        {
            var texture = CreateTexture(1024);
            var (renderer, material) = CreateQuad(0.01f, texture);

            var info = Analyze(renderer, material, texture);

            // Needs ~95 px across 1 cm: 1024 / 8 = 128 still meets it, 1024 / 16 = 64 does not.
            Assert.That(info.Value.GetDivisor(new Vector2Int(1024, 1024)), Is.EqualTo(8));
        }

        [Test]
        public void Tiling_MultipliesDensity()
        {
            var texture = CreateTexture(1024);
            var (renderer, material) = CreateQuad(0.1f, texture);
            material.SetTextureScale("_MainTex", new Vector2(2f, 2f));

            var info = Analyze(renderer, material, texture);

            Assert.That(
                info.Value.DensityAt(new Vector2Int(1024, 1024)),
                Is.EqualTo(204.8f).Within(1f)
            );
        }

        [Test]
        public void ScaledTransform_MeasuresWorldSize()
        {
            var texture = CreateTexture(1024);
            var (renderer, material) = CreateQuad(0.1f, texture);
            renderer.transform.localScale = new Vector3(2f, 2f, 2f);

            var info = Analyze(renderer, material, texture);

            Assert.That(
                info.Value.DensityAt(new Vector2Int(1024, 1024)),
                Is.EqualTo(51.2f).Within(0.5f)
            );
        }

        [Test]
        public void SkinnedMesh_ScaledTransform_MeasuresWorldSize()
        {
            var texture = CreateTexture(1024);
            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _created.Add(material);
            var go = new GameObject("Skinned");
            _created.Add(go);
            go.transform.localScale = new Vector3(2f, 2f, 2f);
            var skinned = go.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = CreateQuadMesh(0.1f);
            skinned.sharedMaterial = material;

            var info = Analyze(skinned, material, texture);

            Assert.That(
                info.Value.DensityAt(new Vector2Int(1024, 1024)),
                Is.EqualTo(51.2f).Within(0.5f)
            );
        }

        // Skinned vertices follow the bone, not the renderer's transform.
        [TestCase(1f, 2f, 102.4f)]
        [TestCase(2f, 1f, 51.2f)]
        public void SkinnedMesh_WithBone_MeasuresPosedWorldSize(
            float boneScale,
            float rendererScale,
            float expected
        )
        {
            var texture = CreateTexture(1024);
            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _created.Add(material);

            var root = new GameObject("Root");
            _created.Add(root);
            var bone = new GameObject("Bone").transform;
            bone.SetParent(root.transform, false);
            bone.localScale = Vector3.one * boneScale;
            var go = new GameObject("Skinned");
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = Vector3.one * rendererScale;

            var mesh = CreateQuadMesh(0.1f);
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            };
            mesh.bindposes = new[] { Matrix4x4.identity };

            var skinned = go.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { bone };
            skinned.rootBone = bone;
            skinned.sharedMaterial = material;

            var info = Analyze(skinned, material, texture);

            Assert.That(
                info.Value.DensityAt(new Vector2Int(1024, 1024)),
                Is.EqualTo(expected).Within(0.5f)
            );
        }

        [Test]
        public void MaterialNotOnAnyRenderer_IsNotMeasured()
        {
            var texture = CreateTexture(1024);
            var (renderer, _) = CreateQuad(0.1f, texture);
            var animationOnly = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _created.Add(animationOnly);

            var info = Analyze(renderer, animationOnly, texture);

            Assert.That(info.HasValue, Is.False);
        }

        [Test]
        public void IsUvMapped_RejectsViewAndLookupTextures()
        {
            var material = new Material(Shader.Find("Standard"));
            _created.Add(material);

            Assert.That(TexelDensityAnalyzer.IsUvMapped(material, "_MainTex"), Is.True);
            Assert.That(TexelDensityAnalyzer.IsUvMapped(material, "_BumpMap"), Is.True);
            Assert.That(TexelDensityAnalyzer.IsUvMapped(material, "_MatCapTex"), Is.False);
            Assert.That(TexelDensityAnalyzer.IsUvMapped(material, "_Ramp"), Is.False);
            Assert.That(TexelDensityAnalyzer.IsUvMapped(material, "_MainGradationTex"), Is.False);
            Assert.That(TexelDensityAnalyzer.IsUvMapped(material, "_DecalTexture"), Is.False);
        }

        [Test]
        public void WeightedPercentile_IgnoresSmallOutliers()
        {
            var samples = new List<(double, double)>
            {
                (100, 50),
                (200, 49),
                (1_000_000, 1), // 1% of the area: a stretched, hidden triangle
            };

            Assert.That(TexelDensityAnalyzer.WeightedPercentile(samples), Is.EqualTo(200));
        }

        [TestCase(true, 128)]
        [TestCase(false, 1024)]
        public void Compress_TinyMesh_ShrinksOnlyWhenLimitIsOn(bool limit, int expectedSize)
        {
            var texture = CreateTexture(1024);
            var (renderer, _) = CreateQuad(0.01f, texture);

            var holder = new GameObject("Config");
            _created.Add(holder);
            var config = holder.AddComponent<TextureCompressor>();
            config.ApplyPreset(CompressorPreset.Balanced);
            config.TargetPlatform = CompressionPlatform.Desktop;
            config.MinDivisor = 1;
            config.MaxDivisor = 1;
            config.ExcludedPaths.Clear();
            config.AvoidCompressionArtifacts = false;
            config.LimitByOnAvatarSize = limit;

            new TextureCompressorService(
                config,
                AnalysisBackendPreference.Auto,
                ResizeBackendPreference.CPU
            ).Compress(renderer.gameObject, false);

            var result = (Texture2D)renderer.sharedMaterial.mainTexture;
            _created.Add(renderer.sharedMaterial);
            if (result != texture)
                _created.Add(result);
            Assert.That(result.width, Is.EqualTo(expectedSize));
        }

        [Test]
        public void ApplyFloor_DetailedAndUnderTarget_KeepsBaseSize()
        {
            var processor = new TextureProcessor(32, 2048, true, ResizeBackendPreference.CPU);
            // 2048 gives 20 px/cm where 48 is needed.
            var density = new TexelDensityInfo(2048.0 * 2048.0 * 5.76, 48f, false);
            var analysis = new TextureAnalysisResult(0.24f, 2, new Vector2Int(1024, 1024));

            var floored = TexelDensityAnalyzer.ApplyFloor(
                analysis,
                new Vector2Int(2048, 2048),
                density,
                1,
                0.1f,
                processor
            );

            Assert.That(floored.RecommendedDivisor, Is.EqualTo(1));
            Assert.That(floored.RecommendedResolution, Is.EqualTo(new Vector2Int(2048, 2048)));
        }

        [Test]
        public void ApplyFloor_StopsAtDivisorThatMeetsTarget()
        {
            var processor = new TextureProcessor(32, 2048, true, ResizeBackendPreference.CPU);
            // Needs 512 pixels across: 2048 / 8 = 256 falls short, 2048 / 4 = 512 meets it.
            var density = new TexelDensityInfo(512.0 * 512.0, 50f, false);
            var analysis = new TextureAnalysisResult(0.5f, 8, new Vector2Int(256, 256));

            var floored = TexelDensityAnalyzer.ApplyFloor(
                analysis,
                new Vector2Int(2048, 2048),
                density,
                1,
                0.1f,
                processor
            );

            Assert.That(floored.RecommendedDivisor, Is.EqualTo(4));
        }

        [Test]
        public void ApplyFloor_LowDetailOrMinDivisor_StillShrinks()
        {
            var processor = new TextureProcessor(32, 2048, true, ResizeBackendPreference.CPU);
            var density = new TexelDensityInfo(2048.0 * 2048.0 * 5.76, 48f, false);
            var base2048 = new Vector2Int(2048, 2048);

            var lowDetail = TexelDensityAnalyzer.ApplyFloor(
                new TextureAnalysisResult(0.04f, 2, new Vector2Int(1024, 1024)),
                base2048,
                density,
                1,
                0.1f,
                processor
            );
            var minDivisor = TexelDensityAnalyzer.ApplyFloor(
                new TextureAnalysisResult(0.5f, 8, new Vector2Int(256, 256)),
                base2048,
                density,
                2,
                0.1f,
                processor
            );

            Assert.That(lowDetail.RecommendedDivisor, Is.EqualTo(2));
            Assert.That(minDivisor.RecommendedDivisor, Is.EqualTo(2));
        }

        [Test]
        public void IsLimitedByMaxResolution_CappedDetailedUnderTarget_SuggestsLargerSize()
        {
            // 2048 gives 11 px/cm where 48 is needed; the 4096 source would give 22.
            var density = new TexelDensityInfo(
                2048.0 * 2048.0 * (48.0 / 11.0) * (48.0 / 11.0),
                48f,
                false
            );

            bool limited = TexelDensityAnalyzer.IsLimitedByMaxResolution(
                new Vector2Int(2048, 2048),
                new Vector2Int(4096, 4096),
                2048,
                density,
                0.24f,
                0.1f,
                out var wanted
            );

            Assert.That(limited, Is.True);
            Assert.That(wanted, Is.EqualTo(new Vector2Int(4096, 4096)));
        }

        [TestCase(1024, 4096, 0.24f, 11f)] // not at the cap
        [TestCase(2048, 2048, 0.24f, 11f)] // no larger image available
        [TestCase(2048, 4096, 0.04f, 11f)] // low detail
        [TestCase(2048, 4096, 0.24f, 60f)] // already sharp enough
        public void IsLimitedByMaxResolution_OtherCases_NoWarning(
            int result,
            int full,
            float complexity,
            float densityAtResult
        )
        {
            var density = new TexelDensityInfo(
                (double)result * result * (48.0 / densityAtResult) * (48.0 / densityAtResult),
                48f,
                false
            );

            bool limited = TexelDensityAnalyzer.IsLimitedByMaxResolution(
                new Vector2Int(result, result),
                new Vector2Int(full, full),
                2048,
                density,
                complexity,
                0.1f,
                out _
            );

            Assert.That(limited, Is.False);
        }

        [TestCase(true, 1024)]
        [TestCase(false, 512)]
        public void Compress_LargeDetailedSurface_KeepsSizeOnlyWithFloor(
            bool keepDetail,
            int expectedSize
        )
        {
            var texture = CreateDetailedTexture(1024);
            var (renderer, _) = CreateQuad(1f, texture);

            var holder = new GameObject("Config");
            _created.Add(holder);
            var config = holder.AddComponent<TextureCompressor>();
            config.ApplyPreset(CompressorPreset.Balanced);
            config.TargetPlatform = CompressionPlatform.Desktop;
            config.MinDivisor = 1;
            config.MaxDivisor = 2;
            // Wide thresholds: the pattern lands below the midpoint, so the detail analysis halves it.
            config.LowComplexityThreshold = 0.01f;
            config.HighComplexityThreshold = 0.99f;
            config.ExcludedPaths.Clear();
            config.AvoidCompressionArtifacts = false;
            config.KeepVisibleDetail = keepDetail;

            new TextureCompressorService(
                config,
                AnalysisBackendPreference.Auto,
                ResizeBackendPreference.CPU
            ).Compress(renderer.gameObject, false);

            var result = (Texture2D)renderer.sharedMaterial.mainTexture;
            _created.Add(renderer.sharedMaterial);
            if (result != texture)
                _created.Add(result);
            Assert.That(result.width, Is.EqualTo(expectedSize));
        }

        private static Texture2D CreateDetailedTexture(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    byte v = (byte)((x ^ y) * 4);
                    pixels[y * size + x] = new Color32(v, (byte)(255 - v), (byte)(x * 8), 255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true);
            AssetDatabase.CreateAsset(texture, $"{TestFolder}/Detailed{size}.asset");
            return texture;
        }

        private static TexelDensityInfo? Analyze(
            Renderer renderer,
            Material material,
            Texture2D texture
        )
        {
            var info = new TextureInfo();
            info.References.Add(
                new MaterialTextureReference { Material = material, PropertyName = "_MainTex" }
            );
            var result = TexelDensityAnalyzer.Analyze(
                new[] { renderer },
                new Dictionary<Texture2D, TextureInfo> { { texture, info } },
                TexelDensityAnalyzer.DefaultFaceViewDistance,
                TexelDensityAnalyzer.DefaultBodyViewDistance
            );
            return result.TryGetValue(texture, out var density) ? density : (TexelDensityInfo?)null;
        }

        private (Renderer, Material) CreateQuad(float size, Texture2D texture)
        {
            var go = new GameObject("Quad");
            _created.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = CreateQuadMesh(size);
            var renderer = go.AddComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _created.Add(material);
            renderer.sharedMaterial = material;
            return (renderer, material);
        }

        private Mesh CreateQuadMesh(float size)
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0),
                    new Vector3(size, 0, 0),
                    new Vector3(0, size, 0),
                    new Vector3(size, size, 0),
                },
                uv = new[]
                {
                    new Vector2(0, 0),
                    new Vector2(1, 0),
                    new Vector2(0, 1),
                    new Vector2(1, 1),
                },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
            };
            _created.Add(mesh);
            return mesh;
        }

        private static Texture2D CreateTexture(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            var pixels = new Color32[size * size];
            var random = new System.Random(1);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(
                    (byte)random.Next(256),
                    (byte)random.Next(256),
                    (byte)random.Next(256),
                    255
                );
            }
            texture.SetPixels32(pixels);
            texture.Apply(true);
            AssetDatabase.CreateAsset(texture, $"{TestFolder}/Texture{size}.asset");
            return texture;
        }
    }
}
