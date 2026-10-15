using System.Text.RegularExpressions;
using System.Numerics;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FFSkinExtractor.Core.Animation;

/// <summary>Un objet de l'effet d'une emote, transformation locale en repère Unity (unités du jeu).</summary>
public sealed class VfxNode
{
    public required string Name { get; init; }
    public int Parent { get; init; } = -1;
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
    public bool Active { get; init; } = true;
    /// <summary>Os du personnage auquel l'objet est accroché (« Root », « RightHand »…), ou null.</summary>
    public string? AttachBone { get; set; }
    /// <summary>Animation de l'objet (positions en unités du jeu), ou null.</summary>
    public BoneTrack? Track { get; set; }
    /// <summary>Instants où l'objet apparaît ou disparaît, ou null s'il ne change pas.</summary>
    public List<(float Time, bool Active)>? ActiveKeys { get; set; }
    /// <summary>Rendu (maillage) activé au départ ; faux pour un rendu que l'animation allume plus tard.</summary>
    public bool RendererEnabled { get; set; } = true;
    /// <summary>Instants où le rendu de l'objet s'allume ou s'éteint, ou null.</summary>
    public List<(float Time, bool Active)>? EnabledKeys { get; set; }
}

public enum VfxBlend { Opaque, Cutout, Alpha, Additive }

public sealed record VfxMaterial(string Name, DecodedTexture? Texture, Vector4 Color, VfxBlend Blend, Vector2 Tiling, Vector2 Offset);

/// <summary>Maillage d'un objet de l'effet ; <see cref="Bones"/> donne les nœuds du squelette s'il est animé.</summary>
public sealed record VfxMesh(int Node, UnityMesh Mesh, VfxMaterial?[] Materials, int[]? Bones);

/// <summary>Système de particules : réglages du jeu (modules Unity, en JSON) et rendu.</summary>
public sealed record VfxEmitter(int Node, JsonObject System, JsonObject Renderer, VfxMaterial? Material, UnityMesh? Mesh);

/// <summary>Effet visuel d'une emote : objets (cheval, pingouin…), leurs animations et leurs particules.</summary>
public sealed class EmoteVfx
{
    public required string Name { get; init; }
    public List<VfxNode> Nodes { get; } = [];
    public List<VfxMesh> Meshes { get; } = [];
    public List<VfxEmitter> Emitters { get; } = [];
}

/// <summary>
/// Retrouve l'effet d'une emote (« FF_VFX_Emote_Male_Penguin » pour « Male_Emote_Penguin ») dans le bundle
/// de son animation et le lit : hiérarchie, maillages, matériaux, animations et systèmes de particules.
/// </summary>
public static class EmoteVfxReader
{
    static readonly HashSet<string> ClipNoise = new(StringComparer.OrdinalIgnoreCase)
        { "male", "female", "emote", "ani", "anim", "groupanim", "preview", "animation", "ingame", "superemote", "inter", "cos", "other" };
    static readonly HashSet<string> VfxNoise = new(StringComparer.OrdinalIgnoreCase)
        { "ff", "vfx", "emote", "groupanim", "superemote", "male", "female", "cos", "lobby", "mvp", "ingame", "ani", "preview", "animation" };

    static List<string> Tokens(string name, HashSet<string> noise) =>
        name.Split('_', ' ', '-').Where(t => t.Length > 0 && !noise.Contains(t)).Select(t => t.ToLowerInvariant()).ToList();

