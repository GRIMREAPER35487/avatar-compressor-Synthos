using System.IO;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.tests
{
    [TestFixture]
    internal sealed class TextureSourceCacheTests
    {
        private const string TestFolder = "Assets/_LAC_SOURCE_TMP";
        private const int Width = 256;
        private const int Height = 128;

        private string _path;
        private Color32[] _filePixels;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "_LAC_SOURCE_TMP");
        }

        [TearDown]
        public void TearDown()
        {
            if (_path != null)
            {
                string guid = AssetDatabase.AssetPathToGUID(_path);
                if (Directory.Exists(TextureSourceCache.CacheFolder))
                {
                    foreach (
                        var file in Directory.GetFiles(TextureSourceCache.CacheFolder, guid + "_*")
                    )
                        File.Delete(file);
                }
            }

            AssetDatabase.DeleteAsset(TestFolder);
            _path = null;
        }

        [Test]
        public void Load_ReturnsFullResolutionUncompressedPixels()
        {
            var imported = CreateTexture(maxSize: 64, normalMap: false);
            Assert.That(imported.width, Is.EqualTo(64), "Import should be capped by the creator.");

            var cache = new TextureSourceCache();
            cache.Prepare(new[] { imported });

            Assert.That(cache.TryGetCachedSize(imported, out var size), Is.True);
            Assert.That(size, Is.EqualTo(new Vector2Int(Width, Height)));

            var source = cache.Load(imported);
            try
            {
                Assert.That(source, Is.Not.Null);
                Assert.That(source.width, Is.EqualTo(Width));
                Assert.That(source.height, Is.EqualTo(Height));
                Assert.That(source.isReadable, Is.True);
                Assert.That(source.GetPixels32(), Is.EqualTo(_filePixels));
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void Prepare_LeavesOriginalImportSettingsUntouched()
        {
            var imported = CreateTexture(maxSize: 64, normalMap: false);
            string metaBefore = File.ReadAllText(_path + ".meta");

            new TextureSourceCache().Prepare(new[] { imported });

            Assert.That(File.ReadAllText(_path + ".meta"), Is.EqualTo(metaBefore));
            var importer = (TextureImporter)AssetImporter.GetAtPath(_path);
            Assert.That(importer.maxTextureSize, Is.EqualTo(64));
            Assert.That(
                importer.textureCompression,
                Is.EqualTo(TextureImporterCompression.Compressed)
            );
            Assert.That(
                AssetDatabase.IsValidFolder(BuildAssetStore.RootFolder + "/_SourceImport"),
                Is.False,
                "The temporary import folder must be removed."
            );
        }

        [Test]
        public void Load_NormalMap_ReturnsRawFileChannels()
        {
            var imported = CreateTexture(maxSize: 2048, normalMap: true);
            Assert.That(TextureSourceCache.IsRawNormalSource(imported), Is.True);

            var cache = new TextureSourceCache();
            cache.Prepare(new[] { imported });
            var source = cache.Load(imported);
            try
            {
                Assert.That(source, Is.Not.Null);
                Assert.That(source.isDataSRGB, Is.False);
                Assert.That(source.GetPixels32(), Is.EqualTo(_filePixels));
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void CanUseSource_RuntimeTexture_ReturnsFalse()
        {
            var runtime = new Texture2D(4, 4);
            try
            {
                Assert.That(TextureSourceCache.CanUseSource(runtime), Is.False);
                Assert.That(new TextureSourceCache().Load(runtime), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(runtime);
            }
        }

        [TestCase(4096, 2048, 2048, 2048, 1024)]
        [TestCase(1024, 512, 2048, 1024, 512)]
        [TestCase(512, 4096, 1024, 128, 1024)]
        public void FitWithin_ScalesDownKeepingAspect(
            int width,
            int height,
            int max,
            int expectedWidth,
            int expectedHeight
        )
        {
            Assert.That(
                TextureSourceCache.FitWithin(new Vector2Int(width, height), max),
                Is.EqualTo(new Vector2Int(expectedWidth, expectedHeight))
            );
        }

        private Texture2D CreateTexture(int maxSize, bool normalMap)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            _filePixels = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    _filePixels[y * Width + x] = new Color32(
                        (byte)x,
                        (byte)(y * 2),
                        (byte)((x + y) & 0xFF),
                        255
                    );
                }
            }
            texture.SetPixels32(_filePixels);
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            _path = $"{TestFolder}/Source.png";
            File.WriteAllBytes(_path, png);
            AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(_path);
            importer.maxTextureSize = maxSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            if (normalMap)
                importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Texture2D>(_path);
        }
    }
}
