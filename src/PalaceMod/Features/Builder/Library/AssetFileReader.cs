using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/*
    A minimal reader for the game's own serialized asset files (sharedassetsN.assets, resources.assets), just enough to
    copy a Mesh's buffers out of the player's install: the file header and object table of serialized format 22 (Unity
    2022.3), and the Mesh layout of 2022.3 up to m_VertexData. Thronefall's builds carry no type trees, so the layout is
    fixed here (from Unity's 2022.3 Mesh type tree) and every read is checked: the Unity version in the header must be
    the catalog's, the object must be a Mesh with the expected name and vertex count, and anything unexpected (blend
    shapes, compressed or streamed vertex data, type trees) is refused rather than guessed at.

    Pure C# (no UnityEngine) so it's unit-testable; SceneryLibrary turns a RawMesh into a UnityEngine.Mesh.
*/
namespace PalaceMod.Builder.Library
{
    internal sealed class RawChannel
    {
        public int Stream, Offset, Format, Dimension;
    }

    internal sealed class RawSubMesh
    {
        public uint FirstByte, IndexCount, BaseVertex, FirstVertex, VertexCount;
        public int Topology;
        public float[] Center = new float[3], Extent = new float[3];
    }

    /// <summary>A Mesh's data as stored: its channels, the vertex streams back to back (each 16-byte aligned), the raw
    /// index buffer (16- or 32-bit) and the submeshes.</summary>
    internal sealed class RawMesh
    {
        public string Name;
        public int VertexCount;
        public bool Index32;
        public byte[] IndexBuffer, VertexData;
        public List<RawChannel> Channels = [];
        public List<RawSubMesh> SubMeshes = [];

        /// <summary>Byte size of one component of a vertex format (UnityEngine.Rendering.VertexAttributeFormat order).</summary>
        public static int FormatSize(int format) => format switch
        {
            0 => 4,               // Float32
            1 => 2,               // Float16
            2 or 3 => 1,          // UNorm8, SNorm8
            4 or 5 => 2,          // UNorm16, SNorm16
            6 or 7 => 1,          // UInt8, SInt8
            8 or 9 => 2,          // UInt16, SInt16
            10 or 11 => 4,        // UInt32, SInt32
            _ => throw new InvalidDataException($"unknown vertex format {format}"),
        };

        public int StreamCount
        {
            get
            {
                int n = 0;
                foreach (var c in Channels) if (c.Dimension > 0) n = Math.Max(n, c.Stream + 1);
                return n;
            }
        }

        /// <summary>The stride of each stream and where it starts in VertexData.</summary>
        public (int offset, int stride)[] Streams()
        {
            var result = new (int, int)[StreamCount];
            int offset = 0;
            for (int s = 0; s < result.Length; s++)
            {
                int stride = 0;
                foreach (var c in Channels)
                    if (c.Stream == s && c.Dimension > 0) stride += c.Dimension * FormatSize(c.Format);
                result[s] = (offset, stride);
                offset += stride * VertexCount;
                offset = (offset + 15) & ~15;
            }
            return result;
        }
    }

    internal sealed class AssetFileReader : IDisposable
    {
        public const int MeshClassId = 43;

        public string UnityVersion { get; private set; }
        public int Format { get; private set; }

        private readonly Stream stream;
        private readonly BinaryReader reader;
        private bool bigEndian;
        private long dataOffset;
        private readonly Dictionary<long, (long start, uint size, int classId)> objects = [];

        public AssetFileReader(string path) : this(File.OpenRead(path)) { }

        public AssetFileReader(Stream stream)
        {
            this.stream = stream;
            reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            ReadHeader();
        }

        public void Dispose() => reader.Dispose();

        public bool Has(long pathId) => objects.ContainsKey(pathId);

        public int ClassOf(long pathId) => objects.TryGetValue(pathId, out var o) ? o.classId : -1;

        private void ReadHeader()
        {
            bigEndian = true;
            U32();                     // legacy metadata size
            U32();                     // legacy file size
            Format = (int)U32();
            U32();                     // legacy data offset
            if (Format < 22) throw new InvalidDataException($"serialized format {Format}, expected 22 or newer");
            bool littleMeta = reader.ReadByte() == 0;
            reader.ReadBytes(3);
            U32();                     // metadata size
            I64();                     // file size
            dataOffset = I64();
            I64();                     // reserved
            bigEndian = !littleMeta;

            UnityVersion = CString();
            I32();                     // target platform
            bool typeTrees = reader.ReadByte() != 0;
            if (typeTrees) throw new InvalidDataException("the file has type trees; this reader only handles stripped builds");
            int typeCount = I32();
            var classes = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                int classId = I32();
                reader.ReadByte();     // isStrippedType
                short scriptIndex = I16();
                if (classId == 114 || scriptIndex >= 0) reader.ReadBytes(16);   // script id
                reader.ReadBytes(16);  // old type hash
                classes[i] = classId;
            }
            int objectCount = I32();
            for (int i = 0; i < objectCount; i++)
            {
                Align();
                long pathId = I64();
                long start = I64();
                uint size = U32();
                int type = I32();
                objects[pathId] = (start, size, type >= 0 && type < classes.Length ? classes[type] : -1);
            }
        }

