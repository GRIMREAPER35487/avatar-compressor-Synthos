using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using UnityEngine;

namespace dev.limitex.avatar.compressor.tests
{
    /// <summary>
    /// Tests for the "Respect import format" toggle and size handling used when resizing from
    /// a full-quality source.
    /// </summary>
    [TestFixture]
    internal sealed class SourceTextureOptionsTests
    {
        [Test]
        public void ResolveTargetFormat_RespectingImport_KeepsCompressedFormat()
        {
            var selector = new TextureFormatSelector(CompressionPlatform.Desktop);

            var format = selector.ResolveTargetFormat(TextureFormat.BC7, false, 0f, false, null);

            Assert.That(format, Is.EqualTo(TextureFormat.BC7));
        }

        [Test]
        public void ResolveTargetFormat_NotRespectingImport_ChoosesFromContent()
        {
            var selector = new TextureFormatSelector(
                CompressionPlatform.Desktop,
                respectImportFormat: false
            );

            // A simple opaque texture imported as BC7 drops to DXT1.
            var format = selector.ResolveTargetFormat(TextureFormat.BC7, false, 0f, false, null);

            Assert.That(format, Is.EqualTo(TextureFormat.DXT1));
        }

        [Test]
        public void ResolveTargetFormat_FrozenFormat_WinsOverToggle()
        {
            var selector = new TextureFormatSelector(CompressionPlatform.Desktop);

            var format = selector.ResolveTargetFormat(
                TextureFormat.DXT1,
                false,
                0f,
                false,
                FrozenTextureFormat.BC7
            );

            Assert.That(format, Is.EqualTo(TextureFormat.BC7));
        }

        [TestCase(true, TextureFormat.DXT1, null, ExpectedResult = true)]
        [TestCase(false, TextureFormat.DXT1, null, ExpectedResult = false)]
        [TestCase(true, TextureFormat.RGBA32, null, ExpectedResult = false)]
        [TestCase(false, TextureFormat.BC7, FrozenTextureFormat.BC7, ExpectedResult = true)]
        [TestCase(true, TextureFormat.DXT1, FrozenTextureFormat.BC7, ExpectedResult = false)]
        [TestCase(true, TextureFormat.DXT1, FrozenTextureFormat.Auto, ExpectedResult = true)]
        public bool KeepsImportFormat_MatchesResolveTargetFormat(
            bool respectImportFormat,
            TextureFormat importFormat,
            FrozenTextureFormat? frozen
        )
        {
            var selector = new TextureFormatSelector(
                CompressionPlatform.Desktop,
                respectImportFormat: respectImportFormat
            );

            return selector.KeepsImportFormat(importFormat, frozen);
        }

        [Test]
        public void CalculateTargetSize_NoDownscale_KeepsBaseSize()
        {
            var processor = new TextureProcessor(32, 2048, true, ResizeBackendPreference.CPU);
            var analysis = new TextureAnalysisResult(0.9f, 1, new Vector2Int(1024, 1024));

            // A full-quality 2048 source with a 512 base (creator's import cap respected)
            // must come out at 512, not at the source size.
            var size = processor.CalculateTargetSize(new Vector2Int(512, 512), analysis);

            Assert.That(size, Is.EqualTo(new Vector2Int(512, 512)));
        }

        [Test]
        public void CalculateTargetSize_Downscale_UsesRecommendedResolution()
        {
            var processor = new TextureProcessor(32, 2048, true, ResizeBackendPreference.CPU);
            var analysis = new TextureAnalysisResult(0.1f, 4, new Vector2Int(256, 256));

            var size = processor.CalculateTargetSize(new Vector2Int(1024, 1024), analysis);

            Assert.That(size, Is.EqualTo(new Vector2Int(256, 256)));
        }
    }
}
