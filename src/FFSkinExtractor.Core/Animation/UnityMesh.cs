using System.Numerics;
using AssetsTools.NET;

namespace FFSkinExtractor.Core.Animation;

/// <summary>Un maillage Unity ordinaire (objets d'effet : cheval, pingouin, guitare…), en repère Unity.</summary>
public sealed class UnityMesh
{
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector2[]? UVs { get; init; }
    public Vector4[]? Colors { get; init; }
    /// <summary>Quatre os et poids par sommet (maillages animés par un squelette), ou null.</summary>
    public (int[] Bones, float[] Weights)[]? Skin { get; init; }
    public Matrix4x4[] BindPoses { get; init; } = [];
    /// <summary>Triangles de chaque sous-maillage (indices de sommets).</summary>
    public required List<int[]> Submeshes { get; init; }
}

/// <summary>
/// Lit un Mesh de Unity 2018 : données de sommets (flux et canaux) ou maillage compressé (« PackedBitVector »).
/// Le décodage suit celui d'AssetStudio (licence MIT, Perfare).
/// </summary>
public static class UnityMeshReader
{
    public static UnityMesh Read(GameBundle bundle, AssetTypeValueField mesh)
    {
        string name = mesh["m_Name"].AsString;
        Vector3[]? positions = null;
        Vector3[]? normals = null;
        Vector2[]? uvs = null;
        Vector4[]? colors = null;
        (int[], float[])[]? skin = null;
        int[] indexBuffer;

        var vd = mesh["m_VertexData"];
        int vertexCount = (int)vd["m_VertexCount"].AsUInt;
        byte[] data = vd["m_DataSize"].AsByteArray ?? [];
        var stream = mesh["m_StreamData"];
        if (data.Length == 0 && !stream.IsDummy && stream["size"].AsUInt > 0)
            data = bundle.ReadStreamData(stream["path"].AsString, stream["offset"].AsUInt, (int)stream["size"].AsUInt);

        if (vertexCount > 0 && data.Length > 0)
        {
            var channels = vd["m_Channels"]["Array"].Children
                .Select(c => (Stream: (int)c["stream"].AsByte, Offset: (int)c["offset"].AsByte, Format: (int)c["format"].AsByte, Dim: c["dimension"].AsByte & 0xF))
                .ToArray();
            // Flux : canaux d'un même flux entrelacés, chaque flux aligné sur 16 octets
            int streams = channels.Where(c => c.Dim > 0).Select(c => c.Stream).DefaultIfEmpty(0).Max() + 1;
            var streamOffset = new int[streams];
            var streamStride = new int[streams];
            int offset = 0;
            for (int s = 0; s < streams; s++)
            {
                streamStride[s] = channels.Where(c => c.Stream == s && c.Dim > 0).Sum(c => c.Dim * FormatSize(c.Format));
                streamOffset[s] = offset;
                offset += vertexCount * streamStride[s];
                offset = (offset + 15) & ~15;
            }
            float[]? Channel(int index)
            {
                if (index >= channels.Length) return null;
                var c = channels[index];
                if (c.Dim == 0) return null;
                var result = new float[vertexCount * c.Dim];
                int size = FormatSize(c.Format);
                for (int v = 0; v < vertexCount; v++)
                    for (int d = 0; d < c.Dim; d++)
                    {
                        int at = streamOffset[c.Stream] + c.Offset + streamStride[c.Stream] * v + size * d;
                        if (at + size > data.Length) return null;
                        result[v * c.Dim + d] = ReadComponent(data, at, c.Format);
                    }
                return result;
            }
            if (Channel(0) is { } p) positions = Vec3(p, channels[0].Dim);
            if (Channel(1) is { } n) normals = Vec3(n, channels[1].Dim);
            if (Channel(3) is { } col) colors = Enumerable.Range(0, vertexCount).Select(i => channels[3].Dim >= 4
                ? new Vector4(col[i * 4], col[i * 4 + 1], col[i * 4 + 2], col[i * 4 + 3]) : Vector4.One).ToArray();
            if (Channel(4) is { } uv) uvs = Enumerable.Range(0, vertexCount).Select(i => new Vector2(uv[i * channels[4].Dim], uv[i * channels[4].Dim + 1])).ToArray();
            if (Channel(12) is { } w && Channel(13) is { } bi)
            {
                int dw = channels[12].Dim, di = channels[13].Dim;
                skin = Enumerable.Range(0, vertexCount).Select(i =>
                    (Enumerable.Range(0, 4).Select(k => k < di ? (int)bi[i * di + k] : 0).ToArray(),
                     Enumerable.Range(0, 4).Select(k => k < dw ? w[i * dw + k] : 0f).ToArray())).ToArray();
            }
        }

        var cm = mesh["m_CompressedMesh"];
        if (!cm.IsDummy && cm["m_Vertices"]["m_NumItems"].AsUInt > 0)
        {
            var verts = Floats(cm["m_Vertices"]);
            vertexCount = verts.Length / 3;
            positions = Vec3(verts, 3);
            var uvField = cm["m_UV"];
            if (uvField["m_NumItems"].AsUInt > 0)
            {
                uint info = cm["m_UVInfo"].AsUInt;
                var all = Floats(uvField);
                int dim = 2, start = 0;
                if (info != 0)
                    for (int ch = 0; ch < 8; ch++)
                    {
                        uint bits = (info >> (ch * 4)) & 0xF;
                        if ((bits & 4) == 0) continue;
                        dim = 1 + (int)(bits & 3);
                        break; // premier jeu de coordonnées seulement
                    }
                uvs = Enumerable.Range(0, vertexCount).Select(i => start + i * dim + 1 < all.Length
                    ? new Vector2(all[start + i * dim], all[start + i * dim + 1]) : Vector2.Zero).ToArray();
            }
            var nField = cm["m_Normals"];
            if (nField["m_NumItems"].AsUInt > 0)
            {
                var nd = Floats(nField);
                var signs = Ints(cm["m_NormalSigns"]);
                normals = new Vector3[nd.Length / 2];
                for (int i = 0; i < normals.Length; i++)
                {
                    float x = nd[i * 2], y = nd[i * 2 + 1], zz = 1 - x * x - y * y;
                    var v = zz >= 0 ? new Vector3(x, y, MathF.Sqrt(zz)) : Vector3.Normalize(new Vector3(x, y, 0));
                    if (i < signs.Length && signs[i] == 0) v.Z = -v.Z;
                    normals[i] = v;
                }
            }
            var fc = cm["m_FloatColors"];
            if (!fc.IsDummy && fc["m_NumItems"].AsUInt > 0)
            {
                var c = Floats(fc);
                colors = Enumerable.Range(0, c.Length / 4).Select(i => new Vector4(c[i * 4], c[i * 4 + 1], c[i * 4 + 2], c[i * 4 + 3])).ToArray();
            }
            var wField = cm["m_Weights"];
            if (wField["m_NumItems"].AsUInt > 0)
            {
                var weights = Ints(wField);
                var bones = Ints(cm["m_BoneIndices"]);
                var list = new List<(int[], float[])>();
                int bonePos = 0, j = 0, sum = 0;
                var bi = new int[4];
                var bw = new float[4];
                foreach (int wt in weights)
                {
                    bw[j] = wt / 31f;
                    bi[j] = bones.ElementAtOrDefault(bonePos++);
                    j++;
                    sum += wt;
                    if (sum >= 31 || j == 3)
                    {
                        if (sum < 31) { bw[3] = (31 - sum) / 31f; bi[3] = bones.ElementAtOrDefault(bonePos++); }
                        list.Add((bi, bw));
                        bi = new int[4]; bw = new float[4]; j = 0; sum = 0;
                    }
                }
                skin = list.ToArray();
            }
            indexBuffer = Ints(cm["m_Triangles"]);
        }
        else
        {
            var raw = Bytes(mesh["m_IndexBuffer"]);
            bool use16 = mesh["m_IndexFormat"].IsDummy || mesh["m_IndexFormat"].AsInt == 0;
            indexBuffer = use16
                ? Enumerable.Range(0, raw.Length / 2).Select(i => (int)BitConverter.ToUInt16(raw, i * 2)).ToArray()
                : Enumerable.Range(0, raw.Length / 4).Select(i => (int)BitConverter.ToUInt32(raw, i * 4)).ToArray();
        }

        bool sixteen = mesh["m_IndexFormat"].IsDummy || mesh["m_IndexFormat"].AsInt == 0;
        var submeshes = new List<int[]>();
        foreach (var sm in mesh["m_SubMeshes"]["Array"].Children)
        {
            int first = (int)sm["firstByte"].AsUInt / (sixteen ? 2 : 4);
            int count = (int)sm["indexCount"].AsUInt;
            int topology = sm["topology"].AsInt;
            var tris = new List<int>();
            if (topology == 0)
                for (int i = 0; i + 2 < count && first + i + 2 < indexBuffer.Length; i += 3)
                    tris.AddRange([indexBuffer[first + i], indexBuffer[first + i + 1], indexBuffer[first + i + 2]]);
            else if (topology == 1) // bandes de triangles
                for (int i = 0; i + 2 < count && first + i + 2 < indexBuffer.Length; i++)
                {
                    int a = indexBuffer[first + i], b = indexBuffer[first + i + 1], c = indexBuffer[first + i + 2];
                    if (a == b || a == c || b == c) continue;
                    if ((i & 1) == 1) tris.AddRange([b, a, c]); else tris.AddRange([a, b, c]);
                }
            submeshes.Add(tris.Where(t => t < vertexCount).Count() == tris.Count ? tris.ToArray() : []);
        }

        var bindPoses = mesh["m_BindPose"]["Array"].Children.Select(m =>
        {
            float F(int r, int c) => m[$"e{r}{c}"].AsFloat;
            // Matrix4x4 de Unity (colonnes) → System.Numerics (lignes, vecteur à gauche)
            return new Matrix4x4(F(0, 0), F(1, 0), F(2, 0), F(3, 0), F(0, 1), F(1, 1), F(2, 1), F(3, 1),
                                 F(0, 2), F(1, 2), F(2, 2), F(3, 2), F(0, 3), F(1, 3), F(2, 3), F(3, 3));
        }).ToArray();

        return new UnityMesh
        {
            Name = name,
            Positions = positions ?? [],
            Normals = normals?.Length == positions?.Length ? normals : null,
            UVs = uvs?.Length == positions?.Length ? uvs : null,
            Colors = colors?.Length == positions?.Length ? colors : null,
            Skin = skin?.Length == positions?.Length ? skin : null,
            BindPoses = bindPoses,
            Submeshes = submeshes,
        };
    }

