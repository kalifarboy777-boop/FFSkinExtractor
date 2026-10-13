using System.Buffers.Binary;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>Une pièce de tenue : un sous-maillage et les textures à lui appliquer.</summary>
/// <param name="IsSkin">Morceau de peau (cou, mains…) qui prend la texture du corps de base.</param>
/// <param name="Piece">Type de pièce : Top, Bottom, Shoe, Hair, Head, Accessory, Hands, Other.</param>
public sealed record OutfitPart(string SlotName, UmaMesh Mesh, int SubmeshIndex, TextureSet Textures, bool IsSkin,
    MaterialKind Material = MaterialKind.Opaque, string Piece = "")
{
    public DecodedTexture? Texture => Textures.BaseColor;
}

/// <summary>Options de lecture des tenues d'un bundle.</summary>
public sealed record OutfitReadOptions
{
    /// <summary>Variantes « _N » (lobby, plus détaillées) plutôt que les variantes en jeu.</summary>
    public bool HighDetail { get; init; }
    /// <summary>Utilise les overlays « _PBR » (couleur, normales, métal) quand la pièce en a.</summary>
    public bool PreferPbr { get; init; } = true;
    /// <summary>Textures de peau du corps de base, si l'APK du jeu est fourni.</summary>
    public SkinTextures? Skin { get; init; }
    /// <summary>Textures rangées dans un autre bundle : (nom CAB, pathId) → texture.</summary>
    public Func<string, long, DecodedTexture?>? External { get; init; }
}

public sealed record Outfit(string Name, string Gender, List<OutfitPart> Parts);

