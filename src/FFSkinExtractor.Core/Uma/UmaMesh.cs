using System.Buffers.Binary;
using System.Numerics;

namespace FFSkinExtractor.Core.Uma;

/// <summary>Poids d'un sommet : 4 index d'os (squelette de base) et 4 poids normalisés.</summary>
public readonly record struct VertexWeights(byte B0, byte B1, byte B2, byte B3, float W0, float W1, float W2, float W3);

/// <summary>Maillage d'un vêtement Free Fire, décodé depuis un UMAMeshAsset.</summary>
public sealed class UmaMesh
{
    public required string Name { get; init; }
    public required string RootBoneName { get; init; }
    /// <summary>Positions en mètres, repère Unity (Y vers le haut, main gauche).</summary>
    public required Vector3[] Positions { get; init; }
    public required Vector2[] UVs { get; init; }
    /// <summary>Null quand le maillage est entièrement lié à un seul os.</summary>
    public VertexWeights[]? Weights { get; init; }
    /// <summary>Triangles de chaque sous-maillage (indices de sommets).</summary>
    public required List<int[]> Submeshes { get; init; }
}

/// <summary>
/// Décodeur du format compressé des UMAMeshAsset de Free Fire.
/// Le format est documenté dans docs/FORMAT.md.
/// </summary>
public static class UmaMeshDecoder
{
    // Plage de quantification des positions (en mètres) : ±4 m sur Y/Z, ±2 m sur X.
    const float RangeYZ = 4f;
    const float RangeX = 2f;

    static readonly byte[] RootBoneMarker = "\x09\0\0\0bone_Root"u8.ToArray();

    public static UmaMesh Decode(string name, ReadOnlySpan<byte> raw)
    {
        int p = raw.IndexOf(RootBoneMarker);
        if (p < 4) throw new InvalidDataException($"{name} : racine du squelette introuvable");
        int n = ReadInt(raw, p - 4);
        var r = new Reader(raw, p);
        string rootBone = r.String();
        r.Int(); // toujours 1

        // 1. UV : n × (u16, u16) normalisés
        int c = r.Int(); Expect(c, n, name, "UV");
        var uv = new Vector2[n];
        for (int i = 0; i < n; i++)
            uv[i] = new Vector2(r.U16LE() / 65535f, r.U16LE() / 65535f);

        // 2. Poids d'os : n × (4 × u8 index, 4 × u16 poids sur 10000), ou vide
        VertexWeights[]? weights = null;
        c = r.Int();
        if (c != 0)
        {
            Expect(c, n, name, "poids");
            weights = new VertexWeights[n];
            for (int i = 0; i < n; i++)
            {
                var b = r.Bytes(4);
                weights[i] = new VertexWeights(b[0], b[1], b[2], b[3],
                    r.U16LE() / 10000f, r.U16LE() / 10000f, r.U16LE() / 10000f, r.U16LE() / 10000f);
            }
        }
        r.ExpectEmpty(name);
        // 4. n × 4 octets (normales ou couleurs, non utilisés pour l'instant)
        c = r.Int(); r.Skip(c * 4);
        r.ExpectEmpty(name);
        r.ExpectEmpty(name);

        // 7. Positions : n × 3 × u16 big-endian
        c = r.Int(); Expect(c, n * 6, name, "positions");
        var pos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float x = (r.U16BE() - 32768) / 32768f * RangeX;
            float y = (r.U16BE() - 32768) / 32768f * RangeYZ;
            float z = (r.U16BE() - 32768) / 32768f * RangeYZ;
            // Données en Z vers le haut : on repasse en Y vers le haut (repère Unity)
            pos[i] = new Vector3(x, z, y);
        }
        r.Align();
        // 8. 3n octets, 9. 6n octets ou vide (non utilisés pour l'instant)
        c = r.Int(); r.Skip(c); r.Align();
        c = r.Int(); r.Skip(c); r.Align();
        if (c == 0) { c = r.Int(); r.Skip(c); r.Align(); }

        // 10. Sous-maillages : taille en octets puis indices u16 big-endian
        int subCount = r.Int();
        var subs = new List<int[]>(subCount);
        for (int s = 0; s < subCount; s++)
        {
            int bytes = r.Int();
            var idx = new int[bytes / 2];
            for (int i = 0; i < idx.Length; i++) idx[i] = r.U16BE();
            r.Align();
            foreach (int i in idx)
                if (i >= n) throw new InvalidDataException($"{name} : indice {i} hors limites ({n})");
            subs.Add(idx);
        }
        return new UmaMesh { Name = name, RootBoneName = rootBone, Positions = pos, UVs = uv, Weights = weights, Submeshes = subs };
    }

    static int ReadInt(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    static void Expect(int got, int want, string name, string what)
    {
        if (got != want) throw new InvalidDataException($"{name} : taille de {what} inattendue ({got} au lieu de {want})");
    }

    ref struct Reader(ReadOnlySpan<byte> data, int pos)
    {
        readonly ReadOnlySpan<byte> _d = data;
        int _p = pos;
        public int Int() { int v = BinaryPrimitives.ReadInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public ushort U16LE() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_p..]); _p += 2; return v; }
        public ushort U16BE() { var v = BinaryPrimitives.ReadUInt16BigEndian(_d[_p..]); _p += 2; return v; }
        public ReadOnlySpan<byte> Bytes(int c) { var s = _d.Slice(_p, c); _p += c; return s; }
        public void Skip(int c) => _p += c;
        public void Align() => _p = (_p + 3) & ~3;
        public string String() { int len = Int(); var s = System.Text.Encoding.UTF8.GetString(_d.Slice(_p, len)); _p += len; Align(); return s; }
        public void ExpectEmpty(string name)
        {
            int c = Int();
            if (c != 0) throw new InvalidDataException($"{name} : tableau vide attendu, {c} trouvé à 0x{_p - 4:x}");
        }
    }
}
