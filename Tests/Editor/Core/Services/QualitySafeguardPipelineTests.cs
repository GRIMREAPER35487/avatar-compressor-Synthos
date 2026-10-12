using System.IO;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.tests
{
    /// <summary>
    /// Pipeline tests for the banding safeguard and for size filters when the import Max Size
    /// is not respected.
    /// </summary>
    [TestFixture]
    internal sealed class QualitySafeguardPipelineTests
    {
        private const string TestFolder = "Assets/_LAC_QUALITY_TMP";
        private const int Size = 384;

        private GameObject _root;
        private GameObject _configHolder;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "_LAC_QUALITY_TMP");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root);
            if (_configHolder != null)
                Object.DestroyImmediate(_configHolder);
            AssetDatabase.DeleteAsset(TestFolder);
        }

        [TestCase(true, TextureFormat.BC7)]
        [TestCase(false, TextureFormat.DXT1)]
        public void Compress_SmoothGradient_UpgradesFromDxt1OnlyWhenSafeguardIsOn(
            bool avoidArtifacts,
            TextureFormat expected
        )
        {
            var gradient = CreateGradientAsset();
            var config = CreateConfig();
            config.AvoidCompressionArtifacts = avoidArtifacts;

            var material = new Material(Shader.Find("Standard")) { mainTexture = gradient };
            _root = new GameObject("Root");
            _root.AddComponent<MeshRenderer>().sharedMaterial = material;

            new TextureCompressorService(
                config,
                AnalysisBackendPreference.Auto,
                ResizeBackendPreference.CPU
            ).Compress(_root, false);

            var result = (Texture2D)_root.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(result.format, Is.EqualTo(expected));

            Object.DestroyImmediate(result);
            Object.DestroyImmediate(_root.GetComponent<MeshRenderer>().sharedMaterial);
            Object.DestroyImmediate(material);
        }

        [Test]
        public void SizeFilters_UseSourceSize_WhenNotRespectingImportMaxSize()
        {
            // A 1024 file imported at 256: High Quality-like filters skip anything under 512.
            string path = $"{TestFolder}/Large.png";
            var file = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            File.WriteAllBytes(path, file.EncodeToPNG());
            Object.DestroyImmediate(file);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 256;
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture.width, Is.EqualTo(256));

            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            try
            {
                var respecting = Collect(material, null);
                var notRespecting = Collect(
                    material,
                    t => TextureSourceCache.GetPlannedBaseSize(t, false, true, 2048)
                );

                Assert.That(respecting.IsProcessed, Is.False);
                Assert.That(respecting.SkipReason, Is.EqualTo(SkipReason.TooSmall));
                Assert.That(notRespecting.IsProcessed, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void Compress_SmallImportOfLargeFile_ProcessedOnlyWhenFiltersUseSourceSize(
            bool filtersUseSource,
            bool expectProcessed
        )
        {
            // A detailed 1024 file imported at 256; the filters skip anything under 512. When
            // processed it grows back toward the source size, so the result differs.
            string path = $"{TestFolder}/Large.png";
            var file = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            var pixels = new Color32[1024 * 1024];
            for (int y = 0; y < 1024; y++)
            {
                for (int x = 0; x < 1024; x++)
                {
                    byte v = (byte)((x ^ y) * 4);
                    pixels[y * 1024 + x] = new Color32(v, (byte)(255 - v), (byte)(x * 8), 255);
                }
            }
            file.SetPixels32(pixels);
            File.WriteAllBytes(path, file.EncodeToPNG());
            Object.DestroyImmediate(file);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 256;
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            var config = CreateConfig();
            config.MinSourceSize = 512;
            config.SkipIfSmallerThan = 512;
            config.RespectImportMaxSize = false;
            config.SizeFiltersUseSourceSize = filtersUseSource;

            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _root = new GameObject("Root");
            _root.AddComponent<MeshRenderer>().sharedMaterial = material;

            new TextureCompressorService(
                config,
                AnalysisBackendPreference.Auto,
                ResizeBackendPreference.CPU,
                sourceCache: new TextureSourceCache()
            ).Compress(_root, false);

            var result = (Texture2D)_root.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(result != texture, Is.EqualTo(expectProcessed));

            if (result != texture)
                Object.DestroyImmediate(result);
            Object.DestroyImmediate(_root.GetComponent<MeshRenderer>().sharedMaterial);
            Object.DestroyImmediate(material);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Compress_FilteredTexture_KeepsSizeAndChangesFormatOnlyWhenReformatOn(
            bool reformat
        )
        {
            // A 1024 file imported uncompressed at 256; the filters catch anything under 512.
            string path = $"{TestFolder}/Small.png";
            var file = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            File.WriteAllBytes(path, file.EncodeToPNG());
            Object.DestroyImmediate(file);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 256;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture.format, Is.EqualTo(TextureFormat.RGBA32));

            var config = CreateConfig();
            config.MinSourceSize = 512;
            config.SkipIfSmallerThan = 512;
            config.RespectImportMaxSize = false;
            config.SizeFiltersUseSourceSize = false;
            config.RespectImportFormat = true;
            config.ReformatFilteredTextures = reformat;

            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            _root = new GameObject("Root");
            _root.AddComponent<MeshRenderer>().sharedMaterial = material;

            new TextureCompressorService(
                config,
                AnalysisBackendPreference.Auto,
                ResizeBackendPreference.CPU,
                sourceCache: new TextureSourceCache()
            ).Compress(_root, false);

            var result = (Texture2D)_root.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            if (reformat)
            {
                Assert.That(result, Is.Not.SameAs(texture));
                Assert.That(result.width, Is.EqualTo(256));
                Assert.That(result.format, Is.Not.EqualTo(TextureFormat.RGBA32));
                Object.DestroyImmediate(result);
            }
            else
            {
                Assert.That(result, Is.SameAs(texture));
            }

            Object.DestroyImmediate(_root.GetComponent<MeshRenderer>().sharedMaterial);
            Object.DestroyImmediate(material);
        }

        private static TextureInfo Collect(
            Material material,
            System.Func<Texture2D, Vector2Int> filterSize
        )
        {
            var collector = new TextureCollector(
                minSourceSize: 512,
                skipIfSmallerThan: 512,
                processMainTextures: true,
                processNormalMaps: true,
                processEmissionMaps: true,
                processOtherTextures: true,
                skipUnknownUncompressedTextures: false,
                filterSize: filterSize
            );
            var textures = new System.Collections.Generic.Dictionary<Texture2D, TextureInfo>();
            collector.CollectFromMaterials(new[] { material }, textures, collectAll: true);
            Assert.That(textures.Count, Is.EqualTo(1));
            foreach (var info in textures.Values)
                return info;
            return null;
        }

        private TextureCompressor CreateConfig()
        {
            _configHolder = new GameObject("ConfigHolder");
            var config = _configHolder.AddComponent<TextureCompressor>();
            config.ApplyPreset(CompressorPreset.Balanced);
            config.TargetPlatform = CompressionPlatform.Desktop;
            config.MinSourceSize = 0;
            config.SkipIfSmallerThan = 0;
            config.MinDivisor = 1;
            config.MaxDivisor = 1;
            config.ExcludedPaths.Clear();
            return config;
        }

        private static Texture2D CreateGradientAsset()
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true);
            var pixels = new Color32[Size * Size];
            var from = new Color32(232, 190, 172, 255);
            var to = new Color32(150, 104, 96, 255);
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                    pixels[y * Size + x] = Color32.Lerp(from, to, x / (float)(Size - 1));
            }
            texture.SetPixels32(pixels);
            texture.Apply(true);
            AssetDatabase.CreateAsset(texture, $"{TestFolder}/Gradient.asset");
            return texture;
        }
    }
}
