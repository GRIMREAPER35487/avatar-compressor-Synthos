using System.Collections.Generic;
using System.IO;
using System.Linq;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor.texture;
using dev.limitex.avatar.compressor.editor.texture.ui;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace dev.limitex.avatar.compressor.tests
{
    /// <summary>
    /// With Poiyomi installed: its lock runs between the two build steps, so textures it strips
    /// (features turned off) are never compressed. Skipped without Poiyomi.
    /// </summary>
    [TestFixture]
    internal sealed class PoiyomiLockOrderTests
    {
        private const string TestFolder = "Assets/_LAC_POI_TMP";
        private const string PoiyomiShader = ".poiyomi/Poiyomi Pro";

        private readonly List<Object> _sceneObjects = new List<Object>();
        private HashSet<string> _buildFoldersBefore;
        private Material _material;
        private GameObject _avatar;

        [SetUp]
        public void SetUp()
        {
            if (Shader.Find(PoiyomiShader) == null || !PoiyomiLockState.CanLock)
                Assert.Ignore("Poiyomi is not installed");

            _buildFoldersBefore = AssetDatabase.IsValidFolder(BuildAssetStore.RootFolder)
                ? new HashSet<string>(AssetDatabase.GetSubFolders(BuildAssetStore.RootFolder))
                : new HashSet<string>();
            AssetDatabase.CreateFolder("Assets", "_LAC_POI_TMP");

            _material = new Material(Shader.Find(PoiyomiShader));
            _material.SetTexture("_MainTex", CreateTextureAsset("PoiMain", 1));
            _material.SetTexture("_ConstellationMask", CreateTextureAsset("PoiUnusedMask", 2));
            _material.SetFloat("_ConstellationEnable", 0f);
            AssetDatabase.CreateAsset(_material, $"{TestFolder}/PoiMat.mat");

            _avatar = new GameObject("PoiTestAvatar");
            _sceneObjects.Add(_avatar);
            _avatar.AddComponent<VRCAvatarDescriptor>();
            var body = new GameObject("Body");
            body.transform.SetParent(_avatar.transform);
            body.AddComponent<MeshFilter>();
            body.AddComponent<MeshRenderer>().sharedMaterial = _material;

            var config = _avatar.AddComponent<TextureCompressor>();
            config.ApplyPreset(CompressorPreset.Maximum);
            config.MinSourceSize = 0;
            config.SkipIfSmallerThan = 0;
            config.ExcludedPaths.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _sceneObjects)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }
            _sceneObjects.Clear();
            if (_buildFoldersBefore == null)
                return;
            foreach (var folder in AssetDatabase.GetSubFolders(BuildAssetStore.RootFolder).Except(_buildFoldersBefore))
                AssetDatabase.DeleteAsset(folder);
            AssetDatabase.DeleteAsset(TestFolder);
        }

        [Test]
        public void UnlockedMaterial_IsFound()
        {
            Assert.That(PoiyomiLockState.FindUnlocked(_avatar), Is.EqualTo(new[] { _material }));
        }

        [Test]
        public void LockBetweenSteps_StrippedTextureIsNotCompressed()
        {
            var copy = Object.Instantiate(_avatar);
            _sceneObjects.Add(copy);

            TextureCompressorBuildHook.CaptureSettings(copy);
            // What Poiyomi's upload step (order 100) does between the two steps
            Assert.That(PoiyomiLockState.Lock(new[] { _material }), Is.True);
            Assert.That(_material.GetFloat("_ShaderOptimizerEnabled"), Is.EqualTo(1f));
            TextureCompressorBuildHook.CompressCaptured(copy);

            var built = copy.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Assert.That(built.mainTexture.name, Does.StartWith("PoiMain"));
            Assert.That(
                AssetDatabase.GetAssetPath(built.mainTexture),
                Does.StartWith(BuildAssetStore.RootFolder),
                "The used texture is compressed."
            );
            var generated = AssetDatabase
                .FindAssets("PoiUnusedMask", new[] { BuildAssetStore.RootFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToList();
            Assert.That(generated, Is.Empty, "The stripped texture is not compressed.");
            Assert.That(PoiyomiLockState.FindUnlocked(_avatar), Is.Empty);
        }

        private static Texture2D CreateTextureAsset(string name, int seed)
        {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var random = new System.Random(seed);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            texture.SetPixels32(pixels);
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            string path = $"{TestFolder}/{name}.png";
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
