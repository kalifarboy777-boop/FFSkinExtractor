using System.Numerics;
using FFSkinExtractor.Core.Uma;
using SharpGLTF.Scenes;

namespace FFSkinExtractor.Core.Rig;

/// <summary>
/// Construit l'armature glTF d'une tenue : squelette de base du personnage, plus les os
/// supplémentaires déclarés par les vêtements (os de tissu, de cheveux…), et relie les poids
/// des sommets aux os.
/// </summary>
public sealed class RigBuilder
{
    readonly Skeleton _skeleton;
    readonly IReadOnlyList<uint> _standard;
    readonly List<NodeBuilder> _joints = [];
    readonly List<Matrix4x4> _world = [];       // repère glTF
    readonly List<int> _parent = [];
    readonly Dictionary<uint, int> _byHash = [];
    readonly int[] _deform;                     // os utilisables pour attacher un maillage sans poids

    public NodeBuilder Root { get; }
    public NodeBuilder[] Joints => [.. _joints];

    // Os qui ne déforment pas le corps : attaches d'armes, sac, repères
    static readonly string[] NonDeforming = ["Weapon", "Backpack", "Footsteps", "Cheat", "ForeTwist", "Bip01"];

    public RigBuilder(Skeleton skeleton, IEnumerable<UmaMesh> meshes)
    {
        _skeleton = skeleton;
        _standard = BoneTables.Standard(skeleton.Gender);
        Root = new NodeBuilder(skeleton.Gender + "_Armature");

        foreach (var b in skeleton.Bones)
            AddJoint(b.Name, b.Hash, b.Parent < 0 ? -1 : b.Parent, b.Position, b.Rotation, b.Scale);

        // Os supplémentaires, dans l'ordre où leurs parents deviennent disponibles
        var pending = meshes.SelectMany(m => m.Bones).Where(b => !_byHash.ContainsKey(b.Hash))
            .GroupBy(b => b.Hash).Select(g => g.First()).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(b => _byHash.ContainsKey(b.ParentHash)).ToList();
            if (ready.Count == 0)
            {
                // Parent introuvable : on accroche l'os aux hanches pour ne pas le perdre
                ready = [pending[0] with { ParentHash = BoneHash.Of("bone_Hips") }];
            }
            foreach (var b in ready)
            {
                AddJoint(b.Name, b.Hash, _byHash.GetValueOrDefault(b.ParentHash, 0), b.Position, b.Rotation, b.Scale);
                pending.RemoveAll(p => p.Hash == b.Hash);
            }
        }

        _deform = Enumerable.Range(0, skeleton.Bones.Count)
            .Where(i => skeleton.Bones[i].Name.StartsWith("bone_") || skeleton.Bones[i].Name.EndsWith("_Dummy001"))
            .Where(i => !NonDeforming.Any(w => skeleton.Bones[i].Name.Contains(w)) && skeleton.Bones[i].Name != "bone_Root")
            .ToArray();
    }

    void AddJoint(string name, uint hash, int parent, Vector3 t, Quaternion r, Vector3 s)
    {
        // Unity (main gauche) → glTF (main droite) : miroir sur X
        var node = (parent < 0 ? Root : _joints[parent]).CreateNode(name)
            .WithLocalTranslation(new Vector3(-t.X, t.Y, t.Z))
            .WithLocalRotation(new Quaternion(r.X, -r.Y, -r.Z, r.W))
            .WithLocalScale(s);
        _byHash.TryAdd(hash, _joints.Count);
        _joints.Add(node);
        _parent.Add(parent);
        _world.Add(node.WorldMatrix);
    }

    /// <summary>
    /// Influences (index d'articulation, poids) d'un sommet. <paramref name="position"/> est en repère glTF,
    /// et sert à attacher aux os les plus proches les sommets sans poids exploitables.
    /// </summary>
    public (int Joint, float Weight)[] Bind(UmaMesh mesh, int vertex, Vector3 position, uint[] table)
    {
        if (mesh.Weights != null)
        {
            var list = new List<(int, float)>(4);
            float total = 0;
            foreach (var (bone, w) in mesh.Weights[vertex].Influences())
            {
                if (bone >= table.Length || !_byHash.TryGetValue(table[bone], out int joint)) continue;
                int at = list.FindIndex(x => x.Item1 == joint);
                if (at >= 0) list[at] = (joint, list[at].Item2 + w); else list.Add((joint, w));
                total += w;
            }
            if (total > 0.01f) return list.Select(x => (x.Item1, x.Item2 / total)).ToArray();
        }
        return [(Nearest(position), 1f)];
    }

    /// <summary>Table index de poids → hash d'os pour un maillage.</summary>
    public uint[] TableFor(UmaMesh mesh) => mesh.BoneIndexTable(_standard);

    int Nearest(Vector3 p)
    {
        int best = 0; float bestD = float.MaxValue;
        foreach (int i in _deform)
        {
            var a = _world[i].Translation;
            float d = (p - a).Length();
            // Segment vers chaque enfant déformant : distance au « bâton » de l'os plutôt qu'à son articulation
            for (int c = 0; c < _skeleton.Bones.Count; c++)
            {
                if (_parent[c] != i || Array.IndexOf(_deform, c) < 0) continue;
                var b = _world[c].Translation;
                var ab = b - a;
                float len2 = ab.LengthSquared();
                if (len2 < 1e-8f) continue;
                float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0, 1);
                d = Math.Min(d, (p - (a + t * ab)).Length());
            }
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
