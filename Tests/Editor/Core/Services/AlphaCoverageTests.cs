using System.Collections.Generic;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using Synthos.TextureAnalysis;
using UnityEngine;
using UnityEngine.Rendering;

namespace dev.limitex.avatar.compressor.tests
{
    [TestFixture]
    internal sealed class AlphaCoverageTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _created)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }
            _created.Clear();
        }

        [TestCase((int)RenderQueue.Geometry, "_MainTex", false)]
        [TestCase((int)RenderQueue.AlphaTest, "_MainTex", true)]
        [TestCase((int)RenderQueue.Transparent, "_MainTex", true)]
        [TestCase((int)RenderQueue.Transparent, "_BaseMap", true)]
        [TestCase((int)RenderQueue.Transparent, "_MetallicGlossMap", false)]
        [TestCase((int)RenderQueue.Transparent, "_BumpMap", false)]
        public void IsCoverage_OnlyMainTextureOfCutoutOrTransparentMaterial(
            int renderQueue,
            string property,
            bool expected
        )
        {
            var material = CreateMaterial(renderQueue);

            Assert.That(AlphaCoverage.IsCoverage(material, property), Is.EqualTo(expected));
        }

        [Test]
        public void IsCoverage_NullMaterial_False()
        {
            Assert.That(AlphaCoverage.IsCoverage(null, "_MainTex"), Is.False);
        }

        [Test]
        public void ToAnalysisInput_SeeThroughWhenAnyMaterialShowsAlpha()
        {
            var texture = Track(new Texture2D(4, 4));
            var info = new TextureInfo();
            info.References.Add(
                new MaterialTextureReference
                {
                    Material = CreateMaterial((int)RenderQueue.Geometry),
                    PropertyName = "_MainTex",
                }
            );
            var textures = new Dictionary<Texture2D, TextureInfo> { { texture, info } };

            Assert.That(textures.ToAnalysisInput()[texture].AlphaIsTransparency, Is.False);

            info.References.Add(
                new MaterialTextureReference
                {
                    Material = CreateMaterial((int)RenderQueue.AlphaTest),
                    PropertyName = "_MainTex",
                }
            );

            Assert.That(textures.ToAnalysisInput()[texture].AlphaIsTransparency, Is.True);
        }

        [Test]
        public void AnalyzeBatch_AlphaIsData_ScoresDetailUnderZeroAlpha()
        {
            // The detail sits where alpha is 0: hidden on a cutout material, but visible when
            // alpha holds data such as smoothness.
            var texture = Track(CreateHiddenNoiseTexture(64, 64));
            var processor = new TextureProcessor(32, 2048, true, ResizeBackendPreference.CPU);
            var backend = new CpuAnalysisBackend(
                AnalyzerFactory.Create(AnalysisStrategyType.Combined),
                AnalyzerFactory.CreateNormalMapAnalyzer(),
                processor
            );

            float hidden = backend.AnalyzeBatch(
                new Dictionary<Texture2D, TextureAnalysisInput>
                {
                    { texture, new TextureAnalysisInput(false, alphaIsTransparency: true) },
                }
            )[texture];
            float visible = backend.AnalyzeBatch(
                new Dictionary<Texture2D, TextureAnalysisInput>
                {
                    { texture, new TextureAnalysisInput(false, alphaIsTransparency: false) },
                }
            )[texture];

            Assert.That(visible, Is.GreaterThan(hidden + 0.1f));
        }

        /// <summary>
        /// Left half: noise with alpha 0. Right half: flat grey with alpha 1.
        /// </summary>
        internal static Texture2D CreateHiddenNoiseTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var pixels = new Color[width * height];
            var random = new System.Random(7);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[y * width + x] =
                        x < width / 2
                            ? new Color(
                                (float)random.NextDouble(),
                                (float)random.NextDouble(),
                                (float)random.NextDouble(),
                                0f
                            )
                            : new Color(0.5f, 0.5f, 0.5f, 1f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Material CreateMaterial(int renderQueue)
        {
            var material = Track(new Material(Shader.Find("Standard")));
            material.renderQueue = renderQueue;
            return material;
        }

        private T Track<T>(T obj)
            where T : Object
        {
            _created.Add(obj);
            return obj;
        }
    }
}
