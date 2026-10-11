using System;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace dev.limitex.avatar.compressor.tests
{
    [TestFixture]
    internal sealed class CompressionArtifactDetectorTests
    {
        private const int Size = 384;

        private static readonly TextureFormat[] s_formats =
        {
            TextureFormat.DXT1,
            TextureFormat.DXT5,
            TextureFormat.BC7,
            TextureFormat.ASTC_4x4,
            TextureFormat.ASTC_5x5,
            TextureFormat.ASTC_6x6,
            TextureFormat.ASTC_8x8,
        };

        [TestCase("skinGradient")]
        [TestCase("gentleGradient")]
        [TestCase("hueGradient")]
        [TestCase("radialGlow")]
        [TestCase("halfGradient")]
        public void Dxt1_OnSmoothColorChanges_IsUpgraded(string sample)
        {
            AssertArtifacts(sample, TextureFormat.DXT1, expected: true);
        }

        [Test]
        public void Dxt1_ThinLinesThroughFlatColors_IsUpgraded()
        {
            AssertArtifacts("animeFlatColors", TextureFormat.DXT1, expected: true);
        }

        [TestCase("noise")]
        [TestCase("noisyGradient")]
        [TestCase("flat")]
        [TestCase("twoToneEdges")]
        [TestCase("smallGradientPatch")]
        public void Dxt1_WhereArtifactsAreHiddenOrSmall_IsKept(string sample)
        {
            AssertArtifacts(sample, TextureFormat.DXT1, expected: false);
        }

        [TestCase("skinGradient")]
        [TestCase("radialGlow")]
        [TestCase("animeFlatColors")]
        public void SafeFormats_NeverReportArtifacts(string sample)
        {
            AssertArtifacts(sample, TextureFormat.BC7, expected: false);
            AssertArtifacts(sample, TextureFormat.ASTC_4x4, expected: false);
        }

        [Test]
        public void GetSaferFormat_StepsThroughLadderOfSamePlatform()
        {
            Assert.That(
                CompressionArtifactDetector.GetSaferFormat(TextureFormat.DXT1),
                Is.EqualTo(TextureFormat.BC7)
            );
            Assert.That(
                CompressionArtifactDetector.GetSaferFormat(TextureFormat.DXT5),
                Is.EqualTo(TextureFormat.BC7)
            );
            Assert.That(
                CompressionArtifactDetector.GetSaferFormat(TextureFormat.ASTC_8x8),
                Is.EqualTo(TextureFormat.ASTC_6x6)
            );
            Assert.That(
                CompressionArtifactDetector.GetSaferFormat(TextureFormat.ASTC_6x6),
                Is.EqualTo(TextureFormat.ASTC_5x5)
            );
            Assert.That(
                CompressionArtifactDetector.GetSaferFormat(TextureFormat.ASTC_5x5),
                Is.EqualTo(TextureFormat.ASTC_4x4)
            );
            Assert.That(CompressionArtifactDetector.IsCandidateFormat(TextureFormat.BC7), Is.False);
            Assert.That(
                CompressionArtifactDetector.IsCandidateFormat(TextureFormat.ASTC_4x4),
                Is.False
            );
        }

        [TestCase("noise", TextureFormat.ASTC_8x8, TextureFormat.ASTC_8x8)]
        [TestCase("flat", TextureFormat.DXT1, TextureFormat.DXT1)]
        [TestCase("skinGradient", TextureFormat.DXT1, TextureFormat.BC7)]
        [TestCase("skinGradient", TextureFormat.BC7, TextureFormat.BC7)]
        public void ChooseCleanFormat_KeepsCleanFormatsAndUpgradesBandedOnes(
            string sample,
            TextureFormat format,
            TextureFormat expected
        )
        {
            var texture = CreateSample(sample);
            try
            {
                Assert.That(
                    CompressionArtifactDetector.ChooseCleanFormat(texture, format),
                    Is.EqualTo(expected)
                );
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [TestCase("hueGradient", TextureFormat.ASTC_8x8)]
        [TestCase("animeFlatColors", TextureFormat.ASTC_6x6)]
        public void ChooseCleanFormat_Astc_StopsAtFirstCleanStep(
            string sample,
            TextureFormat start
        )
        {
            var texture = CreateSample(sample);
            try
            {
                var chosen = CompressionArtifactDetector.ChooseCleanFormat(texture, start);

                // Every skipped step had artifacts; the chosen step is clean unless it is the
                // untested final fallback.
                for (
                    var step = start;
                    step != chosen;
                    step = CompressionArtifactDetector.GetSaferFormat(step)
                )
                {
                    Assert.That(
                        CompressionArtifactDetector.WouldShowArtifacts(texture, step),
                        Is.True,
                        $"skipped clean {step}"
                    );
                }
                if (chosen != TextureFormat.ASTC_4x4)
                {
                    Assert.That(
                        CompressionArtifactDetector.WouldShowArtifacts(texture, chosen),
                        Is.False,
                        $"chose banded {chosen}"
                    );
                }
                Assert.That(chosen, Is.Not.EqualTo(start));
                Debug.Log($"[Ladder] {sample}: {start} -> {chosen}");
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void Measure_NonReadableOrTinyTexture_ReportsNothing()
        {
            var tiny = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            try
            {
                Assert.That(
                    CompressionArtifactDetector.WouldShowArtifacts(tiny, TextureFormat.DXT1),
                    Is.False
                );
            }
            finally
            {
                Object.DestroyImmediate(tiny);
            }
        }

        [Test, Explicit("Prints detector measurements used to calibrate the thresholds.")]
        public void Calibration_LogMeasurements()
        {
            foreach (var name in SampleNames)
            {
                var texture = CreateSample(name);
                try
                {
                    foreach (var format in s_formats)
                    {
                        var m = CompressionArtifactDetector.Measure(texture, format);
                        Debug.Log(
                            $"[Calibration] {name, -18} {format, -12} steps={m.StepFraction:F4} "
                                + $"banded={m.BandedTileFraction:F3} bad={m.BadPixelFraction:F4} "
                                + $"err={m.MeanSmoothError:F2} clean={m.CleanArea:F3} "
                                + $"area={m.ArtifactArea:F3} tiles={m.TilesTested} "
                                + $"-> {(m.HasArtifacts ? "UPGRADE" : "ok")}"
                        );
                    }
                }
                finally
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }

        private static readonly string[] SampleNames =
        {
            "skinGradient",
            "gentleGradient",
            "hueGradient",
            "radialGlow",
            "halfGradient",
            "smallGradientPatch",
            "noise",
            "noisyGradient",
            "flat",
            "twoToneEdges",
            "animeFlatColors",
        };

        private static void AssertArtifacts(string sample, TextureFormat format, bool expected)
        {
            var texture = CreateSample(sample);
            try
            {
                var m = CompressionArtifactDetector.Measure(texture, format);
                Assert.That(
                    m.HasArtifacts,
                    Is.EqualTo(expected),
                    $"{sample} as {format}: banded={m.BandedTileFraction:F3} "
                        + $"bad={m.BadPixelFraction:F4} clean={m.CleanArea:F3} area={m.ArtifactArea:F3}"
                );
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static Texture2D CreateSample(string name)
        {
            var skinA = new Color32(232, 190, 172, 255);
            var skinB = new Color32(150, 104, 96, 255);

            switch (name)
            {
                case "skinGradient":
                    return Create((x, y) => Lerp(skinA, skinB, x));
                case "gentleGradient":
                    return Create(
                        (x, y) =>
                            Lerp(
                                new Color32(110, 120, 140, 255),
                                new Color32(130, 140, 160, 255),
                                x
                            )
                    );
                case "hueGradient":
                    return Create(
                        (x, y) =>
                            Lerp(new Color32(255, 120, 200, 255), new Color32(90, 140, 255, 255), x)
                    );
                case "radialGlow":
                    return Create(
                        (x, y) =>
                        {
                            float dx = (x - Size / 2f) / (Size / 2f);
                            float dy = (y - Size / 2f) / (Size / 2f);
                            float t = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                            return Color32.Lerp(
                                new Color32(20, 10, 40, 255),
                                new Color32(255, 200, 120, 255),
                                t * t
                            );
                        }
                    );
                case "halfGradient":
                {
                    var random = new System.Random(3);
                    return Create(
                        (x, y) => x < Size / 2 ? Lerp(skinA, skinB, x * 2) : RandomColor(random)
                    );
                }
                case "smallGradientPatch":
                {
                    // One 48x48 gradient patch (under 2% of the texture) in a detailed texture.
                    var random = new System.Random(5);
                    return Create(
                        (x, y) => x < 48 && y < 48 ? Lerp(skinA, skinB, x * 8) : RandomColor(random)
                    );
                }
                case "noise":
                {
                    var random = new System.Random(7);
                    return Create((x, y) => RandomColor(random));
                }
                case "noisyGradient":
                {
                    var random = new System.Random(11);
                    return Create(
                        (x, y) =>
                        {
                            var c = Lerp(skinA, skinB, x);
                            int n = random.Next(-24, 25);
                            return new Color32(
                                (byte)Mathf.Clamp(c.r + n, 0, 255),
                                (byte)Mathf.Clamp(c.g + n, 0, 255),
                                (byte)Mathf.Clamp(c.b + n, 0, 255),
                                255
                            );
                        }
                    );
                }
                case "flat":
                    return Create((x, y) => new Color32(80, 160, 200, 255));
                case "twoToneEdges":
                    return Create(
                        (x, y) =>
                            ((x / 24) + (y / 24)) % 2 == 0
                                ? new Color32(230, 60, 80, 255)
                                : new Color32(60, 200, 120, 255)
                    );
                case "animeFlatColors":
                    // Flat fills separated by thin dark lines: several distinct colors meet
                    // inside single 4x4 blocks along the lines.
                    return Create(
                        (x, y) =>
                        {
                            if (x % 40 == 0 || y % 52 == 0)
                                return new Color32(40, 30, 50, 255);
                            switch ((x / 40) % 3)
                            {
                                case 0:
                                    return new Color32(250, 220, 200, 255);
                                case 1:
                                    return new Color32(120, 170, 240, 255);
                                default:
                                    return new Color32(240, 120, 150, 255);
                            }
                        }
                    );
                default:
                    throw new ArgumentException(name);
            }
        }

        private static Color32 Lerp(Color32 from, Color32 to, int x)
        {
            return Color32.Lerp(from, to, Mathf.Clamp01(x / (float)(Size - 1)));
        }

        private static Color32 RandomColor(System.Random random)
        {
            return new Color32(
                (byte)random.Next(256),
                (byte)random.Next(256),
                (byte)random.Next(256),
                255
            );
        }

        private static Texture2D Create(Func<int, int, Color32> pixel)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                    pixels[y * Size + x] = pixel(x, y);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }
    }
}
