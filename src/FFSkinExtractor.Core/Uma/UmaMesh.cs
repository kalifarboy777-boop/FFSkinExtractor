using System.Buffers.Binary;
using System.Numerics;

namespace FFSkinExtractor.Core.Uma;

/// <summary>Jusqu'à 4 influences d'os sur un sommet. Les index renvoient à <see cref="UmaMesh.BoneIndexTable"/>.</summary>
public readonly record struct VertexWeights(int B0, int B1, int B2, int B3, float W0, float W1, float W2, float W3)
{
    public IEnumerable<(int Bone, float Weight)> Influences()
    {
        if (W0 > 0) yield return (B0, W0);
        if (W1 > 0) yield return (B1, W1);
        if (W2 > 0) yield return (B2, W2);
        if (W3 > 0) yield return (B3, W3);
    }
}

/// <summary>Os supplémentaire décrit dans le maillage (os de tissu, attaches…), en repère local Unity.</summary>
public sealed record UmaBone(string Name, uint Hash, uint ParentHash, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>Maillage d'un vêtement Free Fire, décodé depuis un UMAMeshAsset.</summary>
public sealed class UmaMesh
{
    public required string Name { get; init; }
    public required string RootBoneName { get; init; }
    /// <summary>Positions en repère Unity (Y vers le haut, Z vers l'avant), à l'échelle d'export (×2).</summary>
    public required Vector3[] Positions { get; init; }
    public required Vector2[] UVs { get; init; }
    /// <summary>Null quand le maillage n'a pas de poids (il est alors attaché aux os les plus proches à l'export).</summary>
    public VertexWeights[]? Weights { get; init; }
    /// <summary>Triangles de chaque sous-maillage (indices de sommets).</summary>
    public required List<int[]> Submeshes { get; init; }

    /// <summary>
    /// Liste d'os propre au maillage (hash du nom). Quand elle est vide, les index de poids
    /// renvoient à la liste d'os standard du personnage (<see cref="Rig.BoneTables"/>) suivie de <see cref="ExtraBoneHashes"/>.
    /// </summary>
    public required uint[] OwnBoneHashes { get; init; }
    /// <summary>Os ajoutés après la liste standard (index 46, 47…).</summary>
    public required uint[] ExtraBoneHashes { get; init; }
    /// <summary>Description des os absents du squelette de base (nom, parent, transformation locale).</summary>
    public required List<UmaBone> Bones { get; init; }
    /// <summary>Matrices de liaison (repère du maillage → repère de l'os, unités du jeu) des os propres et des os supplémentaires.</summary>
    public Matrix4x4[] OwnBindPoses { get; init; } = [];
    public Matrix4x4[] ExtraBindPoses { get; init; } = [];

    /// <summary>Hash de l'os correspondant à un index de poids.</summary>
    public uint[] BoneIndexTable(IReadOnlyList<uint> standardBones) =>
        OwnBoneHashes.Length > 0 ? OwnBoneHashes : [.. standardBones, .. ExtraBoneHashes];
}

/// <summary>
/// Décodeur du format compressé des UMAMeshAsset de Free Fire.
/// Le format est documenté dans docs/FORMAT.md.
/// </summary>
public static class UmaMeshDecoder
{
    // Quantification des positions : ±1 m sur X, ±2 m sur Y/Z (unités du jeu).
    // Les exports sont mis à l'échelle ×2 pour obtenir une taille humaine (~1,8 m).
    public const float ExportScale = 2f;
    const float RangeX = 1f * ExportScale;
    const float RangeYZ = 2f * ExportScale;

    static readonly int[] StandardArraySizes = [32, 12, 12, 16, 4, 8, 8, 8, 8, 8, 8, 0];

    public static UmaMesh Decode(string name, ReadOnlySpan<byte> raw)
    {
        var r = new Reader(raw, 0x1c);
        r.String();                        // m_Name
        r.String();                        // nom du slot
        r.Int(); r.Int();

        // Données UMA standard (souvent vides : Free Fire stocke sa propre version compressée plus loin)
        int bindCount = r.Int();
        var bindPoses = new Matrix4x4[bindCount];   // bindPoses des os propres (OwnBoneHashes)
        for (int i = 0; i < bindCount; i++) bindPoses[i] = r.Matrix();
        // Tableaux UMA standard, presque toujours vides : unityBoneWeights, vertices, normals, tangents,
        // colors32, uv, uv2, uv3, uv4, clothSkinning, clothSkinningSerialized, submeshes
        foreach (int size in StandardArraySizes)
        {
            int c = r.Int();
            if (size > 0) { r.Skip(c * size); continue; }
            for (int k = 0; k < c; k++) r.Skip(r.Int() * 4);   // submeshes : int[] par élément
        }
        var bones = ReadBones(ref r);
        r.Int();                           // umaBoneCount
        r.Int();                           // hash de l'os racine
        var ownHashes = ReadHashes(ref r);
        int subCount = r.Int();
        int n = r.Int();
        string rootBone = r.String();
        r.Int();

        // 1. UV : n × (u16, u16) normalisés
        Expect(r.Int(), n, name, "UV");
        var uv = new Vector2[n];
        for (int i = 0; i < n; i++)
            uv[i] = new Vector2(r.U16LE() / 65535f, r.U16LE() / 65535f);

        // 2. Poids détaillés : n × (4 × u8 index, 4 × u16 poids sur 10000), ou vide
        VertexWeights[]? weights = null;
        int c2 = r.Int();
        if (c2 != 0)
        {
            Expect(c2, n, name, "poids");
            weights = new VertexWeights[n];
            for (int i = 0; i < n; i++)
            {
                var b = r.Bytes(4);
                weights[i] = new VertexWeights(b[0], b[1], b[2], b[3],
                    r.U16LE() / 10000f, r.U16LE() / 10000f, r.U16LE() / 10000f, r.U16LE() / 10000f);
            }
        }
        // 3-6. tableaux de n × 4 octets optionnels (normales/couleurs compressées, UV secondaires)
        for (int k = 0; k < 4; k++) r.SkipOptional(n, name);

        // 7. Positions : n × 3 × u16 big-endian, Z vers le haut et Y vers l'arrière dans les données
        Expect(r.Int(), n * 6, name, "positions");
        var pos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float x = (r.U16BE() - 32768) / 32768f * RangeX;
            float y = (r.U16BE() - 32768) / 32768f * RangeYZ;
            float z = (r.U16BE() - 32768) / 32768f * RangeYZ;
            pos[i] = new Vector3(x, z, -y);
        }
        r.Align();
        // 8. 3n octets (normales compressées), 9. tableau inutilisé
        r.Skip(r.Int()); r.Align();
        r.Skip(r.Int()); r.Align();
        // 10. Poids compacts : n × 6 octets (4 index sur 6 bits, 3 poids sur 255), ou vide
        int c10 = r.Int();
        if (c10 != 0)
        {
            Expect(c10, n * 6, name, "poids compacts");
            weights ??= new VertexWeights[n];
            for (int i = 0; i < n; i++)
            {
                var v = r.Bytes(6);
                int packed = v[0] << 16 | v[1] << 8 | v[2];
                int w3 = 255 - v[3] - v[4] - v[5];
                weights[i] = new VertexWeights(packed >> 18 & 63, packed >> 12 & 63, packed >> 6 & 63, packed & 63,
                    v[3] / 255f, v[4] / 255f, v[5] / 255f, Math.Max(w3, 0) / 255f);
            }
            r.Align();
        }

        // 11. Sous-maillages : taille en octets puis indices u16 big-endian
        int sc = r.Int();
        if (sc != subCount) throw new InvalidDataException($"{name} : {sc} sous-maillages au lieu de {subCount}");
        var subs = new List<int[]>(sc);
        for (int s = 0; s < sc; s++)
        {
            int bytes = r.Int();
            var idx = new int[bytes / 2];
            for (int i = 0; i < idx.Length; i++) idx[i] = r.U16BE();
            r.Align();
            foreach (int i in idx)
                if (i >= n) throw new InvalidDataException($"{name} : indice {i} hors limites ({n})");
            subs.Add(idx);
        }

        // 12. Os supplémentaires : champs vides, bindPoses, hashes, puis description des os
        var extras = Array.Empty<uint>();
        var extraBindPoses = Array.Empty<Matrix4x4>();
        if (FindExtraBones(raw, r.Pos, out int at))
        {
            var t = new Reader(raw, at);
            extraBindPoses = new Matrix4x4[t.Int()];
            for (int i = 0; i < extraBindPoses.Length; i++) extraBindPoses[i] = t.Matrix();
            extras = ReadHashes(ref t);
            if (t.Pos + 4 <= raw.Length)
            {
                try { bones.AddRange(ReadBones(ref t)); }
                catch (ArgumentOutOfRangeException) { }   // description absente ou tronquée
            }
        }

        return new UmaMesh
        {
            Name = name, RootBoneName = rootBone, Positions = pos, UVs = uv, Weights = weights, Submeshes = subs,
            OwnBoneHashes = ownHashes, ExtraBoneHashes = extras, Bones = bones,
            OwnBindPoses = bindPoses, ExtraBindPoses = extraBindPoses,
        };
    }

    // La section commence par quelques entiers nuls puis « k, k matrices, k, k hashes ».
    static bool FindExtraBones(ReadOnlySpan<byte> raw, int from, out int at)
    {
        for (at = from; at + 8 <= raw.Length; at += 4)
        {
            int k = BinaryPrimitives.ReadInt32LittleEndian(raw[at..]);
            if (k == 0) continue;
            int after = at + 4 + 64 * k;
            return k is > 0 and < 64 && after + 4 + 4 * k <= raw.Length
                   && BinaryPrimitives.ReadInt32LittleEndian(raw[after..]) == k;
        }
        return false;
    }

    static List<UmaBone> ReadBones(ref Reader r)
    {
        int count = r.Int();
        var list = new List<UmaBone>(count);
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(r.Float(), r.Float(), r.Float());
            var q = new Quaternion(r.Float(), r.Float(), r.Float(), r.Float());
            var s = new Vector3(r.Float(), r.Float(), r.Float());
            string name = r.String();
            list.Add(new UmaBone(name, r.UInt(), r.UInt(), p * ExportScale, q, s));
        }
        return list;
    }

    static uint[] ReadHashes(ref Reader r)
    {
        int count = r.Int();
        var h = new uint[count];
        for (int i = 0; i < count; i++) h[i] = r.UInt();
        return h;
    }

    static void Expect(int got, int want, string name, string what)
    {
        if (got != want) throw new InvalidDataException($"{name} : taille de {what} inattendue ({got} au lieu de {want})");
    }

    ref struct Reader(ReadOnlySpan<byte> data, int pos)
    {
        readonly ReadOnlySpan<byte> _d = data;
        int _p = pos;
        public readonly int Pos => _p;
        // Matrix4x4 Unity, rangée e00, e01, e02, e03, e10… (vecteur colonne), transposée pour System.Numerics (vecteur ligne)
        public Matrix4x4 Matrix()
        {
            var f = new float[16];
            for (int i = 0; i < 16; i++) f[i] = Float();
            return new(f[0], f[4], f[8], f[12], f[1], f[5], f[9], f[13], f[2], f[6], f[10], f[14], f[3], f[7], f[11], f[15]);
        }
        public int Int() { int v = BinaryPrimitives.ReadInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public uint UInt() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public float Float() { float v = BinaryPrimitives.ReadSingleLittleEndian(_d[_p..]); _p += 4; return v; }
        public ushort U16LE() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_p..]); _p += 2; return v; }
        public ushort U16BE() { var v = BinaryPrimitives.ReadUInt16BigEndian(_d[_p..]); _p += 2; return v; }
        public ReadOnlySpan<byte> Bytes(int c) { var s = _d.Slice(_p, c); _p += c; return s; }
        public void Skip(int c) => _p += c;
        public void Align() => _p = (_p + 3) & ~3;
        public string String()
        {
            int len = Int();
            if (len < 0 || len > 1024) throw new ArgumentOutOfRangeException(nameof(len));
            var s = System.Text.Encoding.UTF8.GetString(_d.Slice(_p, len)); _p += len; Align(); return s;
        }
        /// <summary>Tableau vide, ou n éléments de 4 octets.</summary>
        public void SkipOptional(int n, string name)
        {
            int c = Int();
            if (c == n) { Skip(c * 4); return; }
            if (c != 0) throw new InvalidDataException($"{name} : tableau inattendu ({c} éléments) à 0x{_p - 4:x}");
        }
    }
}