    /// <summary>Tableau d'octets d'un champ « vector&lt;UInt8&gt; » (lu en bloc ou élément par élément).</summary>
    static byte[] Bytes(AssetTypeValueField vector)
    {
        var array = vector["Array"];
        if (array.IsDummy) return [];
        if (array.Value?.ValueType == AssetValueType.ByteArray) return array.AsByteArray ?? [];
        return array.Children.Select(c => c.AsByte).ToArray();
    }

    static Vector3[] Vec3(float[] f, int dim) =>
        Enumerable.Range(0, f.Length / dim).Select(i => new Vector3(f[i * dim], dim > 1 ? f[i * dim + 1] : 0, dim > 2 ? f[i * dim + 2] : 0)).ToArray();

    // Formats de sommets de Unity 2017–2018
    static int FormatSize(int format) => format switch { 0 or 11 or 12 => 4, 1 or 5 or 6 or 9 or 10 => 2, _ => 1 };

    static float ReadComponent(byte[] d, int at, int format) => format switch
    {
        0 => BitConverter.ToSingle(d, at),
        1 => (float)BitConverter.ToHalf(d, at),
        2 or 3 => d[at] / 255f,
        4 => Math.Max((sbyte)d[at] / 127f, -1f),
        5 => BitConverter.ToUInt16(d, at) / 65535f,
        6 => Math.Max(BitConverter.ToInt16(d, at) / 32767f, -1f),
        7 => d[at],
        8 => (sbyte)d[at],
        9 => BitConverter.ToUInt16(d, at),
        10 => BitConverter.ToInt16(d, at),
        11 => BitConverter.ToUInt32(d, at),
        12 => BitConverter.ToInt32(d, at),
        _ => 0,
    };

