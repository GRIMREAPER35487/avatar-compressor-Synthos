using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace dev.limitex.avatar.compressor.tests
{
    /// <summary>
    /// End-to-end tests for the VRChat SDK build hook: a copy of an avatar with a material
    /// swap animation is processed, and the project assets it came from must be unchanged.
    /// </summary>
    [TestFixture]
    internal sealed class TextureCompressorBuildHookTests
    {
        private const string TestFolder = "Assets/_LAC_HOOK_TMP";
        private const int TextureSize = 256;

        private readonly List<Object> _sceneObjects = new List<Object>();
        private HashSet<string> _buildFoldersBefore;

        private Texture2D _textureA;
        private Material _materialA;
        private Material _materialB;
        private AnimationClip _clip;
        private AnimatorController _controller;
        private GameObject _avatar;

        [SetUp]
        public void SetUp()
        {
            _buildFoldersBefore = GetBuildFolders();
            AssetDatabase.CreateFolder("Assets", "_LAC_HOOK_TMP");

            _textureA = CreateTextureAsset("TexA", 1);
            var textureB = CreateTextureAsset("TexB", 2);
            _materialA = CreateMaterialAsset("MatA", _textureA);
            _materialB = CreateMaterialAsset("MatB", textureB);

            _clip = new AnimationClip();
            AnimationUtility.SetObjectReferenceCurve(
                _clip,
                EditorCurveBinding.PPtrCurve(
                    "Body",
                    typeof(MeshRenderer),
                    "m_Materials.Array.data[0]"
                ),
                new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = _materialB },
                }
            );
            AssetDatabase.CreateAsset(_clip, $"{TestFolder}/Swap.anim");

            _controller = AnimatorController.CreateAnimatorControllerAtPath(
                $"{TestFolder}/FX.controller"
            );
            _controller.layers[0].stateMachine.AddState("Swap").motion = _clip;
            AssetDatabase.SaveAssets();

            _avatar = new GameObject("HookTestAvatar");
            _sceneObjects.Add(_avatar);
            var descriptor = _avatar.AddComponent<VRCAvatarDescriptor>();
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = VRCAvatarDescriptor.AnimLayerType.FX,
                    isDefault = false,
                    isEnabled = true,
                    animatorController = _controller,
                },
            };

            var body = new GameObject("Body");
            body.transform.SetParent(_avatar.transform);
            body.AddComponent<MeshFilter>();
            body.AddComponent<MeshRenderer>().sharedMaterial = _materialA;

            var config = _avatar.AddComponent<TextureCompressor>();
            config.ApplyPreset(CompressorPreset.Maximum);
            config.MinSourceSize = 0;
            config.SkipIfSmallerThan = 0;
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

            foreach (var folder in GetBuildFolders().Except(_buildFoldersBefore))
                AssetDatabase.DeleteAsset(folder);
            if (
                AssetDatabase.IsValidFolder(BuildAssetStore.RootFolder)
                && AssetDatabase.GetSubFolders(BuildAssetStore.RootFolder).Length == 0
            )
            {
                AssetDatabase.DeleteAsset(BuildAssetStore.RootFolder);
            }

            AssetDatabase.DeleteAsset(TestFolder);
        }

        [Test]
        public void Process_LeavesProjectAssetsByteForByteUnchanged()
        {
            var before = HashFolder(TestFolder);

            var copy = ProcessCopy();
            AssetDatabase.SaveAssets();

            Assert.That(HashFolder(TestFolder), Is.EqualTo(before));
            Assert.That(
                AssetDatabase.GetAssetPath(
                    _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterial
                ),
                Is.EqualTo($"{TestFolder}/MatA.mat"),
                "The scene avatar must not be modified."
            );
            Assert.That(copy.GetComponent<TextureCompressor>(), Is.Null);
        }

        [Test]
        public void Process_ReplacesMaterialWithSavedCompressedCopy()
        {
            var copy = ProcessCopy();

            var material = copy.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Assert.That(material, Is.Not.SameAs(_materialA));
            AssertInBuildFolder(material);

            var texture = (Texture2D)material.mainTexture;
            Assert.That(texture, Is.Not.SameAs(_textureA));
            AssertInBuildFolder(texture);
            Assert.That(
                texture.width,
                Is.LessThan(TextureSize),
                "Maximum preset should shrink it."
            );
        }

        [Test]
        public void Process_SavesCompressedTextureWithPixelsAndWithoutReadWrite()
        {
            var copy = ProcessCopy();
            AssetDatabase.SaveAssets();

            var texture = (Texture2D)
                copy.GetComponentInChildren<MeshRenderer>().sharedMaterial.mainTexture;
            string yaml = File.ReadAllText(AssetDatabase.GetAssetPath(texture));

            StringAssert.Contains("m_IsReadable: 0", yaml);
            var imageData = Regex.Match(yaml, @"image data: (\d+)");
            Assert.That(imageData.Success, Is.True, "Texture asset has no image data entry.");
            Assert.That(int.Parse(imageData.Groups[1].Value), Is.GreaterThan(0));
        }

        [Test]
        public void Process_PointsMaterialSwapAnimationAtCopies()
        {
            var copy = ProcessCopy();

            var descriptor = copy.GetComponent<VRCAvatarDescriptor>();
            var controller = (AnimatorController)
                descriptor.baseAnimationLayers[0].animatorController;
            Assert.That(controller, Is.Not.SameAs(_controller));
            AssertInBuildFolder(controller);

            var clip = (AnimationClip)controller.layers[0].stateMachine.states[0].state.motion;
            Assert.That(clip, Is.Not.SameAs(_clip));
            AssertInBuildFolder(clip);

            var binding = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
            var swapped = AnimationUtility.GetObjectReferenceCurve(clip, binding)[0].value;
            Assert.That(swapped, Is.InstanceOf<Material>());
            Assert.That(swapped, Is.Not.SameAs(_materialB));
            AssertInBuildFolder(swapped);

            // The original clip still points at the original material.
            var originalClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                $"{TestFolder}/Swap.anim"
            );
            var originalBinding = AnimationUtility
                .GetObjectReferenceCurveBindings(originalClip)
                .Single();
            Assert.That(
                AssetDatabase.GetAssetPath(
                    AnimationUtility.GetObjectReferenceCurve(originalClip, originalBinding)[0].value
                ),
                Is.EqualTo($"{TestFolder}/MatB.mat")
            );
        }

        private GameObject ProcessCopy()
        {
            var copy = Object.Instantiate(_avatar);
            _sceneObjects.Add(copy);
            TextureCompressorBuildHook.Process(copy);
            return copy;
        }

        private static void AssertInBuildFolder(Object obj)
        {
            StringAssert.StartsWith(
                BuildAssetStore.RootFolder + "/",
                AssetDatabase.GetAssetPath(obj),
                $"{obj.name} should be saved in the build folder."
            );
        }

        private static Texture2D CreateTextureAsset(string name, int seed)
        {
            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
            var random = new System.Random(seed);
            var pixels = new Color32[TextureSize * TextureSize];
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
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            string path = $"{TestFolder}/{name}.png";
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material CreateMaterialAsset(string name, Texture2D texture)
        {
            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            AssetDatabase.CreateAsset(material, $"{TestFolder}/{name}.mat");
            return material;
        }

        private static Dictionary<string, string> HashFolder(string folder)
        {
            using var sha = SHA256.Create();
            return Directory
                .GetFiles(folder, "*", SearchOption.AllDirectories)
                .OrderBy(path => path)
                .ToDictionary(
                    path => path.Replace('\\', '/'),
                    path => System.Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)))
                );
        }

        private static HashSet<string> GetBuildFolders()
        {
            return AssetDatabase.IsValidFolder(BuildAssetStore.RootFolder)
                ? new HashSet<string>(AssetDatabase.GetSubFolders(BuildAssetStore.RootFolder))
                : new HashSet<string>();
        }
    }
}
