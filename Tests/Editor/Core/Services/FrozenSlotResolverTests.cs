using System.Collections.Generic;
using System.IO;
using System.Linq;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor.texture;
using NUnit.Framework;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace dev.limitex.avatar.compressor.tests
{
    /// <summary>
    /// "Apply to swaps in this slot": a frozen texture's settings follow its mesh, material slot
    /// and texture property to whatever texture is there during the build.
    /// </summary>
    [TestFixture]
    internal sealed class FrozenSlotResolverTests
    {
        private const string TestFolder = "Assets/_LAC_SLOT_TMP";
        private const int TextureSize = 256;

        private readonly List<Object> _sceneObjects = new List<Object>();
        private HashSet<string> _buildFoldersBefore;

        private Texture2D _texA;
        private Texture2D _texB;
        private Texture2D _texC;
        private Material _matA;
        private Material _matB;
        private Material _matC;
        private Mesh _bodyMesh;
        private GameObject _avatar;
        private MeshRenderer _body;
        private TextureCompressor _config;

        [SetUp]
        public void SetUp()
        {
            _buildFoldersBefore = GetBuildFolders();
            AssetDatabase.CreateFolder("Assets", "_LAC_SLOT_TMP");

            _texA = CreateTextureAsset("TexA", 1);
            _texB = CreateTextureAsset("TexB", 2);
            _texC = CreateTextureAsset("TexC", 3);
            _matA = CreateMaterialAsset("MatA", _texA);
            _matB = CreateMaterialAsset("MatB", _texB);
            _matC = CreateMaterialAsset("MatC", _texC);
            _bodyMesh = CreateMeshAsset("BodyMesh", "BodyMesh");

            _avatar = new GameObject("SlotTestAvatar");
            _sceneObjects.Add(_avatar);
            _avatar.AddComponent<VRCAvatarDescriptor>();
            _body = AddRenderer("Body", _bodyMesh, _matA);

            _config = _avatar.AddComponent<TextureCompressor>();
            _config.ApplyPreset(CompressorPreset.Maximum);
            _config.TargetPlatform = CompressionPlatform.Desktop;
            _config.MinSourceSize = 0;
            _config.SkipIfSmallerThan = 0;
            _config.ExcludedPaths.Clear();
            _config.LimitByOnAvatarSize = false;
            _config.AvoidCompressionArtifacts = false;
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
        public void FindSlots_RecordsMeshSlotAndProperty()
        {
            var slots = FrozenSlotResolver.FindSlots(Guid(_texA), _avatar);

            Assert.That(slots, Has.Count.EqualTo(1));
            Assert.That(slots[0].MeshName, Is.EqualTo("BodyMesh"));
            Assert.That(slots[0].MaterialIndex, Is.EqualTo(0));
            Assert.That(slots[0].PropertyName, Is.EqualTo("_MainTex"));
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(_bodyMesh, out string meshGuid, out long _);
            Assert.That(slots[0].MeshGuid, Is.EqualTo(meshGuid));
        }

        [Test]
        public void Expand_SwappedMaterialInSameSlot_GetsSettings()
        {
            var frozen = FreezeA(apply: true);
            _body.sharedMaterial = _matB;
            var followedBy = new Dictionary<string, string>();

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar, followedBy);

            var forB = expanded.Single(f => f.TextureGuid == Guid(_texB));
            Assert.That(forB.Divisor, Is.EqualTo(4));
            Assert.That(forB.Format, Is.EqualTo(FrozenTextureFormat.BC7));
            Assert.That(followedBy[Guid(_texB)], Is.EqualTo(Guid(_texA)));
        }

        [Test]
        public void Expand_OptionOff_AddsNothing()
        {
            var frozen = FreezeA(apply: false);
            _body.sharedMaterial = _matB;

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded, Has.Count.EqualTo(1));
        }

        [Test]
        public void Expand_SwappedTextureWithOwnFrozenSettings_KeepsThem()
        {
            var frozenA = FreezeA(apply: true);
            var frozenB = new FrozenTextureSettings(Guid(_texB), 1, FrozenTextureFormat.DXT1, false);
            _body.sharedMaterial = _matB;

            var expanded = FrozenSlotResolver.Expand(new[] { frozenA, frozenB }, _avatar);

            var forB = expanded.Where(f => f.TextureGuid == Guid(_texB)).ToList();
            Assert.That(forB, Has.Count.EqualTo(1));
            Assert.That(forB[0].Divisor, Is.EqualTo(1));
            Assert.That(forB[0].Format, Is.EqualTo(FrozenTextureFormat.DXT1));
        }

        [Test]
        public void Expand_OtherMaterialSlot_NotMatched()
        {
            var frozen = FreezeA(apply: true);
            _body.sharedMaterials = new[] { _matA, _matB };

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded.Any(f => f.TextureGuid == Guid(_texB)), Is.False);
        }

        [Test]
        public void Expand_OtherTextureProperty_NotMatched()
        {
            var frozen = FreezeA(apply: true);
            var emissionOnly = new Material(Shader.Find("Standard"));
            _sceneObjects.Add(emissionOnly);
            emissionOnly.SetTexture("_EmissionMap", _texB);
            _body.sharedMaterial = emissionOnly;

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded.Any(f => f.TextureGuid == Guid(_texB)), Is.False);
        }

        [Test]
        public void Expand_OtherMeshAssetWithSameName_NotMatched()
        {
            var frozen = FreezeA(apply: true);
            var lookalike = CreateMeshAsset("BodyMeshCopy", "BodyMesh");
            _body.GetComponent<MeshFilter>().sharedMesh = lookalike;
            _body.sharedMaterial = _matB;

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded.Any(f => f.TextureGuid == Guid(_texB)), Is.False);
        }

        [Test]
        public void Expand_BuildCopyOfMesh_MatchedByName()
        {
            var frozen = FreezeA(apply: true);
            // A build step's copy (e.g. VRCFury removing blendshapes): no asset, same name
            var copy = Object.Instantiate(_bodyMesh);
            _sceneObjects.Add(copy);
            _body.GetComponent<MeshFilter>().sharedMesh = copy;
            _body.sharedMaterial = _matB;

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded.Any(f => f.TextureGuid == Guid(_texB)), Is.True);
        }

        [Test]
        public void Expand_MaterialSwapAnimation_Matched()
        {
            var frozen = FreezeA(apply: true);
            var clip = new AnimationClip();
            AnimationUtility.SetObjectReferenceCurve(
                clip,
                EditorCurveBinding.PPtrCurve("Body", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0f, value = _matC } }
            );
            AssetDatabase.CreateAsset(clip, $"{TestFolder}/Swap.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath($"{TestFolder}/FX.controller");
            controller.layers[0].stateMachine.AddState("Swap").motion = clip;
            var descriptor = _avatar.GetComponent<VRCAvatarDescriptor>();
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer
                {
                    type = VRCAvatarDescriptor.AnimLayerType.FX,
                    isEnabled = true,
                    animatorController = controller,
                },
            };

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded.Any(f => f.TextureGuid == Guid(_texC)), Is.True);
        }

        [Test]
        public void Expand_SameMeshOnAnotherObject_Matched()
        {
            var frozen = FreezeA(apply: true);
            AddRenderer("Body2", _bodyMesh, _matC);

            var expanded = FrozenSlotResolver.Expand(new[] { frozen }, _avatar);

            Assert.That(expanded.Any(f => f.TextureGuid == Guid(_texC)), Is.True);
        }

        [Test]
        public void Build_SwappedTexture_UsesFrozenSizeAndFormat()
        {
            _config.FrozenTextures.Add(FreezeA(apply: true));
            _body.sharedMaterial = _matB;

            var copy = Object.Instantiate(_avatar);
            _sceneObjects.Add(copy);
            TextureCompressorBuildHook.Process(copy);

            var result = (Texture2D)copy.transform.Find("Body").GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(result, Is.Not.SameAs(_texB));
            Assert.That(result.width, Is.EqualTo(TextureSize / 4));
            Assert.That(result.format, Is.EqualTo(TextureFormat.BC7));
        }

        [Test]
        public void Build_FrozenSkip_LeavesSwappedTextureAsImported()
        {
            var frozen = FreezeA(apply: true);
            frozen.Skip = true;
            _config.FrozenTextures.Add(frozen);
            _body.sharedMaterial = _matB;

            var copy = Object.Instantiate(_avatar);
            _sceneObjects.Add(copy);
            TextureCompressorBuildHook.Process(copy);

            var result = copy.transform.Find("Body").GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(result, Is.SameAs(_texB));
        }

        [Test]
        public void Build_FromSourceFile_SwappedTextureUsesFrozenDivisor()
        {
            _config.RespectImportMaxSize = false;
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(_texB));
            importer.maxTextureSize = 64;
            importer.SaveAndReimport();
            _config.FrozenTextures.Add(FreezeA(apply: true));
            _body.sharedMaterial = _matB;

            var copy = Object.Instantiate(_avatar);
            _sceneObjects.Add(copy);
            TextureCompressorBuildHook.Process(copy);

            // Divisor 4 of the 256 source file, not of the 64 import
            var result = (Texture2D)copy.transform.Find("Body").GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(result.width, Is.EqualTo(TextureSize / 4));
        }

        private FrozenTextureSettings FreezeA(bool apply)
        {
            var frozen = new FrozenTextureSettings(Guid(_texA), 4, FrozenTextureFormat.BC7, false)
            {
                ApplyToSlotSwaps = apply,
            };
            if (apply)
                frozen.Slots = FrozenSlotResolver.FindSlots(frozen.TextureGuid, _avatar);
            return frozen;
        }

        private MeshRenderer AddRenderer(string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_avatar.transform);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        private static string Guid(Object asset)
        {
            return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
        }

        private static HashSet<string> GetBuildFolders()
        {
            return AssetDatabase.IsValidFolder(BuildAssetStore.RootFolder)
                ? new HashSet<string>(AssetDatabase.GetSubFolders(BuildAssetStore.RootFolder))
                : new HashSet<string>();
        }

        private static Mesh CreateMeshAsset(string file, string name)
        {
            var mesh = new Mesh
            {
                name = name,
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one },
                triangles = new[] { 0, 2, 1, 1, 2, 3 },
            };
            AssetDatabase.CreateAsset(mesh, $"{TestFolder}/{file}.asset");
            mesh.name = name;
            return mesh;
        }

        private static Material CreateMaterialAsset(string name, Texture2D texture)
        {
            var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
            AssetDatabase.CreateAsset(material, $"{TestFolder}/{name}.mat");
            return material;
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
    }
}