        /// <summary>Reads a Mesh object. Throws InvalidDataException if it isn't one or uses something this reader
        /// doesn't handle.</summary>
        public RawMesh ReadMesh(long pathId)
        {
            if (!objects.TryGetValue(pathId, out var obj)) throw new InvalidDataException($"no object {pathId}");
            if (obj.classId != MeshClassId) throw new InvalidDataException($"object {pathId} is class {obj.classId}, not a Mesh");
            stream.Position = dataOffset + obj.start;
            long end = stream.Position + obj.size;
            var m = new RawMesh { Name = AlignedString() };

            int subCount = Count(48);
            for (int i = 0; i < subCount; i++)
            {
                var s = new RawSubMesh { FirstByte = U32(), IndexCount = U32(), Topology = I32(), BaseVertex = U32(), FirstVertex = U32(), VertexCount = U32() };
                for (int k = 0; k < 3; k++) s.Center[k] = F32();
                for (int k = 0; k < 3; k++) s.Extent[k] = F32();
                m.SubMeshes.Add(s);
            }

            // m_Shapes: only meshes without blend shapes are supported.
            Skip(Count(40), 40);       // vertices
            if (Count(12) != 0) throw new InvalidDataException("the mesh has blend shapes");
            if (Count(16) != 0) throw new InvalidDataException("the mesh has blend shape channels");
            Skip(Count(4), 4);         // fullWeights
            Skip(Count(64), 64);       // m_BindPose
            Skip(Count(4), 4);         // m_BoneNameHashes
            U32();                     // m_RootBoneNameHash
            Skip(Count(24), 24);       // m_BonesAABB
            Skip(Count(4), 4);         // m_VariableBoneCountWeights
            int compression = reader.ReadByte();
            reader.ReadByte();         // m_IsReadable
            reader.ReadByte();         // m_KeepVertices
            reader.ReadByte();         // m_KeepIndices
            Align();
            if (compression != 0) throw new InvalidDataException($"the mesh is compressed ({compression})");
            m.Index32 = I32() == 1;
            m.IndexBuffer = Bytes();
            Align();

            m.VertexCount = (int)U32();
            int channels = Count(4);
            for (int i = 0; i < channels; i++)
                m.Channels.Add(new RawChannel { Stream = reader.ReadByte(), Offset = reader.ReadByte(), Format = reader.ReadByte(), Dimension = reader.ReadByte() & 0xF });
            m.VertexData = Bytes();
            Align();
            if (stream.Position > end) throw new InvalidDataException("the mesh ran past its object");
            if (m.VertexCount > 0 && m.VertexData.Length == 0) throw new InvalidDataException("the mesh's vertex data is streamed from another file");
            var streams = m.Streams();
            int needed = streams.Length == 0 ? 0 : streams[^1].offset + streams[^1].stride * m.VertexCount;
            if (m.VertexData.Length < needed) throw new InvalidDataException($"vertex data is {m.VertexData.Length} bytes, the channels need {needed}");
            return m;
        }

        // ---- Primitives (the endianness of the part being read) ------------------------------------------------

        private int Count(int elementSize)
        {
            int n = I32();
            long left = stream.Length - stream.Position;
            if (n < 0 || (long)n * elementSize > left) throw new InvalidDataException($"bad array length {n}");
            return n;
        }

        private void Skip(int count, int size) => stream.Position += (long)count * size;

        private byte[] Bytes() => reader.ReadBytes(Count(1));

        private string AlignedString()
        {
            var s = Encoding.UTF8.GetString(Bytes());
            Align();
            return s;
        }

        private string CString()
        {
            var bytes = new List<byte>();
            for (int b; (b = reader.ReadByte()) != 0;) bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private void Align() => stream.Position = (stream.Position + 3) & ~3L;

        private byte[] Raw(int n)
        {
            var b = reader.ReadBytes(n);
            if (b.Length != n) throw new EndOfStreamException();
            if (bigEndian == BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        private uint U32() => BitConverter.ToUInt32(Raw(4), 0);
        private int I32() => BitConverter.ToInt32(Raw(4), 0);
        private short I16() => BitConverter.ToInt16(Raw(2), 0);
        private long I64() => BitConverter.ToInt64(Raw(8), 0);
        private float F32() => BitConverter.ToSingle(Raw(4), 0);
    }
}