    static IEnumerable<uint> Bits(AssetTypeValueField v, out int count)
    {
        count = (int)v["m_NumItems"].AsUInt;
        return UnpackBits(Bytes(v["m_Data"]), v["m_BitSize"].AsByte, count);
    }

    static IEnumerable<uint> UnpackBits(byte[] data, int bitSize, int count)
    {
        int index = 0, bit = 0;
        for (int i = 0; i < count; i++)
        {
            uint x = 0;
            int bits = 0;
            while (bits < bitSize && index < data.Length)
            {
                x |= (uint)((data[index] >> bit) << bits);
                int num = Math.Min(bitSize - bits, 8 - bit);
                bit += num;
                bits += num;
                if (bit == 8) { index++; bit = 0; }
            }
            yield return bitSize >= 32 ? x : x & ((1u << bitSize) - 1);
        }
    }

    static float[] Floats(AssetTypeValueField v)
    {
        float range = v["m_Range"].AsFloat, start = v["m_Start"].AsFloat;
        int bitSize = v["m_BitSize"].AsByte;
        float max = (1u << bitSize) - 1;
        return Bits(v, out _).Select(x => bitSize == 0 ? start : x / max * range + start).ToArray();
    }

    static int[] Ints(AssetTypeValueField v) => v.IsDummy ? [] : Bits(v, out _).Select(x => (int)x).ToArray();
}