    /// <summary>Effet de l'emote dont le clip est <paramref name="clipName"/>, ou null s'il n'y en a pas.</summary>
    /// <param name="resolve">Ouvre le bundle d'un autre fichier (« cab-… ») : les textures des effets sont partagées.</param>
    public static EmoteVfx? Read(GameBundle bundle, string clipName, string gender, Func<string, GameBundle?>? resolve = null)
    {
        var clipTokens = Tokens(clipName, ClipNoise);
        if (clipTokens.Count == 0) return null;
        // Racines de la scène : transforms sans parent dont l'objet s'appelle « FF_VFX… »
        (AssetTypeValueField Transform, string Name, int Score)? best = null;
        foreach (var info in bundle.OfType(AssetClassID.Transform))
        {
            AssetTypeValueField t;
            try { t = bundle.BaseField(info); } catch (Exception) { continue; }
            if (t["m_Father"]["m_PathID"].AsLong != 0) continue;
            string name = ObjectName(bundle, t["m_GameObject"]);
            if (!name.StartsWith("FF_VFX", StringComparison.OrdinalIgnoreCase)) continue;
            var tokens = Tokens(name, VfxNoise);
            if (tokens.Count == 0 || !tokens.All(clipTokens.Contains)) continue;
            string? g = EmoteReader.GenderOf(name);
            int score = tokens.Count * 10 - (g != null && g != gender ? 50 : 0)
                - (name.Contains("lobby", StringComparison.OrdinalIgnoreCase) ? 3 : 0)
                - (name.Contains("mvp", StringComparison.OrdinalIgnoreCase) ? 4 : 0) - name.Length / 20;
            if (best == null || score > best.Value.Score) best = (t, name, score);
        }
        if (best is not { } root) return null;
        var vfx = new EmoteVfx { Name = root.Name };
        new Builder(bundle, vfx, resolve).Walk(root.Transform, -1);
        return vfx;
    }

    static string ObjectName(GameBundle bundle, AssetTypeValueField pptr)
    {
        if (pptr["m_FileID"].AsInt != 0 || bundle.GetInfo(pptr["m_PathID"].AsLong) is not { } info) return "";
        try { return bundle.BaseField(info)["m_Name"].AsString; } catch (Exception) { return ""; }
    }

    sealed class Builder(GameBundle bundle, EmoteVfx vfx, Func<string, GameBundle?>? resolve)
    {
        readonly Dictionary<long, int> _nodeByTransform = [];
        readonly List<(int Node, AssetTypeValueField Animator)> _animators = [];
        readonly Dictionary<long, VfxMaterial?> _materials = [];
        readonly Dictionary<(int, long), DecodedTexture?> _textures = [];
        readonly Dictionary<long, UnityMesh?> _meshes = [];
        readonly List<(int Node, AssetTypeValueField Renderer, bool Skinned, AssetTypeValueField? Filter)> _renderers = [];

        AssetTypeValueField? Get(AssetTypeValueField pptr, out AssetFileInfo? info)
        {
            info = null;
            if (pptr.IsDummy || pptr["m_FileID"].AsInt != 0 || pptr["m_PathID"].AsLong == 0) return null;
            info = bundle.GetInfo(pptr["m_PathID"].AsLong);
            if (info == null) return null;
            try { return info.TypeId == (int)AssetClassID.AnimationClip ? bundle.BaseFieldWithout(info, "m_16BitCurves") : bundle.BaseField(info); }
            catch (Exception) { return null; }
        }

        public void Walk(AssetTypeValueField transform, int parent)
        {
            Walk(transform, parent, 0);
            // Animations : chaque Animator anime les objets placés sous lui (chemins relatifs à son objet)
            foreach (var (node, animator) in _animators) Animate(node, animator);
            foreach (var r in _renderers) AddRenderer(r.Node, r.Renderer, r.Skinned, r.Filter);
        }

        void Walk(AssetTypeValueField t, int parent, int depth)
        {
            if (depth > 40) return;
            var go = Get(t["m_GameObject"], out _);
            var p = t["m_LocalPosition"];
            var q = t["m_LocalRotation"];
            var s = t["m_LocalScale"];
            int index = vfx.Nodes.Count;
            vfx.Nodes.Add(new VfxNode
            {
                Name = go?["m_Name"].AsString ?? "",
                Parent = parent,
                Position = new Vector3(p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat),
                Rotation = Quaternion.Normalize(new Quaternion(q["x"].AsFloat, q["y"].AsFloat, q["z"].AsFloat, q["w"].AsFloat)),
                Scale = new Vector3(s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat),
                Active = go == null || go["m_IsActive"].AsBool,
            });
            if (go != null)
            {
                AssetTypeValueField? filter = null, meshRenderer = null, psRenderer = null, ps = null;
                foreach (var c in go["m_Component"]["Array"].Children)
                {
                    var comp = Get(c["component"], out var info);
                    if (comp == null || info == null) continue;
                    switch ((AssetClassID)info.TypeId)
                    {
                        case AssetClassID.Animator: _animators.Add((index, comp)); break;
                        case AssetClassID.MeshFilter: filter = comp; break;
                        case AssetClassID.MeshRenderer: meshRenderer = comp; break;
                        case AssetClassID.SkinnedMeshRenderer: _renderers.Add((index, comp, true, null)); break;
                        case AssetClassID.ParticleSystem: ps = comp; break;
                        case AssetClassID.ParticleSystemRenderer: psRenderer = comp; break;
                        case AssetClassID.MonoBehaviour:
                            // « BoneNameRef » : l'objet suit un os du personnage
                            if (!comp["BoneName"].IsDummy && comp["BoneName"].AsString is { Length: > 0 } bone)
                                vfx.Nodes[index].AttachBone = bone;
                            break;
                    }
                }
                if (meshRenderer != null && filter != null) _renderers.Add((index, meshRenderer, false, filter));
                if (ps != null && psRenderer != null) AddEmitter(index, ps, psRenderer);
            }
            _nodeByTransform[t["m_GameObject"]["m_PathID"].AsLong] = index;
            foreach (var child in t["m_Children"]["Array"].Children)
                if (Get(child, out _) is { } ct) Walk(ct, index, depth + 1);
        }