/// <summary>
/// Regroupe les slots UMA d'un bundle en tenues (« Male_Top_SC_Hitman » → tenue « SC_Hitman »)
/// et associe chaque slot à son maillage et à sa texture.
/// </summary>
public static partial class OutfitReader
{
    [GeneratedRegex(@"^(?<gender>male|female)_(?<rest>.+?)(?<n>_n)?(_\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotNameRegex();

    static readonly HashSet<string> PartNames = new(StringComparer.OrdinalIgnoreCase)
        { "top", "bottom", "shoe", "shoes", "feet", "head", "hair", "leg", "accessory", "mask", "face", "hand", "hands" };

    // Mots qui varient entre le nom d'un slot et celui de son overlay sans changer la pièce
    static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        { "slot", "overlay", "lobby", "ingame", "cutoff", "mesh" };

    record Slot(string SlotName, long MaterialPathId, long MeshPathId, int SubmeshIndex);

    /// <summary>Nom normalisé : mots significatifs, index éventuel (« top_2 » → 2), marqueurs « _N » et « _PBR ».</summary>
    record Key(string Words, int? Index, bool HighDetail, bool Pbr);

    static Key KeyOf(string name)
    {
        var words = new List<string>();
        int? index = null; bool hd = false, pbr = false;
        foreach (var w in name.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NoiseWords.Contains(w)) continue;
            if (w == "n") { hd = true; continue; }
            if (w == "pbr") { pbr = true; continue; }
            if (int.TryParse(w, out int i)) { index = i; continue; }
            words.Add(w);
        }
        words.Sort(StringComparer.Ordinal);
        return new Key(string.Join('_', words), index, hd, pbr);
    }

    // « Top_SC_Hitman » et « Racergirl_top » donnent tous deux la tenue sans le nom de la pièce
    static string OutfitName(string rest)
    {
        var parts = rest.Split('_').ToList();
        if (parts.Count > 1 && PartNames.Contains(parts[0])) parts.RemoveAt(0);
        else if (parts.Count > 1 && PartNames.Contains(parts[^1])) parts.RemoveAt(parts.Count - 1);
        else if (parts.Count > 2 && PartNames.Contains(parts[^2])) parts.RemoveRange(parts.Count - 2, 2);
        return string.Join('_', parts);
    }

    /// <summary>Type de pièce normalisé (« Top », « Bottom », « Shoe », « Hair », « Head », « Accessory »…).</summary>
    public static string PieceOf(string rest)
    {
        foreach (var w in rest.Split('_'))
        {
            switch (w.ToLowerInvariant())
            {
                case "top": return "Top";
                case "bottom": case "leg": return "Bottom";
                case "shoe": case "shoes": case "feet": return "Shoe";
                case "hair": return "Hair";
                case "head": case "mask": case "face": return "Head";
                case "accessory": return "Accessory";
                case "hand": case "hands": return "Hands";
            }
        }
        return "Other";
    }

    public static List<Outfit> Read(GameBundle bundle, bool highDetail = false, SkinTextures? skin = null) =>
        Read(bundle, new OutfitReadOptions { HighDetail = highDetail, Skin = skin });

    public static List<Outfit> Read(GameBundle bundle, OutfitReadOptions options)
    {
        bool highDetail = options.HighDetail;
        var objects = bundle.UmaObjects().ToList();
        var byId = objects.ToDictionary(o => o.PathId);
        var slots = objects.Where(o => o.ScriptClass == "SlotDataAsset").Select(ParseSlot).ToList();
        var overlays = objects.Where(o => o.ScriptClass == "OverlayDataAsset")
            .Select(o => (Obj: o, Key: KeyOf(o.Name))).ToList();
        var meshCache = new Dictionary<long, UmaMesh>();
        var texCache = new Dictionary<(int, long), DecodedTexture?>();
        var skinParts = new Dictionary<long, HashSet<int>>();

        var outfits = new Dictionary<string, Outfit>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots.OrderBy(s => s.SlotName, StringComparer.OrdinalIgnoreCase))
        {
            var m = SlotNameRegex().Match(slot.SlotName);
            if (!m.Success || !byId.ContainsKey(slot.MeshPathId)) continue;
            if (m.Groups["n"].Success != highDetail) continue;

            string gender = char.ToUpper(m.Groups["gender"].Value[0]) + m.Groups["gender"].Value[1..].ToLower();
            string outfitName = OutfitName(m.Groups["rest"].Value);
            string key = gender + "_" + outfitName;
            if (!outfits.TryGetValue(key, out var outfit))
                outfits[key] = outfit = new Outfit(outfitName, gender, []);

            var mesh = Mesh(slot.MeshPathId);
            var slotKey = KeyOf(slot.SlotName);
            int slotIndex = slotKey.Index ?? 0;
            // Overlay propre à ce sous-maillage (« top_ingame_1 » → « top_2_lobby_overlay »),
            // sinon l'overlay principal de la pièce.
            var dedicated = slotIndex > 0 ? FindOverlay(slotKey, slotIndex + 1) : null;
            var main = FindOverlay(slotKey, null);

            // Les slots détaillés (_N) indiquent leur shader : « DTSAvatarSkin » sur un maillage à plusieurs
            // morceaux = peau du corps. Les slots simplifiés utilisent tous le même shader : on devine
            // la peau d'après la place du morceau dans la texture.
            string material = byId.TryGetValue(slot.MaterialPathId, out var mo) ? mo.Name : "";
            bool isSkin = dedicated == null && (highDetail
                ? material.Contains("AvatarSkin") && !material.Contains("Emit") && mesh.Submeshes.Count > 1
                : IsSkinSubmesh(slot.MeshPathId, mesh, slot.SubmeshIndex));
            var textures = isSkin ? options.Skin?.For(gender) ?? TextureSet.Empty : Textures(dedicated ?? main);
            var kind = material.Contains("CutOff", StringComparison.OrdinalIgnoreCase) || material.Contains("AvatarHair") ? MaterialKind.Cutout
                : material.Contains("Glass", StringComparison.OrdinalIgnoreCase) ? MaterialKind.Transparent
                : MaterialKind.Opaque;
            outfit.Parts.Add(new OutfitPart(slot.SlotName, mesh, slot.SubmeshIndex, textures, isSkin, kind, PieceOf(m.Groups["rest"].Value)));
        }
        return outfits.Values.Where(o => o.Parts.Count > 0).ToList();

        UmaObject? FindOverlay(Key slotKey, int? index)
        {
            bool IndexMatches(Key k) => index == null ? k.Index is null or 1 : k.Index == index;
            // Les overlays « _PBR » n'ont pas de marqueur « _N » mais remplacent les overlays « _N »
            if (slotKey.HighDetail && options.PreferPbr)
            {
                var pbr = overlays.FirstOrDefault(o => o.Key.Pbr && o.Key.Words == slotKey.Words && IndexMatches(o.Key)).Obj;
                if (pbr != null) return pbr;
            }
            return overlays
                .Where(o => !o.Key.Pbr && o.Key.Words == slotKey.Words && o.Key.HighDetail == slotKey.HighDetail && IndexMatches(o.Key))
                .Select(o => o.Obj).FirstOrDefault();
        }

        UmaMesh Mesh(long id)
        {
            if (!meshCache.TryGetValue(id, out var mesh))
                meshCache[id] = mesh = UmaMeshDecoder.Decode(byId[id].Name, byId[id].Raw);
            return mesh;
        }

        TextureSet Textures(UmaObject? overlay)
        {
            if (overlay == null) return TextureSet.Empty;
            var refs = UmaAssets.OverlayTextures(overlay.Raw, p => p.FileId != 0
                || bundle.GetInfo(p.PathId)?.TypeId == (int)AssetsTools.NET.Extra.AssetClassID.Texture2D);
            return TextureSet.FromOverlay(refs.Select(Texture).ToList());
        }

        DecodedTexture? Texture(PPtr p)
        {
            if (p.IsNull) return null;
            if (texCache.TryGetValue((p.FileId, p.PathId), out var tex)) return tex;
            if (p.FileId != 0)
            {
                // Texture rangée dans un autre bundle (surtout les effets « VFX »)
                var cab = bundle.ExternalName(p.FileId);
                tex = cab == null ? null : options.External?.Invoke(cab, p.PathId);
            }
            else
            {
                var info = bundle.GetInfo(p.PathId);
                try { tex = info == null ? null : TextureDecoder.Decode(bundle, info); }
                catch (NotSupportedException) { tex = null; }
            }
            return texCache[(p.FileId, p.PathId)] = tex;
        }

        bool IsSkinSubmesh(long meshId, UmaMesh mesh, int submesh)
        {
            if (!skinParts.TryGetValue(meshId, out var set))
                skinParts[meshId] = set = UvCoverage.SkinSubmeshes(mesh);
            return set.Contains(submesh);
        }
    }

    // SlotDataAsset : m_Name, slotName, PPtr material, float, 8 octets, PPtr meshData, int submeshIndex
    static Slot ParseSlot(UmaObject o)
    {
        var raw = o.Raw.AsSpan();
        int p = 28;
        p = SkipString(raw, p);              // m_Name
        int nameLen = BinaryPrimitives.ReadInt32LittleEndian(raw[p..]);
        string slotName = System.Text.Encoding.UTF8.GetString(raw.Slice(p + 4, nameLen));
        p = SkipString(raw, p);              // slotName
        long material = BinaryPrimitives.ReadInt64LittleEndian(raw[(p + 4)..]);
        p += 12 + 4 + 8;                     // material, float, 8 octets
        long mesh = BinaryPrimitives.ReadInt64LittleEndian(raw[(p + 4)..]);
        int sub = BinaryPrimitives.ReadInt32LittleEndian(raw[(p + 12)..]);
        return new Slot(slotName, material, mesh, sub);
    }

    static int SkipString(ReadOnlySpan<byte> raw, int p)
    {
        int len = BinaryPrimitives.ReadInt32LittleEndian(raw[p..]);
        return (p + 4 + len + 3) & ~3;
    }
}
