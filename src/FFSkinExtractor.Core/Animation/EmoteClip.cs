using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using FFSkinExtractor.Core.Rig;

namespace FFSkinExtractor.Core.Animation;

/// <summary>Clés d'un os pendant une emote, en repère Unity, à l'échelle d'export.</summary>
public sealed class BoneTrack(string bone)
{
    public string Bone { get; } = bone;
    public List<(float Time, Vector3 Value)> Positions { get; } = [];
    public List<(float Time, Quaternion Value)> Rotations { get; } = [];
    public List<(float Time, Vector3 Value)> Scales { get; } = [];
}

/// <summary>Une emote du jeu : animation des os du personnage (« Male_DAB », « Female_Clap »).</summary>
public sealed record EmoteClip(string Name, string Gender, float Duration, IReadOnlyList<BoneTrack> Tracks);

/// <summary>
/// Lit les animations d'emote rangées dans les bundles « optionalab_emote… » : clips « génériques » de Unity,
/// dont les courbes sont regroupées en trois blocs (clés espacées, échantillons réguliers, constantes)
/// et rattachées aux os par le CRC32 de leur chemin (« bone_Root/Bip01/bone_Hips… »).
/// Le décodage suit celui d'AssetStudio (licence MIT, Perfare).
/// </summary>
public static class EmoteReader
{
    const float SampleRate = 30f;

    public static bool IsEmoteBundle(string path)
    {
        string name = Path.GetFileName(path).Split('.')[0].ToLowerInvariant();
        return name.StartsWith("optionalab_emote_") || name.StartsWith("optionalab_groupanim_") || name.StartsWith("optionalab_superemote_");
    }

    /// <summary>Noms des clips d'un bundle, avec le genre déduit du nom (« Male_… », « Female_… »).</summary>
    public static List<(string Name, string Gender, long PathId)> List(GameBundle bundle)
    {
        var result = new List<(string, string, long)>();
        foreach (var info in bundle.OfType(AssetClassID.AnimationClip))
        {
            try
            {
                // Le nom suffit à écarter les clips d'effets et d'objets, sans décoder leurs courbes
                if (!MayAnimateCharacter(bundle.ReadName(info))) continue;
                var clip = bundle.BaseFieldWithout(info, "m_16BitCurves");
                string name = clip["m_Name"].AsString;
                // Clips des accessoires de l'emote (rose, drone, livre) : ils n'animent pas le personnage
                var hashes = clip["m_ClipBindingConstant"]["genericBindings"]["Array"].Children
                    .Where(b => b["typeID"].AsInt == 4).Select(b => b["path"].AsUInt).Distinct().ToList();
                string[] genders = GenderOf(name) is { } g ? [g] : ["Male", "Female"];
                var best = genders.Select(x => (Gender: x, Bones: hashes.Count(BonePaths(x).ContainsKey))).MaxBy(x => x.Bones);
                if (best.Bones >= 10) result.Add((name, best.Gender, info.PathId));
            }
            catch (Exception) { /* clip d'un format inconnu */ }
        }
        return result;
    }

    static bool MayAnimateCharacter(string name)
    {
        if (name.Length == 0 || name.StartsWith("FF_VFX", StringComparison.OrdinalIgnoreCase) || name.StartsWith("VFX", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Recorded", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Take 0", StringComparison.OrdinalIgnoreCase))
            return false;
        return GenderOf(name) != null || name.Contains("Emote", StringComparison.OrdinalIgnoreCase) || name.Contains("Anim", StringComparison.OrdinalIgnoreCase);
    }

    static readonly Dictionary<string, Dictionary<uint, string>> PathCache = [];