        // Chemins (« bone_Root/Bip001 ») des objets sous un nœud, par leur CRC32
        Dictionary<uint, int> PathsUnder(int root)
        {
            var result = new Dictionary<uint, int> { [Rig.BoneHash.Of("")] = root };
            var paths = new Dictionary<int, string> { [root] = "" };
            for (int i = root + 1; i < vfx.Nodes.Count; i++)
            {
                int parent = vfx.Nodes[i].Parent;
                if (!paths.TryGetValue(parent, out var pp)) continue;
                string path = pp.Length == 0 ? vfx.Nodes[i].Name : pp + "/" + vfx.Nodes[i].Name;
                paths[i] = path;
                result.TryAdd(Rig.BoneHash.Of(path), i);
            }
            return result;
        }

        List<AssetTypeValueField> ControllerClips(AssetTypeValueField animator)
        {
            var clips = new List<AssetTypeValueField>();
            var ctrl = Get(animator["m_Controller"], out var info);
            if (ctrl == null || info == null) return clips;
            if (info.TypeId == (int)AssetClassID.AnimatorOverrideController)
            {
                foreach (var pair in ctrl["m_Clips"]["Array"].Children)
                {
                    var c = Get(pair["m_OverrideClip"], out _) ?? Get(pair["m_OriginalClip"], out _);
                    if (c != null) clips.Add(c);
                }
                if (clips.Count == 0 && Get(ctrl["m_Controller"], out _) is { } baseCtrl) ctrl = baseCtrl; else return clips;
            }
            foreach (var pptr in ctrl["m_AnimationClips"]["Array"].Children)
                if (Get(pptr, out _) is { } c) clips.Add(c);
            return clips;
        }

        List<AssetTypeValueField>? _allClips;
        // Sans contrôleur (effets pilotés par une Timeline) : les clips du bundle qui visent ces objets
        List<AssetTypeValueField> AllClips() => _allClips ??= bundle.OfType(AssetClassID.AnimationClip)
            .Where(i => !EmoteReader.LooksLikeCharacterClip(bundle.ReadName(i)))
            .Select(i => { try { return bundle.BaseFieldWithout(i, "m_16BitCurves"); } catch (Exception) { return null; } })
            .OfType<AssetTypeValueField>().ToList();

        static IEnumerable<uint> BindingPaths(AssetTypeValueField clip) =>
            clip["m_ClipBindingConstant"]["genericBindings"]["Array"].Children.Where(b => b["typeID"].AsInt is 1 or 4 or 23 or 137 or 199).Select(b => b["path"].AsUInt);

