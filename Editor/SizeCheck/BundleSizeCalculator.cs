using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build.Content;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace dev.limitex.avatar.compressor.editor.size
{
// Calculates the size an avatar's asset bundle would have, without building it.
// Uncompressed = what the VRC SDK reports (sum of packed sizes + file overhead).
// Download = the LZMA bundle, compressed by Unity's own archiver from the emitted bytes.
// Exact for Unity 2022.3.22f1 (the type table); see AvatarSizeCheck for the supported range.
internal static class BundleSizeCalculator
{
    public sealed class Result
    {
        public long Uncompressed;
        public long Overhead;
        public int ObjectCount;
        public readonly Dictionary<string, long> ByType = new Dictionary<string, long>();
        public readonly List<(Object obj, string type, long size)> Objects = new List<(Object, string, long)>();

        // download-size inputs (only filled when emitting)
        public byte[] SerializedStream, TextureStream;
        public long ClipBytes, ControllerBytes, CompressedMeshBytes, MetadataBytes;
        public readonly Dictionary<Object, byte[]> Emitted = new Dictionary<Object, byte[]>();
        public readonly Dictionary<Object, long> PathIds = new Dictionary<Object, long>();
    }

    // ---------------- type trees ----------------

    sealed class Node
    {
        public int Level, ByteSize, Meta, Version = 1;
        public bool ManagedArray;
        public string Type, Name;
        public readonly List<Node> Children = new List<Node>();
        public bool Align => (Meta & 0x4000) != 0;
    }

    // typetrees_2022_3.txt: Unity 2022.3 player type trees (from UnityPy's Tpk), next to this file
    const string TypeTreeGuid = "66878d99b62d4624b2879066c79b7d69";

    static Dictionary<string, (int id, Node root)> _natives;
    static HashSet<string> _common;
    static Dictionary<string, uint> _commonOffsets;
    static readonly Dictionary<Node, long> _treeCost = new Dictionary<Node, long>();

    static void LoadTypeTrees()
    {
        if (_natives != null)
            return;
        _natives = new Dictionary<string, (int, Node)>();
        string path = AssetDatabase.GUIDToAssetPath(TypeTreeGuid);
        var stack = new List<Node>();
        int classId = 0;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('\t');
            if (parts[0] == "#common")
            {
                // "offset:string" pairs: Unity's shared string buffer for type trees
                _commonOffsets = new Dictionary<string, uint>();
                foreach (var e in parts.Skip(1))
                {
                    int c = e.IndexOf(':');
                    _commonOffsets[e.Substring(c + 1)] = uint.Parse(e.Substring(0, c));
                }
                _common = new HashSet<string>(_commonOffsets.Keys);
                continue;
            }
            if (parts[0] == "#class")
            {
                classId = int.Parse(parts[1]);
                stack.Clear();
                continue;
            }
            var n = new Node
            {
                Level = int.Parse(parts[0]), Type = parts[1], Name = parts[2],
                ByteSize = int.Parse(parts[3]), Meta = int.Parse(parts[4]),
                Version = parts.Length > 5 ? int.Parse(parts[5]) : 1,
            };
            while (stack.Count > n.Level)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0)
                stack[stack.Count - 1].Children.Add(n);
            else if (!_natives.ContainsKey(n.Type))
                _natives[n.Type] = (classId, n);
            stack.Add(n);
        }
    }

    static long TreeCost(Node root)
    {
        if (_treeCost.TryGetValue(root, out long c))
            return c;
        int nodes = 0;
        var strings = new HashSet<string>();
        void Walk(Node n)
        {
            nodes++;
            if (!_common.Contains(n.Type)) strings.Add(n.Type);
            if (!_common.Contains(n.Name)) strings.Add(n.Name);
            foreach (var ch in n.Children) Walk(ch);
        }
        Walk(root);
        c = 32L * nodes + strings.Sum(s => Encoding.UTF8.GetByteCount(s) + 1);
        _treeCost[root] = c;
        return c;
    }

    // ---- script type trees (Unity serialization rules, approximated) ----

    static readonly Dictionary<Type, Node> _scriptTrees = new Dictionary<Type, Node>();

    static Node Leaf(string type, string name, int size, int meta = 0) =>
        new Node { Type = type, Name = name, ByteSize = size, Meta = meta };

    static Node Struct(string type, string name, params Node[] children)
    {
        var n = new Node { Type = type, Name = name, ByteSize = -1 };
        n.Children.AddRange(children);
        n.ByteSize = children.All(c => c.ByteSize >= 0) ? children.Sum(c => c.ByteSize) : -1;
        return n;
    }

    // Meta flag bits Unity writes into script type trees
    const int HideInEditor = 0x1, TreatAsBool = 0x100, AlignBytes = 0x4000, AnyChildAligns = 0x8000,
        FlowMappingStyle = 0x200000, StrongPPtr = 0x800000;

    static Node StringNode(string name)
    {
        var arr = Struct("Array", "Array", Leaf("int", "size", 4, HideInEditor), Leaf("char", "data", 1, HideInEditor));
        arr.Meta = AlignBytes | HideInEditor; arr.ByteSize = -1;
        var n = Struct("string", name, arr);
        n.ByteSize = -1;
        return n;
    }

    static Node PPtr(string target, string name) =>
        Struct($"PPtr<{target}>", name, Leaf("int", "m_FileID", 4, StrongPPtr | HideInEditor), Leaf("SInt64", "m_PathID", 8, StrongPPtr | HideInEditor));

    static Node Vector(string name, Node elem, bool alignArray = true)
    {
        var arr = Struct("Array", "Array", Leaf("int", "size", 4), elem);
        arr.Meta = alignArray ? AlignBytes : 0; arr.ByteSize = -1;
        var n = Struct("vector", name, arr);
        n.ByteSize = -1;
        return n;
    }

    static Node Floats(string type, string name, int meta, params string[] f)
    {
        var n = Struct(type, name, f.Select(x => Leaf("float", x, 4, meta)).ToArray());
        n.Meta = meta;
        return n;
    }

    static bool IsManagedClass(Type t) =>
        (t.IsClass || (t.IsValueType && !t.IsPrimitive && !t.IsEnum)) && t.IsSerializable && !t.IsAbstract
        && t.Namespace?.StartsWith("System") != true && !typeof(Object).IsAssignableFrom(t) && !typeof(Delegate).IsAssignableFrom(t)
        && t.Namespace?.StartsWith("UnityEngine") != true;

    static Node FieldNode(Type t, string name, int depth)
    {
        if (depth > 10 || typeof(Delegate).IsAssignableFrom(t)) return null;
        if (t == typeof(bool)) return Leaf("UInt8", name, 1, AlignBytes | TreatAsBool);
        if (t == typeof(byte)) return Leaf("UInt8", name, 1, AlignBytes);
        if (t == typeof(sbyte)) return Leaf("SInt8", name, 1, AlignBytes);
        if (t == typeof(short)) return Leaf("SInt16", name, 2, AlignBytes);
        if (t == typeof(ushort) || t == typeof(char)) return Leaf("UInt16", name, 2, AlignBytes);
        if (t == typeof(int) || t.IsEnum) return Leaf("int", name, 4);
        if (t == typeof(uint)) return Leaf("unsigned int", name, 4);
        if (t == typeof(long)) return Leaf("SInt64", name, 8);
        if (t == typeof(ulong)) return Leaf("UInt64", name, 8);
        if (t == typeof(float)) return Leaf("float", name, 4);
        if (t == typeof(double)) return Leaf("double", name, 8);
        if (t == typeof(string)) return StringNode(name);
        if (typeof(Object).IsAssignableFrom(t)) return PPtr("$" + t.Name, name);
        if (t == typeof(Vector2)) return Floats("Vector2f", name, FlowMappingStyle, "x", "y");
        if (t == typeof(Vector3)) return Floats("Vector3f", name, FlowMappingStyle, "x", "y", "z");
        if (t == typeof(Vector4)) return Floats("Vector4f", name, FlowMappingStyle, "x", "y", "z", "w");
        if (t == typeof(Quaternion)) return Floats("Quaternionf", name, FlowMappingStyle, "x", "y", "z", "w");
        if (t == typeof(Color)) return Floats("ColorRGBA", name, FlowMappingStyle, "r", "g", "b", "a");
        if (t == typeof(Color32)) { var c = Struct("ColorRGBA", name, Leaf("unsigned int", "rgba", 4)); c.Meta = FlowMappingStyle; return c; }
        if (t == typeof(Rect)) return Floats("Rectf", name, 0, "x", "y", "width", "height");
        if (t == typeof(Bounds)) return Struct("AABB", name, Floats("Vector3f", "m_Center", FlowMappingStyle, "x", "y", "z"), Floats("Vector3f", "m_Extent", FlowMappingStyle, "x", "y", "z"));
        if (t == typeof(Matrix4x4)) return Floats("Matrix4x4f", name, 0, Enumerable.Range(0, 16).Select(i => $"e{i % 4}{i / 4}").ToArray());
        if (t == typeof(LayerMask)) { var b = Struct("BitField", name, Leaf("unsigned int", "m_Bits", 4, 0x400000 | HideInEditor)); b.Version = 2; return b; }
        if (t == typeof(Vector2Int)) return Struct("int2_storage", name, Leaf("int", "x", 4), Leaf("int", "y", 4));
        if (t == typeof(Vector3Int)) return Struct("int3_storage", name, Leaf("int", "x", 4), Leaf("int", "y", 4), Leaf("int", "z", 4));
        if (t == typeof(AnimationCurve))
        {
            var key = Struct("Keyframe", "data", Leaf("float", "time", 4, 1), Leaf("float", "value", 4, 1), Leaf("float", "inSlope", 4, 1),
                Leaf("float", "outSlope", 4, 1), Leaf("int", "weightedMode", 4, 1), Leaf("float", "inWeight", 4, 1), Leaf("float", "outWeight", 4, 1));
            key.Meta = HideInEditor; key.Version = 3;
            var curve = Vector("m_Curve", key);
            curve.Meta = HideInEditor; curve.Children[0].Meta = AlignBytes | HideInEditor; curve.Children[0].Children[0].Meta = HideInEditor;
            var ac = Struct("AnimationCurve", name, curve, Leaf("int", "m_PreInfinity", 4, 1), Leaf("int", "m_PostInfinity", 4, 1), Leaf("int", "m_RotationOrder", 4, 1));
            ac.Version = 2;
            return ac;
        }
        if (t == typeof(Gradient))
        {
            var kids = new List<Node>();
            for (int i = 0; i < 8; i++) kids.Add(Floats("ColorRGBA", "key" + i, FlowMappingStyle, "r", "g", "b", "a"));
            for (int i = 0; i < 8; i++) kids.Add(Leaf("UInt16", "ctime" + i, 2));
            for (int i = 0; i < 8; i++) kids.Add(Leaf("UInt16", "atime" + i, 2));
            kids.Add(Leaf("int", "m_Mode", 4)); kids.Add(Leaf("SInt8", "m_ColorSpace", 1));
            kids.Add(Leaf("UInt8", "m_NumColorKeys", 1)); kids.Add(Leaf("UInt8", "m_NumAlphaKeys", 1, AlignBytes));
            var g = Struct("Gradient", name, kids.ToArray());
            g.Version = 2;
            return g;
        }
        Type elem = t.IsArray ? t.GetElementType()
            : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;
        if (elem != null)
        {
            if (elem.IsArray || (elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(List<>))) return null;
            var en = FieldNode(elem, "data", depth + 1);
            if (en == null) return null;
            if (IsManagedClass(elem))
            {
                // arrays of serializable classes are labelled with the element type and carry no alignment of their own
                var arr = Struct("Array", "Array", Leaf("int", "size", 4), en);
                arr.ByteSize = -1;
                var n = Struct(en.Type, name, arr);
                n.ByteSize = -1;
                n.ManagedArray = true;
                return n;
            }
            return Vector(name, en, alignArray: !en.Type.StartsWith("PPtr<"));
        }
        if (IsManagedClass(t))
        {
            var kids = SerializedFields(t).Select(f => FieldWithAttributes(f, depth + 1)).Where(n => n != null).ToArray();
            return Struct(t.Name, name, kids);
        }
        return null;
    }

    static Node FieldWithAttributes(FieldInfo f, int depth)
    {
        var n = FieldNode(f.FieldType, f.Name, depth);
        if (n != null && f.GetCustomAttribute<HideInInspector>() != null) Hide(n);
        return n;
    }

    static void Hide(Node n)
    {
        n.Meta |= HideInEditor;
        foreach (var c in n.Children) Hide(c);
    }

    // a compound node whose subtree aligns gets AnyChildAligns; managed arrays copy it onto their Array node
    static void PropagateAlign(Node n)
    {
        foreach (var c in n.Children) PropagateAlign(c);
        if (n.Children.Count > 0 && n.Children.Any(c => (c.Meta & (AlignBytes | AnyChildAligns)) != 0))
            n.Meta |= AnyChildAligns;
        if (n.ManagedArray) n.Children[0].Meta = n.Meta;
    }

    static IEnumerable<FieldInfo> SerializedFields(Type t)
    {
        var chain = new List<Type>();
        for (var b = t; b != null && b != typeof(MonoBehaviour) && b != typeof(ScriptableObject) && b != typeof(StateMachineBehaviour) && b != typeof(object); b = b.BaseType)
            chain.Insert(0, b);
        foreach (var b in chain)
            foreach (var f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsNotSerialized || f.IsInitOnly || f.IsLiteral) continue;
                if (!f.IsPublic && f.GetCustomAttribute<SerializeField>() == null) continue;
                if (f.GetCustomAttribute<SerializeReference>() != null) continue;
                yield return f;
            }
    }

    static Node ScriptTree(Type t)
    {
        if (_scriptTrees.TryGetValue(t, out var n))
            return n;
        var go = PPtr("GameObject", "m_GameObject");
        go.Meta = 0x41; go.Children.ForEach(c => c.Meta = 0x41);
        var name = StringNode("m_Name");
        name.Meta = 0x88001; name.Children[0].Meta = 0x84001; name.Children[0].Children.ForEach(c => c.Meta = 0x80001);
        var kids = new List<Node> { go, Leaf("UInt8", "m_Enabled", 1, AlignBytes | TreatAsBool | HideInEditor), PPtr("MonoScript", "m_Script"), name };
        foreach (var f in SerializedFields(t))
        {
            var fn = FieldWithAttributes(f, 1);
            if (fn != null) kids.Add(fn);
        }
        n = Struct("MonoBehaviour", "Base", kids.ToArray());
        PropagateAlign(n);
        name.Meta = 0x88001;
        _scriptTrees[t] = n;
        return n;
    }

    // ---- simulator: walk a player type tree with the editor's serialized values ----

    static long _pos;
    static BinaryWriter _w; // when set, the simulator also writes the object's player bytes

    static void AlignPos()
    {
        long a = (_pos + 3) & ~3L;
        if (_w != null) for (long i = _pos; i < a; i++) _w.Write((byte)0);
        _pos = a;
    }

    static bool _stripShaderPrograms;

    // per-object context for fields whose editor values differ from (or are too slow to read for) the player data
    static Texture2D _curTex;
    static readonly HashSet<Mesh> _skinnedMeshes = new HashSet<Mesh>();
    static Mesh _curMesh;
    static Material _curMat;
    static string _cabName;
    static readonly Dictionary<Texture2D, long> _texOffsets = new Dictionary<Texture2D, long>();

    static void WriteBlob(byte[] b, bool align = true)
    {
        _w.Write(b.Length); _w.Write(b);
        _pos += 4 + b.Length;
        if (align) AlignPos();
    }

    static bool Override(Node n, SerializedProperty p)
    {
        if (_curTex != null)
        {
            if (n.Name == "image data")
            {
                WriteBlob(_curTex.isReadable ? _curTex.GetRawTextureData() : Array.Empty<byte>());
                return true;
            }
            if (n.Name == "m_StreamData")
            {
                bool streamed = !_curTex.isReadable;
                _w.Write(streamed && _texOffsets.TryGetValue(_curTex, out long off) ? off : 0L);
                _w.Write(streamed ? (uint)TexBytes(_curTex) : 0u);
                _pos += 12;
                WriteBlob(Encoding.UTF8.GetBytes(streamed ? $"archive:/{_cabName}/{_cabName}.resS" : ""));
                return true;
            }
        }
        if (_curMesh != null && MeshUtility.GetMeshCompression(_curMesh) != ModelImporterMeshCompression.Off)
        {
            if (n.Name == "m_IndexBuffer") { WriteBlob(Array.Empty<byte>()); return true; }
            if (n.Name == "m_VertexData") { _w.Write(0u); _w.Write(14); _w.Write(new byte[14 * 4]); _w.Write(0); _pos += 12 + 56; return true; }
            if (n.Name == "m_CompressedMesh") { WriteCompressedMesh(_curMesh); return true; }
        }
        if (_curMesh != null)
        {
            if (n.Name == "m_KeepVertices" || n.Name == "m_KeepIndices")
            {
                _w.Write((byte)0); _pos += 1;
                if (n.Align) AlignPos();
                return true;
            }
            if (n.Name == "m_MeshUsageFlags")
            {
                _w.Write(_skinnedMeshes.Contains(_curMesh) ? 1 : 0); _pos += 4;
                return true;
            }
            if (n.Name == "m_IndexBuffer")
            {
                using (var arr = Mesh.AcquireReadOnlyMeshData(_curMesh)) WriteBlob(arr[0].GetIndexData<byte>().ToArray());
                return true;
            }
            if (n.Name == "m_DataSize")
            {
                var vb = new MemoryStream();
                using (var arr = Mesh.AcquireReadOnlyMeshData(_curMesh))
                    for (int s = 0; s < arr[0].vertexBufferCount; s++)
                    {
                        while (s > 0 && vb.Length % 16 != 0) vb.WriteByte(0);
                        var bytes = arr[0].GetVertexData<byte>(s).ToArray();
                        vb.Write(bytes, 0, bytes.Length);
                    }
                WriteBlob(vb.ToArray());
                return true;
            }
            if (n.Name == "vertices" && n.Type == "vector")
            {
                var ms = new MemoryStream();
                var bw = new BinaryWriter(ms);
                int vc = _curMesh.vertexCount, count = 0;
                var dv = new Vector3[vc]; var dn = new Vector3[vc]; var dt = new Vector3[vc];
                for (int b = 0; b < _curMesh.blendShapeCount; b++)
                    for (int f = 0; f < _curMesh.GetBlendShapeFrameCount(b); f++)
                    {
                        _curMesh.GetBlendShapeFrameVertices(b, f, dv, dn, dt);
                        for (int v = 0; v < vc; v++)
                        {
                            if (dv[v] == Vector3.zero && dn[v] == Vector3.zero && dt[v] == Vector3.zero) continue;
                            bw.Write(dv[v].x); bw.Write(dv[v].y); bw.Write(dv[v].z);
                            bw.Write(dn[v].x); bw.Write(dn[v].y); bw.Write(dn[v].z);
                            bw.Write(dt[v].x); bw.Write(dt[v].y); bw.Write(dt[v].z);
                            bw.Write((uint)v);
                            count++;
                        }
                    }
                bw.Flush();
                _w.Write(count); _w.Write(ms.ToArray());
                _pos += 4 + ms.Length;
                AlignPos();
                return true;
            }
        }
        if (_curMat != null && n.Name == "m_TexEnvs" && p != null && p.isArray)
        {
            // the build drops texture slots the shader doesn't declare
            var keep = new List<SerializedProperty>();
            for (int i = 0; i < p.arraySize; i++)
            {
                var e = p.GetArrayElementAtIndex(i);
                string name = e.FindPropertyRelative("first").stringValue;
                if (_curMat.shader != null && _curMat.shader.FindPropertyIndex(name) >= 0) keep.Add(e);
            }
            _w.Write(keep.Count); _pos += 4;
            var elem = n.Children[0].Children[1];
            foreach (var e in keep) Sim(elem, e);
            if (n.Children[0].Align) AlignPos();
            if (n.Align) AlignPos();
            return true;
        }
        return false;
    }

    // ---- compressed (quantised) meshes ----

    static byte[] PackBits(IList<uint> values, int bits)
    {
        var data = new byte[(values.Count * (long)bits + 7) / 8];
        long bitPos = 0;
        foreach (var v in values)
        {
            for (int b = 0; b < bits; b++, bitPos++)
                if (((v >> b) & 1) != 0) data[bitPos >> 3] |= (byte)(1 << (int)(bitPos & 7));
        }
        return data;
    }

    static void WritePacked(byte[] data, int bits)
    {
        _w.Write(data.Length); _w.Write(data); _pos += 4 + data.Length; AlignPos();
        _w.Write((byte)bits); _pos += 1; AlignPos();
    }

    static void PackedFloats(IList<float> values, int bits)
    {
        float min = values.Count > 0 ? values.Min() : 0, max = values.Count > 0 ? values.Max() : 0;
        float range = max - min;
        if (range <= 0) bits = 0;
        uint maxQ = bits >= 32 ? uint.MaxValue : (1u << bits) - 1;
        var q = bits == 0 ? new uint[0] : values.Select(v => (uint)Math.Round((v - min) / range * maxQ)).ToArray();
        _w.Write((uint)values.Count); _w.Write(range); _w.Write(min); _pos += 12;
        WritePacked(PackBits(q, bits), bits);
    }

    static void PackedInts(IList<uint> values, int bits)
    {
        _w.Write((uint)values.Count); _pos += 4;
        WritePacked(PackBits(values, bits), bits);
    }

    static void WriteCompressedMesh(Mesh m)
    {
        int[][] table = { null, new[] { 20, 10, 16, 8 }, new[] { 16, 8, 10, 6 }, new[] { 10, 6, 8, 4 } };
        var b = table[(int)MeshUtility.GetMeshCompression(m)];
        int Bits(long count) => count <= 1 ? 0 : (int)Math.Ceiling(Math.Log(count, 2));

        var verts = m.vertices;
        PackedFloats(verts.SelectMany(v => new[] { v.x, v.y, v.z }).ToList(), b[0]);

        var uv = new List<float>();
        uint uvInfo = 0;
        var tmp = new List<Vector4>();
        for (int ch = 0; ch < 8; ch++)
        {
            var attr = UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch;
            if (!m.HasVertexAttribute(attr)) continue;
            int dim = m.GetVertexAttributeDimension(attr);
            uvInfo |= (uint)((dim - 1) | 4) << (4 * ch);
            m.GetUVs(ch, tmp);
            foreach (var t in tmp) for (int d = 0; d < dim; d++) uv.Add(t[d]);
        }
        float uvRange = uv.Count > 0 ? uv.Max() - uv.Min() : 0;
        PackedFloats(uv, b[2] + (uvRange >= 1 ? (int)Math.Floor(Math.Log(uvRange, 2)) + 1 : 0));

        var normals = m.normals;
        PackedFloats(normals.SelectMany(n => new[] { n.x, n.y }).ToList(), b[1]);
        var tangents = m.tangents;
        PackedFloats(tangents.SelectMany(t => new[] { t.x, t.y }).ToList(), b[1]);

        // weights: quantised to 31ths, up to three stored per vertex (the last is implied), one bone index per weight
        var weights = new List<uint>();
        var boneIdx = new List<uint>();
        var bpv = m.GetBonesPerVertex();
        var bw = m.GetAllBoneWeights();
        int wi = 0;
        foreach (var count in bpv)
        {
            int sum = 0;
            for (int j = 0; j < count; j++)
            {
                var w = bw[wi + j];
                boneIdx.Add((uint)w.boneIndex);
                if (j == 3) break;                          // the 4th weight is implied
                int q = j == count - 1 ? 31 - sum : Mathf.RoundToInt(w.weight * 31);
                weights.Add((uint)q);
                sum += q;
                if (sum >= 31) break;
            }
            wi += count;
        }
        int bones = m.bindposes.Length;
        PackedInts(weights, bones > 0 ? 5 : 0);
        PackedInts(normals.Select(n => n.z > 0 ? 1u : 0u).ToList(), 1);
        PackedInts(tangents.SelectMany(t => new[] { t.z > 0 ? 1u : 0u, t.w > 0 ? 1u : 0u }).ToList(), 1);

        var colors = m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)
            ? m.colors.SelectMany(c => new[] { c.r, c.g, c.b, c.a }).ToList() : new List<float>();
        PackedFloats(colors, b[3]);
        PackedInts(boneIdx, Bits(bones));

        var tris = new List<uint>();
        for (int s = 0; s < m.subMeshCount; s++) tris.AddRange(m.GetIndices(s).Select(i => (uint)i));
        PackedInts(tris, Bits(tris.Count > 0 ? tris.Max() + 1L : 0));

        _w.Write(uvInfo); _pos += 4;
    }

    static void Sim(Node n, SerializedProperty p)
    {
        if (_w != null && Override(n, p))
            return;
        if (_stripShaderPrograms && n.Name == "m_SubShaders")
        {
            _pos += 4; // the VRChat SDK strips every shader variant from Android avatar builds
            _w?.Write(0);
        }
        else if (n.Type == "string")
        {
            var bytes = p != null && p.propertyType == SerializedPropertyType.String ? Encoding.UTF8.GetBytes(p.stringValue ?? "") : Array.Empty<byte>();
            _pos += 4 + bytes.Length;
            if (_w != null) { _w.Write(bytes.Length); _w.Write(bytes); }
            AlignPos();
        }
        else if (n.Type.StartsWith("PPtr<"))
        {
            _pos += 12;
            if (_w != null)
            {
                var (file, path) = PPtrValue(p);
                _w.Write(file); _w.Write(path);
            }
        }
        else if (n.Children.Count > 0 && n.Children[0].Name == "Array" && n.Type != "Array")
        {
            var arr = n.Children[0];
            var elem = arr.Children[1];
            int count = p != null && p.isArray && p.propertyType != SerializedPropertyType.String ? p.arraySize : 0;
            _pos += 4;
            _w?.Write(count);
            if (_w == null && elem.ByteSize > 0 && !HasVariable(elem))
                _pos += (long)count * elem.ByteSize;
            else
            {
                // walk the elements in sequence; a fresh GetArrayElementAtIndex per element is slow on big arrays
                var e = count > 0 ? p.GetArrayElementAtIndex(0) : null;
                for (int i = 0; i < count; i++)
                {
                    Sim(elem, e);
                    if (i + 1 < count) e.Next(false);
                }
            }
            if (arr.Align) AlignPos();
        }
        else if (n.Type == "TypelessData")
        {
            int count = p != null && p.isArray ? p.arraySize : 0;
            _pos += 4 + count;
            if (_w != null)
            {
                _w.Write(count);
                var e = count > 0 ? p.GetArrayElementAtIndex(0) : null;
                for (int i = 0; i < count; i++)
                {
                    _w.Write((byte)e.intValue);
                    if (i + 1 < count) e.Next(false);
                }
            }
            AlignPos();
        }
        else if (n.Children.Count == 0)
        {
            _pos += Math.Max(0, n.ByteSize);
            if (_w != null) WriteLeaf(n, p);
        }
        else
            foreach (var ch in n.Children)
            {
                var cp = p?.FindPropertyRelative(ch.Name);
                if (cp == null && p != null && _w != null && ch.Children.Count == 0 && TryCompound(p, ch.Name, out float f))
                {
                    _pos += 4; _w.Write(f);
                    if (ch.Align) AlignPos();
                    continue;
                }
                Sim(ch, cp);
            }
        if (n.Align) AlignPos();
    }

    static void WriteLeaf(Node n, SerializedProperty p)
    {
        if (n.Type == "float") { _w.Write(p == null ? 0f : p.propertyType == SerializedPropertyType.Float ? p.floatValue : (float)LongValue(p)); return; }
        if (n.Type == "double") { _w.Write(p == null ? 0d : p.propertyType == SerializedPropertyType.Float ? p.doubleValue : LongValue(p)); return; }
        long v = p == null ? 0 : p.propertyType == SerializedPropertyType.Float ? (long)p.floatValue : LongValue(p);
        for (int i = 0; i < n.ByteSize; i++) _w.Write((byte)(v >> (8 * i)));
    }

    static long LongValue(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Boolean: return p.boolValue ? 1 : 0;
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.Enum:
            case SerializedPropertyType.LayerMask:
            case SerializedPropertyType.ArraySize:
            case SerializedPropertyType.Character:
                try { return p.longValue; } catch { return p.intValue; }
            default: return 0;
        }
    }

    static bool TryCompound(SerializedProperty p, string name, out float f)
    {
        f = 0;
        switch (p.propertyType)
        {
            case SerializedPropertyType.Color: { var c = p.colorValue; f = name == "r" ? c.r : name == "g" ? c.g : name == "b" ? c.b : c.a; return true; }
            case SerializedPropertyType.Vector2: { var v = p.vector2Value; f = name == "x" ? v.x : v.y; return true; }
            case SerializedPropertyType.Vector3: { var v = p.vector3Value; f = name == "x" ? v.x : name == "y" ? v.y : v.z; return true; }
            case SerializedPropertyType.Vector4: { var v = p.vector4Value; f = name == "x" ? v.x : name == "y" ? v.y : name == "z" ? v.z : v.w; return true; }
            case SerializedPropertyType.Quaternion: { var q = p.quaternionValue; f = name == "x" ? q.x : name == "y" ? q.y : name == "z" ? q.z : q.w; return true; }
            default: return false;
        }
    }

    // ---- object identity in the bundle ----

    // pathID = first 8 bytes of MD4(guid text, file type, local id), as the built-in pipeline does
    static readonly Dictionary<Object, long> _pathIds = new Dictionary<Object, long>();
    const string BuiltinExtra = "Resources/unity_builtin_extra";

    static (int file, long path) PPtrValue(SerializedProperty p)
    {
        var o = p != null && p.propertyType == SerializedPropertyType.ObjectReference ? p.objectReferenceValue : null;
        if (o == null) return (0, 0);
        if (_pathIds.TryGetValue(o, out long id)) return (0, id);
        if (AssetDatabase.GetAssetPath(o) == "Library/unity default resources" && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out long lfid))
        {
            _usesDefaultResources = true;
            return (1, lfid);
        }
        return (0, 0);
    }

    public static bool RandomizeVolatileIds = true;

    static long PathId(Object o, string volatilePrefabPath)
    {
        string path = AssetDatabase.GetAssetPath(o);
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long lfid) || string.IsNullOrEmpty(path))
            return StableRandom(o.GetType().Name + o.name + o.GetInstanceID());
        // the upload prefab and the sub-assets of generated files get new ids on every build;
        // a generated file's main object keeps its guid and id (VRCFury rewrites the same path)
        // the compressor writes each build's assets to a new folder, so all of them (main objects too) change
        if (RandomizeVolatileIds && (path == volatilePrefabPath
            || path.StartsWith("Assets/_AvatarCompressorTemp/")
            || (path.StartsWith("Packages/com.vrcfury.temp/") && !AssetDatabase.IsMainAsset(o))))
            return StableRandom(guid + lfid);
        byte[] input;
        if (path == BuiltinExtra || path == "Library/unity default resources")
            input = Encoding.UTF8.GetBytes(path).Concat(BitConverter.GetBytes(lfid)).ToArray();
        else
        {
            int fileType = path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsNativeAsset(o) ? 3 : 2;
            input = Encoding.UTF8.GetBytes(guid).Concat(BitConverter.GetBytes(fileType)).Concat(BitConverter.GetBytes(lfid)).ToArray();
        }
        return BitConverter.ToInt64(Md4(input), 0);
    }

    public static int VolatileSeed;
    static long StableRandom(string key) => BitConverter.ToInt64(Md4(Encoding.UTF8.GetBytes($"vol{VolatileSeed}:" + key)), 0);

    static byte[] Md4(byte[] msg)
    {
        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        long bitLen = (long)msg.Length * 8;
        int padLen = ((msg.Length + 8) / 64 + 1) * 64;
        var buf = new byte[padLen];
        Buffer.BlockCopy(msg, 0, buf, 0, msg.Length);
        buf[msg.Length] = 0x80;
        for (int i = 0; i < 8; i++) buf[padLen - 8 + i] = (byte)(bitLen >> (8 * i));
        var x = new uint[16];
        uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));
        for (int off = 0; off < padLen; off += 64)
        {
            for (int i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(buf, off + 4 * i);
            uint a = a0, b = b0, c = c0, d = d0;
            int[] s1 = { 3, 7, 11, 19 }, s2 = { 3, 5, 9, 13 }, s3 = { 3, 9, 11, 15 };
            for (int i = 0; i < 16; i++)
            {
                uint f = (b & c) | (~b & d);
                uint t = Rol(a + f + x[i], s1[i % 4]); a = d; d = c; c = b; b = t;
            }
            int[] k2 = { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
            for (int i = 0; i < 16; i++)
            {
                uint g = (b & c) | (b & d) | (c & d);
                uint t = Rol(a + g + x[k2[i]] + 0x5a827999, s2[i % 4]); a = d; d = c; c = b; b = t;
            }
            int[] k3 = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
            for (int i = 0; i < 16; i++)
            {
                uint h = b ^ c ^ d;
                uint t = Rol(a + h + x[k3[i]] + 0x6ed9eba1, s3[i % 4]); a = d; d = c; c = b; b = t;
            }
            a0 += a; b0 += b; c0 += c; d0 += d;
        }
        var res = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(a0), 0, res, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(b0), 0, res, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(c0), 0, res, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(d0), 0, res, 12, 4);
        return res;
    }

    static readonly Dictionary<Node, bool> _variable = new Dictionary<Node, bool>();
    static bool HasVariable(Node n)
    {
        if (_variable.TryGetValue(n, out bool v)) return v;
        v = n.ByteSize < 0 || n.Children.Any(HasVariable);
        _variable[n] = v;
        return v;
    }

    static long Simulate(Node root, Object obj)
    {
        var so = new SerializedObject(obj);
        _pos = 0;
        MemoryStream ms = null;
        if (_emitting) { ms = new MemoryStream(); _w = new BinaryWriter(ms); }
        foreach (var ch in root.Children)
            Sim(ch, so.FindProperty(ch.Name));
        if (_emitting) { _w.Flush(); _lastEmit = ms.ToArray(); _w = null; }
        return _pos;
    }

    static bool _emitting;
    static byte[] _lastEmit;

    // ---------------- per-type sizes ----------------

    static long Al(long x) => (x + 3) & ~3L;
    static long Str(string s) => Al(4 + Encoding.UTF8.GetByteCount(s ?? ""));

    static long TexBytes(Texture2D t) => GraphicsFormatUtility.IsCrunchFormat(t.format) ? StorageSize(t) : TextureData(t);

    static long TextureData(Texture2D t)
    {
        long bytes = 0;
        for (int m = 0; m < t.mipmapCount; m++)
            bytes += GraphicsFormatUtility.ComputeMipmapSize(Math.Max(1, t.width >> m), Math.Max(1, t.height >> m), t.graphicsFormat);
        return bytes;
    }

    static long TextureData(int w, int h, int mips, GraphicsFormat f)
    {
        long bytes = 0;
        for (int m = 0; m < mips; m++)
            bytes += GraphicsFormatUtility.ComputeMipmapSize(Math.Max(1, w >> m), Math.Max(1, h >> m), f);
        return bytes;
    }

    static MethodInfo _storage;
    static long StorageSize(Texture t)
    {
        _storage ??= typeof(Editor).Assembly.GetType("UnityEditor.TextureUtil").GetMethod("GetStorageMemorySizeLong", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return Convert.ToInt64(_storage.Invoke(null, new object[] { t }));
    }

    static long AudioDataSize(AudioClip clip)
    {
        var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as AudioImporter;
        if (importer == null) return 0;
        var prop = typeof(AudioImporter).GetProperty("compSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return prop == null ? 0 : Convert.ToInt64(prop.GetValue(importer));
    }

    static long MeshSize(Mesh mesh)
    {
        int vc = mesh.vertexCount;
        long size = 396 + Str(mesh.name);
        for (int s = 0; s < mesh.vertexBufferCount; s++)
            size += (long)mesh.GetVertexBufferStride(s) * vc;
        int idxSize = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 ? 2 : 4;
        long idx = 0;
        for (int s = 0; s < mesh.subMeshCount; s++)
            idx += (long)mesh.GetIndexCount(s) * idxSize;
        size += Al(idx);
        size += 48L * Math.Max(0, mesh.subMeshCount - 1);
        size += 92L * mesh.bindposes.Length;
        var compression = MeshUtility.GetMeshCompression(mesh);
        if (compression != ModelImporterMeshCompression.Off)
        {
            // compressed meshes store quantised PackedBitVectors instead of the vertex/index buffers
            for (int s = 0; s < mesh.vertexBufferCount; s++)
                size -= (long)mesh.GetVertexBufferStride(s) * vc;
            size -= Al(idx) + 40;
            size += CompressedMeshBytes(mesh, (int)compression);
        }
        if (mesh.blendShapeCount > 0)
        {
            var dv = new Vector3[vc]; var dn = new Vector3[vc]; var dt = new Vector3[vc];
            for (int b = 0; b < mesh.blendShapeCount; b++)
            {
                size += Str(mesh.GetBlendShapeName(b)) + 12;
                for (int fr = 0; fr < mesh.GetBlendShapeFrameCount(b); fr++)
                {
                    size += 16;
                    mesh.GetBlendShapeFrameVertices(b, fr, dv, dn, dt);
                    for (int v = 0; v < vc; v++)
                        if (dv[v] != Vector3.zero || dn[v] != Vector3.zero || dt[v] != Vector3.zero)
                            size += 40;
                }
            }
        }
        return size;
    }

    static long CompressedMeshBytes(Mesh mesh, int level)
    {
        // bits per value for Low / Medium / High: position, normal+tangent, uv base, color
        int[][] bits = { null, new[] { 20, 10, 16, 8 }, new[] { 16, 8, 10, 6 }, new[] { 10, 6, 8, 4 } };
        var b = bits[level];
        long vc = mesh.vertexCount;
        long Vec(long n, int bitSize) => 4 + Al((n * bitSize + 7) / 8);
        int Bits(long count) => count <= 1 ? 0 : (int)Math.Ceiling(Math.Log(count, 2));

        int uvDims = 0;
        float uvMin = float.MaxValue, uvMax = float.MinValue;
        var uvs = new List<Vector4>();
        for (int ch = 0; ch < 8; ch++)
        {
            var attr = UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch;
            if (!mesh.HasVertexAttribute(attr)) continue;
            int dim = mesh.GetVertexAttributeDimension(attr);
            uvDims += dim;
            mesh.GetUVs(ch, uvs);
            foreach (var uv in uvs)
                for (int d = 0; d < dim; d++) { uvMin = Math.Min(uvMin, uv[d]); uvMax = Math.Max(uvMax, uv[d]); }
        }
        float uvRange = uvDims > 0 ? uvMax - uvMin : 0;
        int uvBits = uvRange <= 0 ? 0 : b[2] + (uvRange >= 1 ? (int)Math.Floor(Math.Log(uvRange, 2)) + 1 : 0);

        long colorItems = 0; int colorBits = 0;
        if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color))
        {
            colorItems = 4 * vc;
            var cols = mesh.colors;
            bool constant = cols.All(c => c == cols[0]);
            colorBits = constant ? 0 : b[3];
        }

        bool hasN = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal);
        bool hasT = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent);
        long weights = 0, boneIdx = 0;
        foreach (var n in mesh.GetBonesPerVertex()) { weights += Math.Min((int)n, 3); boneIdx += Math.Min((int)n, 4); }
        int bones = mesh.bindposes.Length;

        long indices = 0; int maxIndex = 0;
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            indices += mesh.GetIndexCount(s);
            foreach (var i in mesh.GetIndices(s)) if (i > maxIndex) maxIndex = i;
        }

        return Vec(3 * vc, b[0])
            + Vec(uvDims * vc, uvBits)
            + Vec(hasN ? 2 * vc : 0, b[1]) + Vec(hasT ? 2 * vc : 0, b[1])
            + Vec(weights, bones > 0 ? 5 : 0)
            + Vec(hasN ? vc : 0, 1) + Vec(hasT ? 2 * vc : 0, 1)
            + Vec(colorItems, colorBits)
            + Vec(boneIdx, Bits(bones))
            + Vec(indices, Bits(maxIndex + 1L));
    }

    static MethodInfo _clipStats;
    static long ClipSize(AnimationClip clip)
    {
        _clipStats ??= typeof(AnimationUtility).GetMethod("GetAnimationClipStats", BindingFlags.NonPublic | BindingFlags.Static);
        var stats = _clipStats.Invoke(null, new object[] { clip });
        long F(string n) => Convert.ToInt64(stats.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(stats));
        long size = F("size");
        if (size == 0)
            return 2224 + Str(clip.name);
        return size + Str(clip.name) - 272 + (long)Math.Round(27.75 * F("genericCurves")
            + 23.95 * (F("positionCurves") + F("quaternionCurves") + F("eulerCurves") + F("scaleCurves"))
            + 29.18 * F("muscleCurves") + 40 * F("pptrCurves"));
    }

    // ---- animator controller (compiled ControllerConstant + string table) ----

    static IEnumerable<AnimatorTransitionBase> Live(IEnumerable<AnimatorTransitionBase> ts)
    {
        var list = ts.ToList();
        return list.Any(t => t.solo) ? list.Where(t => t.solo && !t.mute) : list.Where(t => !t.mute);
    }

    static long NodeBytes(Motion m, HashSet<string> tos, bool inTree, ref int clips, ref int trees)
    {
        if (m == null) return 0;
        if (m is AnimationClip c)
        {
            clips++;
            if (inTree) tos.Add(AssetDatabase.GetAssetPath(c) + c.name);
            return 64;
        }
        var bt = (BlendTree)m;
        tos.Add(bt.blendParameter); tos.Add(bt.blendParameterY);
        var kids = bt.children;
        int k = kids.Length;
        long n = 64 + 4L * k;
        double nb = Math.Min(k - 1, 3.6 + 0.07 * k);
        switch (bt.blendType)
        {
            case BlendTreeType.Simple1D: n += 4L * k; break;
            case BlendTreeType.SimpleDirectional2D: n += 8L * k; break;
            case BlendTreeType.FreeformDirectional2D: n += 8L * k + 4L * k + 12L * k * k + (long)Math.Round(k * (4 + 4 * nb)); break;
            case BlendTreeType.FreeformCartesian2D: n += 8L * k + 12L * k * k + (long)Math.Round(k * (4 + 4 * nb)); break;
            case BlendTreeType.Direct: n += 4L * k; break;
        }
        foreach (var ch in kids)
        {
            tos.Add(ch.directBlendParameter);
            n += NodeBytes(ch.motion, tos, true, ref clips, ref trees);
        }
        return n;
    }

    static long ControllerSize(AnimatorController ac)
    {
        var tos = new HashSet<string> { "GravityWeight" };
        foreach (var p in ac.parameters) tos.Add(p.name);
        var layers = ac.layers;
        long body = 4 + 4; // layer + state machine array sizes
        int clips = 0, beh = 0, ranges = 0, indices = 0;
        var syncedTo = new Dictionary<int, List<int>>();
        for (int i = 0; i < layers.Length; i++)
        {
            tos.Add(layers[i].name);
            if (layers[i].syncedLayerIndex >= 0)
            {
                if (!syncedTo.TryGetValue(layers[i].syncedLayerIndex, out var l)) syncedTo[layers[i].syncedLayerIndex] = l = new List<int>();
                l.Add(i);
            }
            body += 40;
        }
        for (int li = 0; li < layers.Length; li++)
        {
            var layer = layers[li];
            if (layer.syncedLayerIndex >= 0 || layer.stateMachine == null) continue;
            var syn = syncedTo.TryGetValue(li, out var sl) ? sl : new List<int>();
            long sm = 20;
            void Trans(string srcShort, string srcFull, AnimatorTransitionBase t, string exitName)
            {
                if (t.destinationState != null)
                {
                    tos.Add($"{srcShort} -> {t.destinationState.name}");
                    tos.Add($"{(srcFull == "AnyState" ? "Entry" : srcFull)} -> {_fullPath[t.destinationState]}");
                }
                else if (t.destinationStateMachine != null)
                {
                    tos.Add($"{srcShort} -> {t.destinationStateMachine.name}");
                    tos.Add($"{srcFull} -> Exit");
                }
                else if (t.isExit)
                {
                    tos.Add($"{srcShort} -> {exitName}");
                    tos.Add($"{srcFull} -> Exit");
                }
            }
            _fullPath.Clear();
            IndexPaths(layer.stateMachine, null);
            void Walk(AnimatorStateMachine m, string path, string parent, string exitName, int inherited, bool root, AnimatorTransitionBase[] exitTrans)
            {
                beh += m.behaviours.Length;
                int inh = inherited + m.behaviours.Length;
                // entry + exit selectors
                sm += 12 + Live(m.entryTransitions).Sum(t => 8 + 16L * t.conditions.Length) + 8;
                sm += 12 + (root ? 8 : Live(exitTrans).Sum(t => 8 + 16L * t.conditions.Length) + 8);
                foreach (var t in Live(m.anyStateTransitions))
                {
                    sm += 44 + 16L * t.conditions.Length;
                    Trans("AnyState", "AnyState", t, exitName);
                }
                foreach (var cs in m.states)
                {
                    var s = cs.state;
                    string full = path + "." + s.name;
                    tos.Add(s.name); tos.Add(parent + "." + s.name); tos.Add(full); tos.Add(s.tag);
                    long st = 56 + 4L * (1 + syn.Count);
                    foreach (var t in Live(s.transitions))
                    {
                        st += 44 + 16L * t.conditions.Length;
                        Trans(s.name, full, t, exitName);
                    }
                    var motions = new List<Motion> { s.motion };
                    motions.AddRange(syn.Select(j => layers[j].GetOverrideMotion(s)));
                    bool top = true;
                    foreach (var mo in motions)
                    {
                        int trees = 0;
                        if (mo != null) st += 4 + NodeBytes(mo, top ? tos : tos, false, ref clips, ref trees);
                        top = false;
                    }
                    int own = s.behaviours.Length;
                    beh += own;
                    if (own + inh > 0) { ranges++; indices += own + inh; }
                    foreach (int j in syn)
                    {
                        int ob = layers[j].GetOverrideBehaviours(s).Length;
                        beh += ob;
                        if (ob > 0) { ranges++; indices += ob; }
                    }
                    sm += st;
                }
                foreach (var ch in m.stateMachines)
                {
                    var ex = m.GetStateMachineTransitions(ch.stateMachine);
                    var named = ex.FirstOrDefault(t => t.destinationState != null || t.destinationStateMachine != null);
                    string exName = named == null ? "Exit" : named.destinationState != null ? named.destinationState.name : named.destinationStateMachine.name;
                    Walk(ch.stateMachine, path + "." + ch.stateMachine.name, ch.stateMachine.name, exName, inh, false, ex);
                }
            }
            Walk(layer.stateMachine, layer.stateMachine.name, layer.stateMachine.name, "Exit", 0, true, Array.Empty<AnimatorTransitionBase>());
            // synced layers' override motions contribute strings too (handled in NodeBytes via tos)
            body += sm;
        }
        var types = ac.parameters.Select(p => p.type).ToList();
        int floats = types.Count(t => t == AnimatorControllerParameterType.Float);
        int ints = types.Count(t => t == AnimatorControllerParameterType.Int);
        int bools = types.Count - floats - ints;
        body += 4 + 12L * types.Count;
        body += 4 + 4 + 4 + (4 + 4L * floats) + (4 + 4L * ints) + Al(4 + bools);
        long tosBytes = 4 + tos.Sum(s => 4 + Str(s));
        return Str(ac.name) + 4 + body + tosBytes + 4 + 12L * clips + 4 + 16L * ranges + 4 + 4L * indices + 4 + 12L * beh + 4;
    }

    static readonly Dictionary<Object, string> _fullPath = new Dictionary<Object, string>();
    static void IndexPaths(AnimatorStateMachine sm, string prefix)
    {
        string me = prefix == null ? sm.name : prefix + "." + sm.name;
        _fullPath[sm] = me;
        foreach (var cs in sm.states) _fullPath[cs.state] = me + "." + cs.state.name;
        foreach (var c in sm.stateMachines) IndexPaths(c.stateMachine, me);
    }

    // ---------------- bundle ----------------

    static readonly HashSet<Type> CompiledAway = new HashSet<Type>
    {
        typeof(AnimatorState), typeof(AnimatorStateMachine), typeof(AnimatorStateTransition), typeof(AnimatorTransition),
        typeof(BlendTree), typeof(AvatarMask),
    };

    static bool IsPacked(Object o)
    {
        if (o == null || CompiledAway.Contains(o.GetType())) return false;
        string path = AssetDatabase.GetAssetPath(o);
        if (path.Contains("/Editor/") || path.StartsWith("Assets/Editor/")) return false;
        if (path == "Library/unity default resources") return false;
        if (o is MonoScript ms && IsEditorScript(ms)) return false;
        // editor-only components (VRCFury, GoGo Loco setup, this compressor) are stripped by the SDK
        // when it builds the bundle, along with everything only they reference
        if (o is VRC.SDKBase.IEditorOnly) return false;
        if (o is Component comp && IsEditorOnly(comp.gameObject)) return false;
        if (o is GameObject go && IsEditorOnly(go)) return false;
        if (o is MonoBehaviour mb && IsEditorScript(MonoScript.FromMonoBehaviour(mb))) return false;
        return true;
    }

    static HashSet<string> _playerAssemblies;
    static readonly Dictionary<MonoScript, bool> _editorScript = new Dictionary<MonoScript, bool>();

    static bool IsEditorScript(MonoScript ms)
    {
        if (ms == null) return false;
        if (_editorScript.TryGetValue(ms, out bool v)) return v;
        string path = AssetDatabase.GetAssetPath(ms);
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var pi = AssetImporter.GetAtPath(path) as PluginImporter;
            v = pi != null && !pi.GetCompatibleWithAnyPlatform() && !pi.GetCompatibleWithPlatform(EditorUserBuildSettings.activeBuildTarget);
        }
        else
        {
            _playerAssemblies ??= new HashSet<string>(UnityEditor.Compilation.CompilationPipeline
                .GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name));
            var cls = ms.GetClass();
            v = cls != null && !_playerAssemblies.Contains(cls.Assembly.GetName().Name);
        }
        _editorScript[ms] = v;
        return v;
    }

    static bool IsEditorOnly(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
            if (t.CompareTag("EditorOnly")) return true;
        return false;
    }

    // Like EditorUtility.CollectDependencies, but follows only what a build keeps: materials keep
    // only texture slots their shader has, and editor-only references are not followed.
    static readonly HashSet<string> EditorRefs = new HashSet<string> { "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset" };

    static List<Object> CollectPacked(GameObject root)
    {
        var seen = new HashSet<Object>();
        var order = new List<Object>();
        var stack = new Stack<(Object o, bool viaController)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (o, viaController) = stack.Pop();
            if (o is AvatarMask && !viaController && !order.Contains(o)) order.Add(o);
            if (o == null || !seen.Add(o)) continue;
            if (CompiledAway.Contains(o.GetType())) { } // followed, not packed
            else if (!IsPacked(o)) continue;
            else order.Add(o);
            bool childViaController = o is AnimatorController || CompiledAway.Contains(o.GetType());
            switch (o)
            {
                case Texture _:
                case Mesh _:
                case MonoScript _:
                    continue;
                case Shader sh:
                    foreach (var d in EditorUtility.CollectDependencies(new Object[] { sh }))
                        if (d != sh) stack.Push((d, childViaController));
                    continue;
                case AnimationClip clip:
                    // a clip only references objects through its object-reference curves and its events;
                    // walking every keyframe with SerializedObject is the slowest part of collecting
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var k in AnimationUtility.GetObjectReferenceCurve(clip, b))
                            if (k.value != null) stack.Push((k.value, childViaController));
                    foreach (var ev in AnimationUtility.GetAnimationEvents(clip))
                        if (ev.objectReferenceParameter != null) stack.Push((ev.objectReferenceParameter, childViaController));
                    continue;
                case Material m:
                    if (m.shader != null) stack.Push((m.shader, false));
                    foreach (var name in m.GetTexturePropertyNames())
                        if (m.shader != null && m.shader.FindPropertyIndex(name) >= 0)
                            stack.Push((m.GetTexture(name), false));
                    continue;
            }
            var so = new SerializedObject(o);
            var p = so.GetIterator();
            bool enter = true;
            while (p.Next(enter))
            {
                enter = true;
                if (p.propertyType == SerializedPropertyType.ObjectReference)
                {
                    if (!EditorRefs.Contains(p.name) && p.objectReferenceValue != null)
                        stack.Push((p.objectReferenceValue, childViaController));
                    enter = false;
                }
                else if (p.propertyType == SerializedPropertyType.String)
                    enter = false;
                else if (p.isArray && p.arraySize > 0 && IsPrimitive(p.arrayElementType))
                    enter = false;
            }
        }
        return order;
    }

    static bool IsPrimitive(string t) => t == "int" || t == "float" || t == "UInt8" || t == "SInt8" || t == "char" || t == "bool"
        || t == "UInt16" || t == "SInt16" || t == "unsigned int" || t == "UInt32" || t == "SInt32" || t == "UInt64" || t == "SInt64" || t == "double";

    static long StrippedTexEnvBytes(Material m)
    {
        long bytes = 0;
        var so = new SerializedObject(m);
        var envs = so.FindProperty("m_SavedProperties.m_TexEnvs");
        for (int i = 0; envs != null && i < envs.arraySize; i++)
        {
            string name = envs.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue;
            if (m.shader == null || m.shader.FindPropertyIndex(name) < 0)
                bytes += Str(name) + 28;
        }
        return bytes;
    }

    public static Result Calculate(GameObject root, string bundleName, string assetPath, bool emit = false)
    {
        LoadTypeTrees();
        var res = new Result();
        var deps = CollectPacked(root);
        _pathIds.Clear();
        _texOffsets.Clear();
        _usesDefaultResources = false;
        _skinnedMeshes.Clear();
        foreach (var smr in deps.OfType<SkinnedMeshRenderer>())
            if (smr.sharedMesh != null) _skinnedMeshes.Add(smr.sharedMesh);
        foreach (var o in deps) _pathIds[o] = PathId(o, AssetDatabase.GetAssetPath(root));
        _cabName = "CAB-" + string.Concat(Md4(Encoding.UTF8.GetBytes(bundleName)).Select(x => x.ToString("x2")));
        _compiled = SerializeCompiled(deps);
        if (emit)
        {
            long off = 0;
            foreach (var t in deps.OfType<Texture2D>().Where(t => !t.isReadable).OrderBy(t => _pathIds[t]))
            {
                _texOffsets[t] = off;
                off = (off + TexBytes(t) + 15) & ~15L;
            }
        }
        var typeCosts = new Dictionary<string, long>();
        var scripts = new HashSet<MonoScript>();
        long objectBytes = 0, padding = 0, resS = 0;
        foreach (var o in deps)
        {
            string type;
            long size;
            Node tree = null;
            _emitting = emit;
            _lastEmit = null;
            switch (o)
            {
                case Texture2D t:
                    type = "Texture2D";
                    long data = TextureData(t);
                    if (GraphicsFormatUtility.IsCrunchFormat(t.format))
                    {
                        long storage = StorageSize(t);
                        data = storage;
                    }
                    size = 200 + Str(t.name);
                    resS += data;
                    res.ByType[type] = res.ByType.TryGetValue(type, out var tv) ? tv + data : data;
                    break;
                case Mesh m: type = "Mesh"; size = MeshSize(m); break;
                case Cubemap cube:
                    type = "Cubemap";
                    long cdata = StorageSize(cube);
                    size = Simulate(_natives["Cubemap"].root, cube) + 88;
                    resS += cdata;
                    res.ByType[type] = res.ByType.TryGetValue(type, out var cv) ? cv + cdata : cdata;
                    break;
                case AudioClip clip:
                    type = "AudioClip";
                    size = Simulate(_natives["AudioClip"].root, clip);
                    long adata = AudioDataSize(clip);
                    resS += adata;
                    res.ByType[type] = res.ByType.TryGetValue(type, out var av) ? av + adata : adata;
                    break;
                case AnimationClip c: type = "AnimationClip"; size = _compiled.TryGetValue(c, out var cb) ? cb.Length : ClipSize(c); break;
                case AnimatorController ac: type = "AnimatorController"; size = _compiled.TryGetValue(ac, out var ab) ? ab.Length : ControllerSize(ac); break;
                case Shader sh when _compiled.TryGetValue(sh, out var sb): type = "Shader"; size = sb.Length; break;
                case MonoBehaviour _:
                case ScriptableObject _:
                    type = "MonoBehaviour";
                    tree = ScriptTree(o.GetType());
                    size = Simulate(tree, o);
                    var script = o is MonoBehaviour b ? MonoScript.FromMonoBehaviour(b) : MonoScript.FromScriptableObject((ScriptableObject)o);
                    if (script != null) scripts.Add(script);
                    typeCosts["114:" + o.GetType().FullName] = TreeCost(tree);
                    break;
                case Material mat:
                    type = "Material";
                    if (emit)
                    {
                        _curMat = mat;
                        size = Simulate(_natives["Material"].root, mat);
                        _curMat = null;
                    }
                    else
                        size = Simulate(_natives["Material"].root, mat) - StrippedTexEnvBytes(mat);
                    break;
                case Shader sh when EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android:
                    type = "Shader";
                    _stripShaderPrograms = true;
                    // + one platform entry with an empty program blob
                    size = Simulate(_natives["Shader"].root, sh) + 52;
                    _stripShaderPrograms = false;
                    break;
                default:
                    type = o.GetType().Name;
                    if (_natives.TryGetValue(type, out var nat))
                    {
                        tree = nat.root;
                        size = Simulate(tree, o);
                    }
                    else
                    {
                        size = 0;
                        Debug.LogWarning($"[BundleSize] no type tree for {type} ({o.name})");
                    }
                    break;
            }
            if (!typeCosts.ContainsKey(type) && type != "MonoBehaviour" && _natives.TryGetValue(type, out var nt))
                typeCosts[type] = TreeCost(nt.root);
            _emitting = false;
            if (emit) res.Emitted[o] = EmitSpecial(o) ?? _lastEmit;
            objectBytes += size;
            padding += ((size + 7) & ~7L) - size;
            res.ByType[type] = res.ByType.TryGetValue(type, out var v) ? v + size : size;
            res.Objects.Add((o, type, size));
        }
        int objects = deps.Count + 1; // + AssetBundle object
        long assetBundleObj = Str(bundleName) + 4 + 12L * deps.Count + 4 + Str(assetPath) + 20 + 20 + 4 + Str(bundleName) + 4 + 4 + 4 + 4 + 4;
        typeCosts["AssetBundle"] = TreeCost(_natives["AssetBundle"].root);
        objectBytes += assetBundleObj;
        res.ByType["AssetBundle"] = assetBundleObj;

        long meta = "2022.3.22f1".Length + 1 + 4 + 1 + 4;
        foreach (var kv in typeCosts)
            meta += 4 + 1 + 2 + (kv.Key.StartsWith("114:") ? 16 : 0) + 16 + 8 + kv.Value + 4;
        meta = Al(meta + 4) + 24L * objects + 4 + 12L * scripts.Count + 4 + 4 + 1;
        long dataOffset = (48 + meta + 15) & ~15L;
        long manifest = 4032 + 96 + 260; // manifest bundle: overhead + manifest + its AssetBundle object (measured)
        res.Overhead = dataOffset + padding + manifest;
        res.ObjectCount = objects;
        res.Uncompressed = objectBytes + resS + res.Overhead;
        if (emit) BuildStreams(res, deps, bundleName, assetBundleObj, dataOffset);
        return res;
    }

    // ---- download-size streams ----

    static byte[] EmitSpecial(Object o)
    {
        switch (o)
        {
            case Texture2D t:
                _emitting = true; _curTex = t;
                Simulate(_natives["Texture2D"].root, t);
                _emitting = false; _curTex = null;
                return _lastEmit;
            case Mesh m:
                _emitting = true; _curMesh = m;
                Simulate(_natives["Mesh"].root, m);
                _emitting = false; _curMesh = null;
                return _lastEmit;
            case AnimationClip _:
            case AnimatorController _:
                return Array.Empty<byte>(); // estimated by ratio
            default:
                return null;
        }
    }

    static void BuildStreams(Result res, List<Object> deps, string bundleName, long assetBundleSize, long dataOffset)
    {
        var sizes = res.Objects.ToDictionary(x => x.obj, x => x.size);
        var types = res.Objects.ToDictionary(x => x.obj, x => x.type);
        // data order: MonoScripts first, then everything (including the AssetBundle object, pathID 1) by pathID
        var entries = deps.Select(o => (obj: o, id: _pathIds[o])).ToList();
        entries.Add((null, 1L));
        var dataOrder = entries.OrderBy(e => e.obj is MonoScript ? 0 : 1).ThenBy(e => e.id).ToList();

        var objects = new MemoryStream();   // object data, as it sits after the metadata
        var tex = new MemoryStream();
        var table = new List<(long id, long start, long size, int typeIndex)>();
        var typeList = new List<(int classId, Node root, MonoScript script)>();
        var typeIndex = new Dictionary<string, int>();

        int TypeOf(Object o)
        {
            int classId; Node root; MonoScript script = null; string key;
            if (o == null) { (classId, root) = _natives["AssetBundle"]; key = "AssetBundle"; }
            else if (o is MonoBehaviour || o is ScriptableObject)
            {
                script = o is MonoBehaviour mb ? MonoScript.FromMonoBehaviour(mb) : MonoScript.FromScriptableObject((ScriptableObject)o);
                classId = 114; root = ScriptTree(o.GetType()); key = "114:" + o.GetType().FullName;
            }
            else { (classId, root) = _natives[types[o]]; key = types[o]; }
            if (!typeIndex.TryGetValue(key, out int idx))
            {
                idx = typeList.Count;
                typeIndex[key] = idx;
                typeList.Add((classId, root, script));
            }
            return idx;
        }

        var anim = _compiled;
        foreach (var kv in anim) res.Emitted[kv.Key] = kv.Value;

        foreach (var (o, id) in dataOrder)
        {
            if (o != null) res.PathIds[o] = id;
            long start = objects.Length;
            byte[] bytes;
            long size;
            if (o == null)
            {
                // AssetBundle object: name, preload table of every object, then the rest of its fields
                var ab = new MemoryStream();
                var w = new BinaryWriter(ab);
                var nb = Encoding.UTF8.GetBytes(bundleName);
                w.Write(nb.Length); w.Write(nb);
                while (ab.Length % 4 != 0) w.Write((byte)0);
                w.Write(deps.Count);
                foreach (var e in entries.Where(e => e.obj != null).OrderBy(e => e.id)) { w.Write(0); w.Write(e.id); }
                w.Flush();
                bytes = ab.ToArray();
                size = assetBundleSize;
            }
            else
            {
                size = sizes[o];
                bytes = res.Emitted.TryGetValue(o, out var b) && b != null ? b : Array.Empty<byte>();
                if (o is Texture2D rt && rt.isReadable) size = Math.Max(size, bytes.Length);
            }
            int ti = TypeOf(o);
            bool estimated = (o is AnimationClip || o is AnimatorController) && !anim.ContainsKey(o);
            switch (o)
            {
                case AnimationClip _ when estimated: res.ClipBytes += size; break;
                case AnimatorController _ when estimated: res.ControllerBytes += size; break;
                default:
                    objects.Write(bytes, 0, (int)Math.Min(bytes.Length, size));
                    for (long i = bytes.Length; i < size; i++) objects.WriteByte(0);
                    break;
            }
            // estimated objects take no room in the stream, but keep their real offsets in the table
            table.Add((id, start + _skipped, size, ti));
            if (estimated)
                _skipped += (size + 7) & ~7L;
            else
                while (objects.Length % 8 != 0) objects.WriteByte(0);
            if (o is Texture2D st && !st.isReadable)
            {
                var raw = st.GetRawTextureData();
                tex.Write(raw, 0, raw.Length);
                while (tex.Length % 16 != 0) tex.WriteByte(0);
            }
            else if (o is Cubemap || o is AudioClip)
                Debug.LogWarning($"[BundleSize] {o.GetType().Name} {o.name}: stream data not emitted");
        }
        _skipped = 0;

        var meta = WriteMetadata(typeList, table, deps.OfType<MonoScript>().Select(s => _pathIds[s]).OrderBy(x => x).ToList());
        long metaOffset = 48;
        long dataStart = (metaOffset + meta.Length + 15) & ~15L;
        var file = new MemoryStream();
        var fw = new BinaryWriter(file);
        long fileSize = dataStart + objects.Length + res.ClipBytes + res.ControllerBytes;
        fw.Write(BE(0u)); fw.Write(BE(0u)); fw.Write(BE(22u)); fw.Write(BE(0u));
        fw.Write((byte)0); fw.Write((byte)0); fw.Write((byte)0); fw.Write((byte)0);
        fw.Write(BE((uint)meta.Length)); fw.Write(BE(fileSize)); fw.Write(BE(dataStart)); fw.Write(BE(0L));
        fw.Write(meta);
        while (file.Length < dataStart) fw.Write((byte)0);
        fw.Write(objects.ToArray());
        fw.Flush();
        res.SerializedStream = file.ToArray();
        res.TextureStream = tex.ToArray();
        res.MetadataBytes = 0; // now emitted exactly
    }

    // Clips, controllers and shaders are compiled at build time (muscle clips, state machine constants, shader
    // variants), so Unity's own player serializer writes just those objects. Everything else is mapped to a stand-in file so references
    // resolve without being written; those references come out as fileID 1 and are patched back to 0.
    static Dictionary<Object, byte[]> _compiled = new Dictionary<Object, byte[]>();
    static bool Compiled(Object o) => o is AnimationClip || o is AnimatorController || o is Shader;

    static Dictionary<Object, byte[]> SerializeCompiled(List<Object> deps)
    {
        var result = new Dictionary<Object, byte[]>();
        var target = EditorUserBuildSettings.activeBuildTarget;
        string internalName = $"archive:/{_cabName}/{_cabName}";
        var refMap = new BuildReferenceMap();
        var write = new List<SerializationInfo>();
        var byId = new Dictionary<ObjectIdentifier, Object>();
        var external = new HashSet<long>();
        var all = new List<ObjectIdentifier>();
        var cache = new Dictionary<string, ObjectIdentifier[]>();
        var builtin = new List<(Object o, long pid, string guid, long lfid)>();

        void Map(Object o, long pid, ObjectIdentifier id)
        {
            all.Add(id);
            bool mine = Compiled(o);
            refMap.AddMapping(mine ? internalName : "archive:/other/other", pid, id);
            if (mine)
            {
                write.Add(new SerializationInfo { serializationObject = id, serializationIndex = pid });
                byId[id] = o;
            }
            else if (Math.Abs(pid) > 1L << 32) external.Add(pid);
        }

        foreach (var o in deps)
        {
            if (!_pathIds.TryGetValue(o, out long pid)) continue;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long lfid)) continue;
            if (!cache.TryGetValue(guid, out var ids))
                cache[guid] = ids = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(new GUID(guid), target);
            int k = Array.FindIndex(ids, i => i.localIdentifierInFile == lfid);
            if (k < 0) builtin.Add((o, pid, guid, lfid));
            else Map(o, pid, ids[k]);
        }
        // built-in objects (unity_builtin_extra, e.g. Legacy Shaders/Diffuse) have no per-asset identifiers;
        // they show up in the dependency walk instead
        if (builtin.Count > 0)
        {
            var found = ContentBuildInterface.GetPlayerDependenciesForObjects(all.ToArray(), target, null);
            foreach (var (o, pid, guid, lfid) in builtin)
            {
                int k = Array.FindIndex(found, i => i.guid.ToString() == guid && i.localIdentifierInFile == lfid);
                if (k >= 0) Map(o, pid, found[k]);
                else Debug.LogWarning($"[BundleSize] no player identifier for {o.GetType().Name} {o.name}");
            }
        }
        if (write.Count == 0) return result;

        // usage over every packed object so materials contribute their shader keywords
        var objIds = write.Select(s => s.serializationObject).ToArray();
        var global = ContentBuildInterface.GetGlobalUsageFromGraphicsSettings();
        var usage = new BuildUsageTagSet();
        ContentBuildInterface.CalculateBuildUsageTags(all.ToArray(), all.ToArray(), global, usage);
        string dir = Path.Combine(Path.GetTempPath(), "BundleSizeCalculator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WriteResult wr;
        // like the Scriptable Build Pipeline, switch on the IPreprocessShaders callbacks (the SDK's stripping) for the write
        var bpi = typeof(BuildPipeline).Assembly.GetType("UnityEditor.Build.BuildPipelineInterfaces");
        var flags = bpi.GetNestedType("BuildCallbacks", BindingFlags.NonPublic | BindingFlags.Public);
        bpi.GetMethod("InitializeBuildCallbacks", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new[] { Enum.Parse(flags, "ShaderProcessors") });
        try
        {
            wr = ContentBuildInterface.WriteSerializedFile(dir, new WriteParameters
            {
                writeCommand = new WriteCommand { fileName = _cabName, internalName = internalName, serializeObjects = write },
                settings = new BuildSettings { target = target, group = BuildPipeline.GetBuildTargetGroup(target), buildFlags = ContentBuildFlags.None },
                globalUsage = global,
                usageSet = usage,
                referenceMap = refMap,
                preloadInfo = new PreloadInfo { preloadObjects = objIds.ToList() },
                bundleInfo = new AssetBundleInfo { bundleName = _cabName, bundleAssets = new List<AssetLoadInfo>() },
            });
        }
        finally
        {
            bpi.GetMethod("CleanupBuildCallbacks", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        }

        var files = new Dictionary<string, byte[]>();
        foreach (var so in wr.serializedObjects)
        {
            if (!byId.TryGetValue(so.serializedObject, out var o)) continue;
            string fn = so.header.fileName;
            if (!files.TryGetValue(fn, out var file))
                files[fn] = file = File.ReadAllBytes(Path.IsPathRooted(fn) ? fn : Path.Combine(dir, Path.GetFileName(fn)));
            var b = new byte[so.header.size];
            Array.Copy(file, (long)so.header.offset, b, 0, b.Length);
            for (int i = 0; i + 12 <= b.Length; i += 4)
                if (BitConverter.ToInt32(b, i) == 1 && external.Contains(BitConverter.ToInt64(b, i + 4)))
                    Array.Clear(b, i, 4);
            result[o] = b;
        }
        Directory.Delete(dir, true);
        return result;
    }

    /// <summary>
    /// Compresses emitted streams the way the SDK's LZMA build does and returns the bundle size.
    /// </summary>
    public static long DownloadSize(Result res)
    {
        if (res.SerializedStream == null) throw new InvalidOperationException("Calculate with emit: true first");
        string dir = Path.Combine(Path.GetTempPath(), "AvatarCompressorSize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ser = Path.Combine(dir, _cabName), tex = ser + ".resS", output = Path.Combine(dir, "bundle");
            File.WriteAllBytes(ser, res.SerializedStream);
            var files = new List<ResourceFile> { new ResourceFile { fileName = ser, fileAlias = _cabName, serializedFile = true } };
            if (res.TextureStream.Length > 0)
            {
                File.WriteAllBytes(tex, res.TextureStream);
                files.Add(new ResourceFile { fileName = tex, fileAlias = _cabName + ".resS", serializedFile = false });
            }
            ContentBuildInterface.ArchiveAndCompress(files.ToArray(), output, UnityEngine.BuildCompression.LZMA);
            return new FileInfo(output).Length;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// LZMA size of raw bytes on their own (used to estimate how well texture data compresses).
    /// </summary>
    public static long CompressedSize(byte[] data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AvatarCompressorSize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "data.resS"), output = Path.Combine(dir, "bundle");
            File.WriteAllBytes(input, data);
            ContentBuildInterface.ArchiveAndCompress(
                new[] { new ResourceFile { fileName = input, fileAlias = "data.resS", serializedFile = false } },
                output, UnityEngine.BuildCompression.LZMA);
            return new FileInfo(output).Length;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static long _skipped;
    static bool _usesDefaultResources;

    static byte[] BE(uint v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; }
    static byte[] BE(long v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; }

    static byte[] WriteMetadata(List<(int classId, Node root, MonoScript script)> typeList,
        List<(long id, long start, long size, int typeIndex)> table, List<long> scriptIds)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Align4() { while ((48 + ms.Length) % 4 != 0) w.Write((byte)0); }
        w.Write(Encoding.ASCII.GetBytes(Application.unityVersion)); w.Write((byte)0);
        w.Write((int)EditorUserBuildSettings.activeBuildTarget);
        w.Write((byte)1); // type trees enabled
        w.Write(typeList.Count);
        foreach (var (classId, root, script) in typeList)
        {
            w.Write(classId);
            w.Write((byte)0);
            short scriptIndex = -1;
            if (script != null) scriptIndex = (short)scriptIds.IndexOf(_pathIds.TryGetValue(script, out long sid) ? sid : 0);
            w.Write(scriptIndex);
            string hashKey = script != null ? "script:" + script.GetClass()?.FullName : "class:" + classId;
            if (classId == 114) w.Write(Md4(Encoding.UTF8.GetBytes("id:" + hashKey)));
            w.Write(Md4(Encoding.UTF8.GetBytes("tree:" + hashKey)));
            WriteTree(w, root);
            w.Write(0); // type dependencies
        }
        w.Write(table.Count);
        foreach (var e in table.OrderBy(e => e.id))
        {
            Align4();
            w.Write(e.id); w.Write(e.start); w.Write((uint)e.size); w.Write(e.typeIndex);
        }
        w.Write(scriptIds.Count);
        foreach (var id in scriptIds) { w.Write(0); Align4(); w.Write(id); }
        if (_usesDefaultResources)
        {
            w.Write(1);
            w.Write((byte)0);
            w.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0x0e, 0, 0, 0, 0, 0, 0, 0 });
            w.Write(0);
            w.Write(Encoding.ASCII.GetBytes("Library/unity default resources")); w.Write((byte)0);
        }
        else w.Write(0);
        w.Write(0);         // ref types
        w.Write((byte)0);   // user information
        w.Flush();
        return ms.ToArray();
    }

    static void WriteTree(BinaryWriter w, Node root)
    {
        var nodes = new List<(Node n, int level)>();
        void Flatten(Node n, int level) { nodes.Add((n, level)); foreach (var c in n.Children) Flatten(c, level + 1); }
        Flatten(root, 0);
        var local = new MemoryStream();
        var localOffsets = new Dictionary<string, uint>();
        uint Offset(string s)
        {
            if (_commonOffsets.TryGetValue(s, out uint c)) return 0x80000000u | c;
            if (!localOffsets.TryGetValue(s, out uint o))
            {
                o = (uint)local.Length;
                localOffsets[s] = o;
                var b = Encoding.UTF8.GetBytes(s);
                local.Write(b, 0, b.Length); local.WriteByte(0);
            }
            return o;
        }
        var body = new MemoryStream();
        var bw = new BinaryWriter(body);
        for (int i = 0; i < nodes.Count; i++)
        {
            var (n, level) = nodes[i];
            uint t = Offset(n.Type), nm = Offset(n.Name);
            bw.Write((ushort)n.Version); bw.Write((byte)level);
            bw.Write((byte)(n.Type == "Array" || n.Type == "TypelessData" ? 1 : 0));
            bw.Write(t); bw.Write(nm); bw.Write(n.ByteSize); bw.Write(i); bw.Write(n.Meta); bw.Write(0UL);
        }
        bw.Flush();
        w.Write(nodes.Count); w.Write((int)local.Length);
        w.Write(body.ToArray()); w.Write(local.ToArray());
    }

}
}
