using System.Buffers.Binary;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>Une pièce de tenue : un sous-maillage et la texture à lui appliquer.</summary>
public sealed record OutfitPart(string SlotName, UmaMesh Mesh, int SubmeshIndex, DecodedTexture? Texture);

public sealed record Outfit(string Name, string Gender, List<OutfitPart> Parts);

/// <summary>
/// Regroupe les slots UMA d'un bundle en tenues (« Male_Top_SC_Hitman » → tenue « SC_Hitman »)
/// et associe chaque slot à son maillage et à sa texture.
/// </summary>
public static partial class OutfitReader
{
    [GeneratedRegex(@"^(?<gender>male|female)_(?<part>[a-z]+)_(?<outfit>.+?)(?<n>_n)?(_\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotNameRegex();

    record Slot(string SlotName, long MeshPathId, int SubmeshIndex);

    /// <param name="highDetail">Utilise les variantes « _N » (plus détaillées) quand elles existent.</param>
    public static List<Outfit> Read(GameBundle bundle, bool highDetail = false)
    {
        var objects = bundle.UmaObjects().ToList();
        var byId = objects.ToDictionary(o => o.PathId);
        var slots = objects.Where(o => o.ScriptClass == "SlotDataAsset").Select(ParseSlot).ToList();
        var overlays = objects.Where(o => o.ScriptClass == "OverlayDataAsset").ToList();
        var meshCache = new Dictionary<long, UmaMesh>();
        var texCache = new Dictionary<long, DecodedTexture>();

        var outfits = new Dictionary<string, Outfit>(StringComparer.OrdinalIgnoreCase);
        var usedOverlays = new HashSet<long>();
        var pending = new List<(Outfit outfit, Slot slot)>();

        foreach (var slot in slots.OrderBy(s => s.SlotName, StringComparer.OrdinalIgnoreCase))
        {
            var m = SlotNameRegex().Match(slot.SlotName);
            if (!m.Success || !byId.ContainsKey(slot.MeshPathId)) continue;
            if (m.Groups["n"].Success != highDetail) continue;

            string gender = char.ToUpper(m.Groups["gender"].Value[0]) + m.Groups["gender"].Value[1..].ToLower();
            string key = gender + "_" + m.Groups["outfit"].Value;
            if (!outfits.TryGetValue(key, out var outfit))
                outfits[key] = outfit = new Outfit(m.Groups["outfit"].Value, gender, []);

            var overlay = overlays.FirstOrDefault(o =>
                string.Equals(o.Name, slot.SlotName + "_Overlay", StringComparison.OrdinalIgnoreCase));
            if (overlay == null) { pending.Add((outfit, slot)); continue; }
            usedOverlays.Add(overlay.PathId);
            outfit.Parts.Add(MakePart(slot, overlay));
        }

        // Slots sans overlay du même nom (ex. une veste en 2e sous-maillage) :
        // on leur donne, dans l'ordre, les overlays restants de la même tenue.
        foreach (var (outfit, slot) in pending)
        {
            var overlay = overlays.FirstOrDefault(o => !usedOverlays.Contains(o.PathId)
                && o.Name.Contains(outfit.Name.Split('_').Last(), StringComparison.OrdinalIgnoreCase)
                && !o.Name.Contains("hair", StringComparison.OrdinalIgnoreCase)
                && o.Name.EndsWith("_N", StringComparison.OrdinalIgnoreCase) == highDetail
                    || !usedOverlays.Contains(o.PathId) && o.Name.Contains("_N_Overlay", StringComparison.OrdinalIgnoreCase) && highDetail);
            if (overlay != null) usedOverlays.Add(overlay.PathId);
            // Sinon, on réutilise l'overlay de la même pièce (« Male_Top_X_1 » → « Male_Top_X_Overlay »)
            overlay ??= overlays.FirstOrDefault(o => string.Equals(o.Name,
                Regex.Replace(slot.SlotName, @"_\d+$", "") + "_Overlay", StringComparison.OrdinalIgnoreCase));
            outfit.Parts.Add(MakePart(slot, overlay));
        }
        return outfits.Values.Where(o => o.Parts.Count > 0).ToList();

        OutfitPart MakePart(Slot slot, UmaObject? overlay)
        {
            if (!meshCache.TryGetValue(slot.MeshPathId, out var mesh))
            {
                var mo = byId[slot.MeshPathId];
                meshCache[slot.MeshPathId] = mesh = UmaMeshDecoder.Decode(mo.Name, mo.Raw);
            }
            DecodedTexture? tex = null;
            long texId = overlay == null ? 0 : FirstTexture(bundle, overlay);
            if (texId != 0 && !texCache.TryGetValue(texId, out tex))
            {
                var info = bundle.GetInfo(texId);
                if (info != null) texCache[texId] = tex = TextureDecoder.Decode(bundle, info);
            }
            return new OutfitPart(slot.SlotName, mesh, slot.SubmeshIndex, tex);
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
