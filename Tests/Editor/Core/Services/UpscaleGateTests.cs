using System.IO;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.tests
{
    [TestFixture]
    internal sealed class UpscaleGateTests
    {
        private const string TestFolder = "Assets/_LAC_UPSCALE_TMP";

        private static readonly Vector2Int Imported = new Vector2Int(512, 512);
        private static readonly Vector2Int Larger = new Vector2Int(1024, 1024);

        private GameObject _root;
        private GameObject _configHolder;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root);
            if (_configHolder != null)
                Object.DestroyImmediate(_configHolder);
            AssetDatabase.DeleteAsset(TestFolder);
        }

        [Test]
        public void NotLarger_IsUnchanged()
        {
            var size = UpscaleGate.Apply(
                new Vector2Int(256, 256),
                Imported,
                0f,
                0.1f,
                null,
                out var decision
            );

            Assert.That(size, Is.EqualTo(new Vector2Int(256, 256)));
            Assert.That(decision, Is.EqualTo(UpscaleGate.Decision.NotLarger));
        }

        [Test]
        public void LowDetail_KeepsImportedSize()
        {
            var size = UpscaleGate.Apply(Larger, Imported, 0.05f, 0.1f, null, out var decision);

            Assert.That(size, Is.EqualTo(Imported));
            Assert.That(decision, Is.EqualTo(UpscaleGate.Decision.LowDetail));
        }

        [Test]
        public void Detailed_NoDensity_Grows()
        {
            var size = UpscaleGate.Apply(Larger, Imported, 0.5f, 0.1f, null, out var decision);

            Assert.That(size, Is.EqualTo(Larger));
            Assert.That(decision, Is.EqualTo(UpscaleGate.Decision.Allowed));
        }

        [Test]
        public void Detailed_AlreadySharpOnAvatar_KeepsImportedSize()
        {
            // Needs 100 px/cm over a surface where 512 already gives 200.
            var density = new TexelDensityInfo(512.0 * 512.0 / 4.0, 100f, true);

            var size = UpscaleGate.Apply(Larger, Imported, 0.5f, 0.1f, density, out var decision);

            Assert.That(size, Is.EqualTo(Imported));
            Assert.That(decision, Is.EqualTo(UpscaleGate.Decision.AlreadySharp));
        }

        [Test]
        public void Detailed_TooBlurryOnAvatar_Grows()
        {
            // 512 gives only 50 px/cm where 100 is needed.
            var density = new TexelDensityInfo(512.0 * 512.0 * 4.0, 100f, false);

            var size = UpscaleGate.Apply(Larger, Imported, 0.5f, 0.1f, density, out var decision);

            Assert.That(size, Is.EqualTo(Larger));
            Assert.That(decision, Is.EqualTo(UpscaleGate.Decision.Allowed));
        }

        [TestCase(false, 64)]
        [TestCase(true, 128)]
        public void Compress_FromLargerSource_GrowsOnlyWhenDetailed(bool detailed, int expected)
        {
            AssetDatabase.CreateFolder("Assets", "_LAC_UPSCALE_TMP");
            var texture = CreateSourceImportedSmall(detailed);
            Assert.That(texture.width, Is.EqualTo(64));

            _configHolder = new GameObject("Config");
            var config = _configHolder.AddComponent<TextureCompressor>();
            config.ApplyPreset(CompressorPreset.HighQuality);
            config.TargetPlatform = CompressionPlatform.Desktop;
            config.MinSourceSize = 0;
            config.SkipIfSmallerThan = 0;
            config.MinResolution = 32;
            config.MinDivisor = 2;
            config.MaxDivisor = 2;
            config.ExcludedPaths.Clear();
            config.RespectImportMaxSize = false;
            config.AvoidCompressionArtifacts = false;

            // A 1 m quad: far below the target density at any of these sizes.
            _root = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _root.GetComponent<MeshRenderer>().sharedMaterial = material;

            new TextureCompressorService(
                config,
                AnalysisBackendPreference.Auto,
                ResizeBackendPreference.CPU,
                sourceCache: new TextureSourceCache()
            ).Compress(_root, false);

            var result = (Texture2D)_root.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(result.width, Is.EqualTo(expected));

            if (result != texture)
                Object.DestroyImmediate(result);
            Object.DestroyImmediate(_root.GetComponent<MeshRenderer>().sharedMaterial);
            Object.DestroyImmediate(material);
        }

        /// <summary>A 256 source file imported with Max Size 64.</summary>
        private static Texture2D CreateSourceImportedSmall(bool detailed)
        {
            const int size = 256;
            var file = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Structured detail (an XOR pattern) rather than noise.
                    byte v = (byte)((x ^ y) * 4);
                    pixels[y * size + x] = detailed
                        ? new Color32(v, (byte)(255 - v), (byte)(x * 8), 255)
                        : new Color32(90, 140, 200, 255);
                }
            }
            file.SetPixels32(pixels);
            string path = $"{TestFolder}/Source.png";
            File.WriteAllBytes(path, file.EncodeToPNG());
            Object.DestroyImmediate(file);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 64;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
