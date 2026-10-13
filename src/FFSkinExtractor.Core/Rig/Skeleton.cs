using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace FFSkinExtractor.Core.Rig;

/// <summary>Os du squelette de base, transformation locale en repère Unity, à l'échelle d'export.</summary>
public sealed record SkeletonBone(string Name, int Parent, Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public uint Hash { get; } = BoneHash.Of(Name);
}

/// <summary>Hash des noms d'os utilisé par UMA et Unity (Animator.StringToHash : CRC-32 du nom).</summary>
public static class BoneHash
{
    public static uint Of(string name) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(name));
}

/// <summary>
/// Squelette de base d'un personnage (homme ou femme), dans sa pose de liaison (« bind pose »),
/// celle dans laquelle les vêtements sont modélisés.
/// </summary>
public sealed class Skeleton
{
    public required string Gender { get; init; }
    /// <summary>Os triés de sorte que chaque parent précède ses enfants. Le premier est « bone_Root ».</summary>
    public required List<SkeletonBone> Bones { get; init; }

    public int IndexOf(uint hash) => Bones.FindIndex(b => b.Hash == hash);
    public int IndexOf(string name) => Bones.FindIndex(b => b.Name == name);

    /// <summary>Matrices monde (repère Unity) de chaque os.</summary>
    public Matrix4x4[] WorldMatrices()
    {
        var world = new Matrix4x4[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            var b = Bones[i];
            var local = Matrix4x4.CreateScale(b.Scale) * Matrix4x4.CreateFromQuaternion(b.Rotation) * Matrix4x4.CreateTranslation(b.Position);
            world[i] = b.Parent < 0 ? local : local * world[b.Parent];
        }
        return world;
    }

    static readonly Dictionary<string, Skeleton> Cache = new();

    /// <summary>Squelette intégré à l'outil (extrait une fois des fichiers du jeu, voir <see cref="FromAvatar"/>).</summary>
    public static Skeleton For(string gender)
    {
        string g = gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "Female" : "Male";
        lock (Cache)
        {
            if (Cache.TryGetValue(g, out var s)) return s;
            using var stream = typeof(Skeleton).Assembly.GetManifestResourceStream($"FFSkinExtractor.Rig.{g}.json")
                ?? throw new InvalidOperationException($"squelette {g} absent des ressources");
            return Cache[g] = FromJson(g, stream);
        }
    }

    // ---- JSON intégré : [{ "name", "parent", "t":[x,y,z], "r":[x,y,z,w], "s":[x,y,z] }]

    internal sealed record JsonBone(string name, int parent, float[] t, float[] r, float[] s);

    public static Skeleton FromJson(string gender, Stream json)
    {
        var bones = JsonSerializer.Deserialize(json, CoreJson.Default.ListJsonBone)!;
        return new Skeleton
        {
            Gender = gender,
            Bones = bones.Select(b => new SkeletonBone(b.name, b.parent, new Vector3(b.t[0], b.t[1], b.t[2]),
                new Quaternion(b.r[0], b.r[1], b.r[2], b.r[3]), new Vector3(b.s[0], b.s[1], b.s[2]))).ToList(),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(Bones.Select(b => new JsonBone(b.Name, b.Parent,
        [b.Position.X, b.Position.Y, b.Position.Z], [b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W],
        [b.Scale.X, b.Scale.Y, b.Scale.Z])).ToList(), CoreJson.Indented.ListJsonBone);

    // ---- Lecture d'un asset Avatar Unity 2018.4 (sans typetree)

    /// <summary>
    /// Lit le squelette d'un asset Avatar (« Base_FemaleAvatar », « Base_MaleAvatar ») :
    /// hiérarchie, pose par défaut et noms des os. Seul le sous-arbre « bone_Root » est gardé.
    /// </summary>
    public static Skeleton FromAvatar(string gender, byte[] raw)
    {
        var r = new Reader(raw);
        r.String();                                    // m_Name
        r.Int();                                       // m_AvatarSize
        int n = r.Int();                               // m_AvatarSkeleton.m_Node
        var parents = new int[n];
        for (int i = 0; i < n; i++) { parents[i] = r.Int(); r.Int(); }
        int idCount = r.Int();
        var ids = new uint[idCount];
        for (int i = 0; i < idCount; i++) ids[i] = r.UInt();
        if (r.Int() != 0) throw new InvalidDataException("Avatar : axes de squelette non gérés");
        r.Skip(r.Int() * 40);                          // m_AvatarSkeletonPose
        int poseCount = r.Int();                       // m_DefaultPose
        var pose = new (Vector3 T, Quaternion Q, Vector3 S)[poseCount];
        for (int i = 0; i < poseCount; i++)
            pose[i] = (new Vector3(r.Float(), r.Float(), r.Float()), new Quaternion(r.Float(), r.Float(), r.Float(), r.Float()),
                       new Vector3(r.Float(), r.Float(), r.Float()));
        int nameCount = r.Int();                       // m_SkeletonNameIDArray
        var nameIds = new uint[nameCount];
        for (int i = 0; i < nameCount; i++) nameIds[i] = r.UInt();

        var names = KnownStrings(raw);
        string NameOf(int i) =>
            names.TryGetValue(nameIds[i], out var s) ? s
            : names.TryGetValue(ids[i], out var path) ? path[(path.LastIndexOf('/') + 1)..]
            : $"bone_{nameIds[i]:x8}";

        int root = Enumerable.Range(0, n).First(i => NameOf(i) == "bone_Root");
        var order = new List<int>();
        void Visit(int i) { order.Add(i); for (int c = 0; c < n; c++) if (parents[c] == i) Visit(c); }
        Visit(root);
        var newIndex = order.Select((old, idx) => (old, idx)).ToDictionary(x => x.old, x => x.idx);
        var bones = order.Select(i => new SkeletonBone(NameOf(i), i == root ? -1 : newIndex[parents[i]],
            pose[i].T * Uma.UmaMeshDecoder.ExportScale, pose[i].Q, pose[i].S)).ToList();
        return new Skeleton { Gender = gender, Bones = bones };
    }

    // Les noms viennent de la table m_TOS (chemins) ; on complète avec les noms d'os connus de Free Fire.
    static Dictionary<uint, string> KnownStrings(byte[] raw)
    {
        var map = new Dictionary<uint, string>();
        for (int p = 4; p < raw.Length; p++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(p - 4));
            if (len <= 0 || len > 300 || p + len > raw.Length) continue;
            var span = raw.AsSpan(p, len);
            if (span.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e)) continue;
            string s = Encoding.ASCII.GetString(span);
            map.TryAdd(BoneHash.Of(s), s);
        }
        foreach (var s in BoneTables.KnownBoneNames) map.TryAdd(BoneHash.Of(s), s);
        return map;
    }

    ref struct Reader(byte[] data)
    {
        readonly ReadOnlySpan<byte> _d = data;
        int _p;
        public int Int() { int v = BinaryPrimitives.ReadInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public uint UInt() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public float Float() { float v = BinaryPrimitives.ReadSingleLittleEndian(_d[_p..]); _p += 4; return v; }
        public void Skip(int c) => _p += c;
        public string String() { int len = Int(); var s = Encoding.UTF8.GetString(_d.Slice(_p, len)); _p = (_p + len + 3) & ~3; return s; }
    }
}