        void Animate(int node, AssetTypeValueField animator)
        {
            var paths = PathsUnder(node);
            var candidates = ControllerClips(animator);
            bool fromController = candidates.Count > 0;
            if (!fromController) candidates = AllClips();
            AssetTypeValueField? clip = null;
            int bestHits = 0;
            float bestLength = 0;
            foreach (var c in candidates)
            {
                var targets = BindingPaths(c).Distinct().ToList();
                if (targets.Count == 0) continue;
                int hits = targets.Count(paths.ContainsKey);
                // Hors contrôleur, le clip doit viser surtout ces objets
                if (!fromController && (hits < 1 || hits * 2 < targets.Count)) continue;
                float length = c["m_MuscleClip"]["m_StopTime"].AsFloat;
                if (hits > bestHits || (hits == bestHits && length > bestLength)) { clip = c; bestHits = hits; bestLength = length; }
            }
            if (clip == null || bestHits == 0) return;
            var sampler = new ClipSampler(clip);
            foreach (var b in sampler.Bindings)
            {
                if (!paths.TryGetValue(b.Path, out int target)) continue;
                var n = vfx.Nodes[target];
                if (b.Type == 4 && b.Attribute is >= 1 and <= 4)
                {
                    n.Track ??= new BoneTrack(n.Name);
                    if ((b.Attribute == 1 && n.Track.Positions.Count > 0) || (b.Attribute is 2 or 4 && n.Track.Rotations.Count > 0)
                        || (b.Attribute == 3 && n.Track.Scales.Count > 0)) continue;
                    sampler.SampleTransform(b, n.Track, 1f);
                }
                else if (b.Type == 1 && b.Attribute == ClipSampler.ActiveAttribute && n.ActiveKeys == null)
                    n.ActiveKeys = sampler.Times().Select(t => (t, sampler.Value(b.Start, t) > 0.5f)).ToList();
                else if (b.Type is 23 or 137 or 199 && b.Attribute == ClipSampler.EnabledAttribute && n.EnabledKeys == null)
                    n.EnabledKeys = sampler.Times().Select(t => (t, sampler.Value(b.Start, t) > 0.5f)).ToList();
            }
        }

        void AddRenderer(int node, AssetTypeValueField renderer, bool skinned, AssetTypeValueField? filter)
        {
            bool enabled = renderer["m_Enabled"].IsDummy || renderer["m_Enabled"].AsBool;
            // Rendu éteint au départ : gardé seulement si l'animation l'allume
            if (!enabled && vfx.Nodes[node].EnabledKeys == null) return;
            vfx.Nodes[node].RendererEnabled = enabled;
            var meshPtr = skinned ? renderer["m_Mesh"] : filter!["m_Mesh"];
            var mesh = Mesh(meshPtr);
            if (mesh == null || mesh.Positions.Length == 0) return;
            var materials = renderer["m_Materials"]["Array"].Children.Select(Material).ToArray();
            int[]? bones = null;
            if (skinned && mesh.Skin != null)
                bones = renderer["m_Bones"]["Array"].Children
                    .Select(b => _nodeByTransformOf(b)).ToArray();
            vfx.Meshes.Add(new VfxMesh(node, mesh, materials, bones));
        }

        // Os d'un SkinnedMeshRenderer : PPtr vers un Transform → nœud (par son GameObject)
        int _nodeByTransformOf(AssetTypeValueField pptr)
        {
            var t = Get(pptr, out _);
            if (t == null) return -1;
            return _nodeByTransform.GetValueOrDefault(t["m_GameObject"]["m_PathID"].AsLong, -1);
        }

        UnityMesh? Mesh(AssetTypeValueField pptr)
        {
            long id = pptr["m_PathID"].AsLong;
            if (_meshes.TryGetValue(id, out var cached)) return cached;
            UnityMesh? mesh = null;
            try { if (Get(pptr, out _) is { } m) mesh = UnityMeshReader.Read(bundle, m); }
            catch (Exception) { /* maillage illisible */ }
            return _meshes[id] = mesh;
        }