    /// <summary>CRC32 du chemin de chaque os du squelette de base (« bone_Root/Bip01/bone_Hips ») → nom de l'os.</summary>
    static Dictionary<uint, string> BonePaths(string gender)
    {
        lock (PathCache)
        {
            if (PathCache.TryGetValue(gender, out var cached)) return cached;
            var skeleton = Skeleton.For(gender);
            var byPath = new Dictionary<uint, string>();
            var paths = new string[skeleton.Bones.Count];
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var b = skeleton.Bones[i];
                paths[i] = b.Parent < 0 ? b.Name : paths[b.Parent] + "/" + b.Name;
                byPath.TryAdd(BoneHash.Of(paths[i]), b.Name);
            }
            return PathCache[gender] = byPath;
        }
    }

    /// <summary>Genre écrit dans le nom du clip : « Male_DAB », « GroupAnim_Riding_Female », « FemaleSit ».</summary>
    public static string? GenderOf(string clip)
    {
        var tokens = clip.Split('_', ' ');
        if (tokens.Any(t => t.StartsWith("Female", StringComparison.OrdinalIgnoreCase))) return "Female";
        if (tokens.Any(t => t.StartsWith("Male", StringComparison.OrdinalIgnoreCase))) return "Male";
        return null;
    }

    public static EmoteClip Read(GameBundle bundle, long pathId, string gender)
    {
        var info = bundle.GetInfo(pathId) ?? throw new KeyNotFoundException($"clip {pathId} introuvable");
        var clip = bundle.BaseFieldWithout(info, "m_16BitCurves");
        string name = clip["m_Name"].AsString;
        var muscle = clip["m_MuscleClip"];
        float stop = muscle["m_StopTime"].AsFloat;
        var data = muscle["m_Clip"]["data"];

        var byPath = BonePaths(gender);

        // Liaisons : chaque liaison de Transform occupe 3 courbes (position, échelle, angles) ou 4 (quaternion)
        var bindings = new List<(string? Bone, int Attribute, int Start, int Count)>();
        int curves = 0;
        foreach (var b in clip["m_ClipBindingConstant"]["genericBindings"]["Array"].Children)
        {
            int type = b["typeID"].AsInt, attribute = (int)b["attribute"].AsUInt;
            int count = type == 4 ? attribute switch { 1 or 3 or 4 => 3, 2 => 4, _ => 1 } : 1;
            string? bone = type == 4 && byPath.TryGetValue(b["path"].AsUInt, out var n) ? n : null;
            bindings.Add((bone, type == 4 ? attribute : 0, curves, count));
            curves += count;
        }
        // Clés espacées : chaque clé porte le polynôme du segment qui la suit (a·dt³ + b·dt² + c·dt + d)
        var streamed = data["m_StreamedClip"];
        int streamedCount = (int)streamed["curveCount"].AsUInt;
        var segments = new List<(float Time, float A, float B, float C, float D)>[Math.Max(streamedCount, 0)];
        for (int i = 0; i < segments.Length; i++) segments[i] = [];
        var words = streamed["data"]["Array"].Children.Select(c => c.AsUInt).ToArray();
        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        using (var r = new BinaryReader(new MemoryStream(bytes)))
        {
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                float time = r.ReadSingle();
                int keys = r.ReadInt32();
                for (int k = 0; k < keys; k++)
                {
                    int index = r.ReadInt32();
                    float a = r.ReadSingle(), b = r.ReadSingle(), c = r.ReadSingle(), d = r.ReadSingle();
                    if (index >= 0 && index < segments.Length) segments[index].Add((time, a, b, c, d));
                }
            }
        }
        float Streamed(int curve, float t)
        {
            var list = segments[curve];
            if (list.Count == 0) return 0;
            int i = list.FindLastIndex(s => s.Time <= t);
            if (i < 0) return list[0].D;
            var (time, a, b, c, d) = list[i];
            // Bornes de la piste (−∞ / +∞) : la valeur est constante
            if (!float.IsFinite(time)) return d;
            float dt = t - time;
            return ((a * dt + b) * dt + c) * dt + d;
        }

        var dense = data["m_DenseClip"];
        int frames = dense["m_FrameCount"].AsInt, denseCount = (int)dense["m_CurveCount"].AsUInt;
        float rate = dense["m_SampleRate"].AsFloat, begin = dense["m_BeginTime"].AsFloat;
        var denseArray = frames > 0 && denseCount > 0 ? dense["m_SampleArray"]["Array"].Children.Select(c => c.AsFloat).ToArray() : [];
        float Dense(int curve, float t)
        {
            float f = Math.Clamp((t - begin) * rate, 0, frames - 1);
            int f0 = (int)f, f1 = Math.Min(f0 + 1, frames - 1);
            float v0 = denseArray.ElementAtOrDefault(f0 * denseCount + curve), v1 = denseArray.ElementAtOrDefault(f1 * denseCount + curve);
            return v0 + (v1 - v0) * (f - f0);
        }
        var constant = data["m_ConstantClip"]["data"]["Array"].Children.Select(c => c.AsFloat).ToArray();

        float Value(int curve, float t) =>
            curve < streamedCount ? Streamed(curve, t)
            : curve < streamedCount + denseCount ? Dense(curve - streamedCount, t)
            : constant.ElementAtOrDefault(curve - streamedCount - denseCount);

        // Échantillonnage régulier (30 images par seconde) de chaque os
        var tracks = new List<BoneTrack>();
        float scale = Uma.UmaMeshDecoder.ExportScale;
        int count30 = Math.Max(1, (int)MathF.Ceiling(stop * SampleRate));
        var byBone = new Dictionary<string, BoneTrack>();
        foreach (var (bone, attribute, startCurve, count) in bindings)
        {
            if (bone == null || attribute is not (1 or 2 or 3)) continue;
            if (!byBone.TryGetValue(bone, out var track)) { byBone[bone] = track = new BoneTrack(bone); tracks.Add(track); }
            for (int f = 0; f <= count30; f++)
            {
                float t = Math.Min(f / SampleRate, stop);
                float V(int c) => Value(startCurve + c, t);
                switch (attribute)
                {
                    case 1: track.Positions.Add((t, new Vector3(V(0), V(1), V(2)) * scale)); break;
                    case 2: track.Rotations.Add((t, Quaternion.Normalize(new Quaternion(V(0), V(1), V(2), V(3))))); break;
                    case 3: track.Scales.Add((t, new Vector3(V(0), V(1), V(2)))); break;
                }
            }
        }
        return new EmoteClip(name, gender, stop, tracks);
    }
}
