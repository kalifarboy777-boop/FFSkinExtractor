using System.Buffers.Binary;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>Une pièce de tenue : un sous-maillage et la texture à lui appliquer.</summary>
/// <param name="IsSkin">Morceau de peau (cou, mains…) qui prend la texture du corps de base.</param>
public sealed record OutfitPart(string SlotName, UmaMesh Mesh, int SubmeshIndex, DecodedTexture? Texture, bool IsSkin);

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

    record Slot(string SlotName, long MeshPathId, int SubmeshIndex);

    /// <summary>Nom normalisé : mots significatifs, index éventuel (« top_2 » → 2) et marqueur « _N ».</summary>
    record Key(string Words, int? Index, bool HighDetail);

    static Key KeyOf(string name)
    {
        var words = new List<string>();
        int? index = null; bool hd = false;
        foreach (var w in name.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NoiseWords.Contains(w)) continue;
            if (w == "n") { hd = true; continue; }
            if (int.TryParse(w, out int i)) { index = i; continue; }
            words.Add(w);
        }
        words.Sort(StringComparer.Ordinal);
        return new Key(string.Join('_', words), index, hd);
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

    /// <param name="highDetail">Utilise les variantes « _N » (plus détaillées) quand elles existent.</param>
    /// <param name="skin">Textures de peau du corps de base, si l'APK du jeu est fourni.</param>
    public static List<Outfit> Read(GameBundle bundle, bool highDetail = false, SkinTextures? skin = null)
    {
        var objects = bundle.UmaObjects().ToList();
        var byId = objects.ToDictionary(o => o.PathId);
        var slots = objects.Where(o => o.ScriptClass == "SlotDataAsset").Select(ParseSlot).ToList();
        var overlays = objects.Where(o => o.ScriptClass == "OverlayDataAsset")
            .Select(o => (Obj: o, Key: KeyOf(o.Name))).ToList();
        var meshCache = new Dictionary<long, UmaMesh>();
        var texCache = new Dictionary<long, DecodedTexture>();
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

            bool isSkin = dedicated == null && IsSkinSubmesh(slot.MeshPathId, mesh, slot.SubmeshIndex);
            DecodedTexture? tex = isSkin ? skin?.For(gender) : Texture(dedicated ?? main);
            outfit.Parts.Add(new OutfitPart(slot.SlotName, mesh, slot.SubmeshIndex, tex, isSkin));
        }
        return outfits.Values.Where(o => o.Parts.Count > 0).ToList();

        UmaObject? FindOverlay(Key slotKey, int? index) => overlays
            .Where(o => o.Key.Words == slotKey.Words && o.Key.HighDetail == slotKey.HighDetail
                        && (index == null ? o.Key.Index is null or 1 : o.Key.Index == index))
            .Select(o => o.Obj).FirstOrDefault();

        UmaMesh Mesh(long id)
        {
            if (!meshCache.TryGetValue(id, out var mesh))
                meshCache[id] = mesh = UmaMeshDecoder.Decode(byId[id].Name, byId[id].Raw);
            return mesh;
        }

        DecodedTexture? Texture(UmaObject? overlay)
        {
            long texId = overlay == null ? 0 : FirstTexture(bundle, overlay);
            if (texId == 0) return null;
            if (!texCache.TryGetValue(texId, out var tex))
            {
                var info = bundle.GetInfo(texId);
                if (info == null) return null;
                texCache[texId] = tex = TextureDecoder.Decode(bundle, info);
            }
            return tex;
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
        p += 12 + 4 + 8;                     // material, float, 8 octets
        long mesh = BinaryPrimitives.ReadInt64LittleEndian(raw[(p + 4)..]);
        int sub = BinaryPrimitives.ReadInt32LittleEndian(raw[(p + 12)..]);
        return new Slot(slotName, mesh, sub);
    }

    static int SkipString(ReadOnlySpan<byte> raw, int p)
    {
        int len = BinaryPrimitives.ReadInt32LittleEndian(raw[p..]);
        return (p + 4 + len + 3) & ~3;
    }

    // OverlayDataAsset se termine par : textureList (int count + count × PPtr) puis PPtr material.
    // La première texture de la liste est la couleur de base.
    static long FirstTexture(GameBundle bundle, UmaObject overlay)
    {
        var raw = overlay.Raw.AsSpan();
        int end = raw.Length - 12;
        for (int count = 1; count <= 8; count++)
        {
            int at = end - 12 * count - 4;
            if (at < 32) break;
            if (BinaryPrimitives.ReadInt32LittleEndian(raw[at..]) != count) continue;
            int fileId = BinaryPrimitives.ReadInt32LittleEndian(raw[(at + 4)..]);
            long id = BinaryPrimitives.ReadInt64LittleEndian(raw[(at + 8)..]);
            if (fileId != 0) return 0; // texture rangée dans un autre bundle : pas encore géré
            var info = bundle.GetInfo(id);
            if (info != null && info.TypeId == (int)AssetsTools.NET.Extra.AssetClassID.Texture2D) return id;
        }
        return 0;
    }
}