        VfxMaterial? Material(AssetTypeValueField pptr)
        {
            long id = pptr["m_PathID"].AsLong;
            if (_materials.TryGetValue(id, out var cached)) return cached;
            VfxMaterial? result = null;
            if (Get(pptr, out _) is { } m)
            {
                var props = m["m_SavedProperties"];
                var floats = props["m_Floats"]["Array"].Children.ToDictionary(f => f["first"].AsString, f => f["second"].AsFloat);
                var colors = props["m_Colors"]["Array"].Children.ToDictionary(f => f["first"].AsString,
                    f => new Vector4(f["second"]["r"].AsFloat, f["second"]["g"].AsFloat, f["second"]["b"].AsFloat, f["second"]["a"].AsFloat));
                DecodedTexture? texture = null;
                Vector2 tiling = Vector2.One, offset = Vector2.Zero;
                // Texture principale (« _MainTex », « _Tex ») ; pas le bruit de dissolution ni le masque
                foreach (var env in props["m_TexEnvs"]["Array"].Children
                    .Where(e => TexturePriority(e["first"].AsString) < 9).OrderBy(e => TexturePriority(e["first"].AsString)))
                {
                    var tex = env["second"]["m_Texture"];
                    if (tex["m_PathID"].AsLong == 0) continue;
                    texture = Texture(tex);
                    if (texture == null) continue;
                    tiling = new Vector2(env["second"]["m_Scale"]["x"].AsFloat, env["second"]["m_Scale"]["y"].AsFloat);
                    offset = new Vector2(env["second"]["m_Offset"]["x"].AsFloat, env["second"]["m_Offset"]["y"].AsFloat);
                    break;
                }
                string name = m["m_Name"].AsString;
                // Masque du shader d'effets du jeu (« _Mask », canal « _MaskChannel ») : il découpe la texture
                bool maskOff = m["m_ShaderKeywords"].IsDummy is false && m["m_ShaderKeywords"].AsString.Contains("_BYPASS_MASKTEX_ON");
                if (texture?.Pixels != null && !maskOff
                    && props["m_TexEnvs"]["Array"].Children.FirstOrDefault(e => e["first"].AsString == "_Mask") is { } maskEnv
                    && maskEnv["second"]["m_Texture"]["m_PathID"].AsLong != 0 && Texture(maskEnv["second"]["m_Texture"]) is { Pixels: not null } mask)
                    texture = Masked(texture, mask, (int)floats.GetValueOrDefault("_MaskChannel"));
                // Les shaders du jeu sont dans un bundle chiffré : le mélange se lit dans les réglages du matériau
                // (facteur de destination : 1 = « One », additif ; 10 = « OneMinusSrcAlpha », transparent), sinon dans le nom
                float? dstFactor = floats.TryGetValue("_DstBlend", out var dst) ? dst
                    : floats.TryGetValue("_DestAlpha", out var da) ? da : floats.TryGetValue("_AlphaDst", out var ad) ? ad : null;
                var blend = dstFactor is { } f ? f switch { 1 => VfxBlend.Additive, 0 => VfxBlend.Opaque, _ => VfxBlend.Alpha }
                    : name.Contains("add", StringComparison.OrdinalIgnoreCase) ? VfxBlend.Additive
                    : name.Contains("blend", StringComparison.OrdinalIgnoreCase) || name.Contains("alpha", StringComparison.OrdinalIgnoreCase) ? VfxBlend.Alpha
                    : colors.ContainsKey("_TintColor") ? VfxBlend.Additive // « Particles/Additive » et ses copies
                    : floats.ContainsKey("_Cutoff") ? VfxBlend.Cutout : VfxBlend.Opaque;
                var color = colors.TryGetValue("_TintColor", out var tint) ? tint * new Vector4(2, 2, 2, 2)
                    : colors.TryGetValue("_Color", out var c) ? c
                    : colors.TryGetValue("_MainColor", out var mc) ? mc
                    : colors.TryGetValue("_Color_Tex", out var ct) ? ct
                    : colors.TryGetValue("_MainTex_Color", out var mtc) ? mtc : Vector4.One;
                result = new VfxMaterial(name, texture, Vector4.Clamp(color, Vector4.Zero, new Vector4(8)), blend, tiling, offset);
            }
            return _materials[id] = result;
        }

        readonly Dictionary<(DecodedTexture, DecodedTexture, int), DecodedTexture> _masked = [];

        // Texture multipliée par un canal du masque (R, G, B ou A), mis à sa taille
        DecodedTexture Masked(DecodedTexture texture, DecodedTexture mask, int channel)
        {
            if (_masked.TryGetValue((texture, mask, channel), out var cached)) return cached;
            int w = texture.Width, h = texture.Height;
            var src = texture.Pixels!;
            var px = new byte[src.Length];
            int offset = channel switch { 1 => 1, 2 => 0, 3 => 3, _ => 2 }; // pixels BGRA
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    int mx = x * mask.Width / w, my = y * mask.Height / h;
                    float k = mask.Pixels![(my * mask.Width + mx) * 4 + offset] / 255f;
                    px[i] = (byte)(src[i] * k); px[i + 1] = (byte)(src[i + 1] * k); px[i + 2] = (byte)(src[i + 2] * k);
                    px[i + 3] = (byte)(src[i + 3] * k);
                }
            return _masked[(texture, mask, channel)] = DecodedTexture.FromBgra(texture.Name + "_" + mask.Name, w, h, px);
        }

        static int TexturePriority(string name) => name switch
        {
            "_MainTex" => 0, "_Tex" => 1, "_BaseMap" or "_MainTexture" or "_Albedo" => 2,
            _ when Regex.IsMatch(name, "dis|noise|nosie|mask|flow|normal|bump|ramp|matcap", RegexOptions.IgnoreCase) => 9,
            _ => 5,
        };

        DecodedTexture? Texture(AssetTypeValueField pptr)
        {
            int file = pptr["m_FileID"].AsInt;
            long pathId = pptr["m_PathID"].AsLong;
            var key = (file, pathId);
            if (_textures.TryGetValue(key, out var cached)) return cached;
            DecodedTexture? texture = null;
            try
            {
                // Texture rangée dans un bundle commun aux effets
                var source = file == 0 ? bundle : bundle.ExternalCab(file) is { } cab ? resolve?.Invoke(cab) : null;
                if (source?.GetInfo(pathId) is { } info && info.TypeId == (int)AssetClassID.Texture2D)
                    texture = TextureDecoder.Decode(source, info);
            }
            catch (Exception) { /* format de texture non géré */ }
            return _textures[key] = texture;
        }

        // Modules du système de particules que l'aperçu sait jouer
        static readonly string[] Modules = ["InitialModule", "ShapeModule", "EmissionModule", "SizeModule", "RotationModule",
            "ColorModule", "UVModule", "VelocityModule", "ForceModule", "LimitVelocity"];

        void AddEmitter(int node, AssetTypeValueField ps, AssetTypeValueField renderer)
        {
            if (!renderer["m_Enabled"].IsDummy && !renderer["m_Enabled"].AsBool) return;
            var system = new JsonObject();
            foreach (var child in ps.Children)
            {
                string name = child.FieldName;
                if (child.Children.Count == 0 || name == "startDelay" || Modules.Contains(name))
                    if (name is not ("m_GameObject" or "m_Name")) system[name] = ToJson(child, 0);
            }
            var r = new JsonObject();
            foreach (var name in new[] { "m_RenderMode", "m_LengthScale", "m_VelocityScale", "m_CameraVelocityScale", "m_MinParticleSize", "m_MaxParticleSize", "m_RenderAlignment", "m_SortingFudge" })
                if (!renderer[name].IsDummy) r[name] = ToJson(renderer[name], 0);
            var material = renderer["m_Materials"]["Array"].Children.Select(Material).FirstOrDefault(m => m != null);
            UnityMesh? mesh = null;
            if (!renderer["m_RenderMode"].IsDummy && renderer["m_RenderMode"].AsInt == 4 && !renderer["m_Mesh"].IsDummy)
                mesh = Mesh(renderer["m_Mesh"]);
            vfx.Emitters.Add(new VfxEmitter(node, system, r, material, mesh));
        }

        static JsonNode? ToJson(AssetTypeValueField f, int depth)
        {
            if (depth > 12) return null;
            if (f.FieldName == "Array" || f.TemplateField.IsArray)
            {
                var arr = new JsonArray();
                foreach (var c in f.Children) arr.Add(ToJson(c, depth + 1));
                return arr;
            }
            if (f.Children.Count == 0)
            {
                var v = f.Value;
                if (v == null) return null;
                try
                {
                return v.ValueType switch
                {
                    AssetValueType.Bool => JsonValue.Create(f.AsBool),
                    AssetValueType.Float or AssetValueType.Double => JsonValue.Create(f.AsFloat is var x && float.IsFinite(x) ? x : 0),
                    AssetValueType.String => JsonValue.Create(f.AsString),
                    AssetValueType.Int64 or AssetValueType.UInt64 => JsonValue.Create(f.AsLong),
                    AssetValueType.ByteArray => null,
                    AssetValueType.Int8 or AssetValueType.UInt8 or AssetValueType.Int16 or AssetValueType.UInt16
                        or AssetValueType.Int32 or AssetValueType.UInt32 => JsonValue.Create(f.AsLong),
                    _ => null,
                };
                }
                catch (Exception) { return null; }
            }
            var obj = new JsonObject();
            foreach (var c in f.Children)
                obj[c.FieldName] = c.FieldName == "Array" || (c.Children.Count == 1 && c.Children[0].FieldName == "Array")
                    ? ToJson(c.FieldName == "Array" ? c : c.Children[0], depth + 1)
                    : ToJson(c, depth + 1);
            return obj;
        }
    }
}
