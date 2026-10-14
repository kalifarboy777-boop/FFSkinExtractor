using System.Buffers.Binary;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>Une pièce de tenue : un sous-maillage et les textures à lui appliquer.</summary>
/// <param name="IsSkin">Morceau de peau (cou, mains…) qui prend la texture du corps de base.</param>
/// <param name="Piece">Type de pièce : Top, Bottom, Shoe, Hair, Head, Accessory, Hands, Other.</param>
public sealed record OutfitPart(string SlotName, UmaMesh Mesh, int SubmeshIndex, bool IsSkin,
    MaterialKind Material = MaterialKind.Opaque, string Piece = "")
{
    public OutfitPart(string slotName, UmaMesh mesh, int submeshIndex, TextureSet textures, bool isSkin,
        MaterialKind material = MaterialKind.Opaque, string piece = "")
        : this(slotName, mesh, submeshIndex, isSkin, material, piece) => TextureSource = new(textures);

    /// <summary>Textures décodées à la première utilisation (le bundle doit encore être ouvert).</summary>
    public Lazy<TextureSet> TextureSource { get; init; } = new(TextureSet.Empty);

    public TextureSet Textures => TextureSource.Value;
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
    /// <summary>
    /// Overlay rangé dans un autre bundle : reçoit des clés (<see cref="OutfitReader.OverlayKey"/>) par ordre de préférence
    /// et renvoie les textures du premier overlay trouvé.
    /// </summary>
    public Func<IReadOnlyList<string>, TextureSet?>? ExternalOverlay { get; init; }
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
        { "top", "bottom", "shoe", "shoes", "feet", "head", "hair", "leg", "accessory", "mask", "face", "hand", "hands",
          "hat", "clothes", "hairroot", "headadditive" };

    // Mots d'overlays d'effets (animations de texture, émission) : ce ne sont pas des coloris
    static readonly string[] EffectWords = ["vfx", "fx", "scroll", "tex", "glow", "emit", "flow", "mask", "light"];

    static readonly string[] GlassWords = ["glass", "glasses", "galss"];

    // Mots qui varient entre le nom d'un slot et celui de son overlay sans changer la pièce
    static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        { "slot", "overlay", "lobby", "ingame", "cutoff", "mesh" };

    record Slot(string SlotName, long MaterialPathId, long MeshPathId, int SubmeshIndex);

    /// <summary>Nom normalisé : mots significatifs, index éventuel (« top_2 » → 2), marqueurs « _N » et « _PBR ».</summary>
    record Key(string Words, int? Index, bool HighDetail, bool Pbr);

    /// <summary>Clé de rapprochement d'un overlay (mots triés, index, détail, PBR), comparable entre bundles.</summary>
    public static string OverlayKey(string name) => KeyString(KeyOf(name));

    static string KeyString(Key k) => $"{k.Words}|{k.Index ?? 1}|{(k.HighDetail ? 1 : 0)}|{(k.Pbr ? 1 : 0)}";

    // Mots de pièce interchangeables entre le nom d'un slot et celui de son overlay
    static readonly (string From, string To)[] Synonyms =
    [
        ("hair", "hat"), ("hat", "hair"), ("shoe", "shoes"), ("shoes", "shoe"), ("feet", "shoe"), ("feet", "shoes"),
        ("clothes", "top"), ("top", "clothes"), ("leg", "bottom"), ("bottom", "body"), ("shoe", "body"), ("top", "body"),
        ("hair", "head"),   // cheveux peints dans la texture du visage (personnages)
    ];

    // La clé du slot, puis les variantes : autre niveau de détail, autre mot pour la pièce
    static IEnumerable<Key> Variants(Key k)
    {
        yield return k;
        yield return k with { HighDetail = !k.HighDetail };
        var words = k.Words.Split('_');
        foreach (var (from, to) in Synonyms)
        {
            if (!words.Contains(from)) continue;
            var w = string.Join('_', words.Select(x => x == from ? to : x).OrderBy(x => x, StringComparer.Ordinal));
            yield return k with { Words = w };
            yield return k with { Words = w, HighDetail = !k.HighDetail };
        }
    }

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
                case "head": case "mask": case "face": case "hat": return "Head";
                case "clothes": return "Top";
                case "accessory": case "accseeory": case "galss": case "glass": case "glasses": return "Accessory";
                case "hand": case "hands": return "Hands";
                // Vêtements simples nommés par leur type (« male_tshirt_mario », « female_a-skirt_… »)
                case "tshirt": case "shirt": case "sleeveless": case "hoodie": case "sweater": case "jacket": return "Top";
                case "pants": case "pant": case "a-skirt": case "skirt": case "bellbottoms": case "casualpants":
                case "ballpants": case "shorts": case "jeans": return "Bottom";
                case "boots": case "leathershoe": case "hikingshoes": case "sneakers": return "Shoe";
                case "strawhat": case "cap": return "Head";
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
        var slots = objects.Where(o => o.ScriptClass == "SlotDataAsset").Select(TryParseSlot).OfType<Slot>().ToList();
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
            // Les slots « _PBR » reprennent le maillage « _N » : on garde le slot « _N » et son overlay « _PBR »
            if (slot.SlotName.Contains("_PBR", StringComparison.OrdinalIgnoreCase)) continue;

            string gender = char.ToUpper(m.Groups["gender"].Value[0]) + m.Groups["gender"].Value[1..].ToLower();
            string outfitName = OutfitName(m.Groups["rest"].Value);
            string key = gender + "_" + outfitName;
            if (!outfits.TryGetValue(key, out var outfit))
                outfits[key] = outfit = new Outfit(outfitName, gender, []);

            UmaMesh mesh;
            try { mesh = Mesh(slot.MeshPathId); }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                continue;   // maillage d'un format inconnu : on garde le reste de la tenue
            }
            var slotKey = KeyOf(slot.SlotName);
            int slotIndex = slotKey.Index ?? 0;
            // Overlay propre à ce sous-maillage (« top_ingame_1 » → « top_2_lobby_overlay »),
            // sinon l'overlay principal de la pièce.
            var dedicated = slotIndex > 0 ? FindOverlay(slotKey, slotIndex + 1) ?? GlassOverlay(slotKey) : null;
            var main = FindAny(slotKey);
            string material0 = byId.TryGetValue(slot.MaterialPathId, out var mo0) ? mo0.Name : "";
            // Sous-maillage découpé (barbe, mèches) : overlay « CutOff » de la tenue (« Male_Accessory_CutOff_Cos_Seaman »)
            if (dedicated == null && slotIndex > 0 && material0.Contains("CutOff", StringComparison.OrdinalIgnoreCase))
                dedicated = CutOffOverlay(outfitName, gender);

            // Les slots détaillés (_N) indiquent leur shader : « DTSAvatarSkin » sur un maillage à plusieurs
            // morceaux = peau du corps. Les slots simplifiés utilisent tous le même shader : on devine
            // la peau d'après la place du morceau dans la texture.
            string material = material0;
            // Le visage d'un personnage utilise aussi le shader de peau, mais avec sa propre texture
            bool isFace = main != null && PieceOf(m.Groups["rest"].Value) == "Head"
                && m.Groups["rest"].Value.Split('_').Any(w => w.Equals("head", StringComparison.OrdinalIgnoreCase));
            bool isSkin = dedicated == null && !isFace && (highDetail
                ? material.Contains("AvatarSkin") && !material.Contains("Emit") && mesh.Submeshes.Count > 1
                : IsSkinSubmesh(slot.MeshPathId, mesh, slot.SubmeshIndex));
            var overlay = dedicated ?? main;
            // L'overlay au nom exact du slot peut être rangé dans un autre bundle (« Male_Shoe_Cos_Hip_hop2_Overlay_N ») :
            // il passe avant un overlay de ce bundle trouvé seulement par un synonyme (« Male_Body_… »)
            bool bySynonym = dedicated == null && main != null && Variants(slotKey).Take(2).All(k => FindOverlay(k, null) == null);
            var exactKeys = Variants(slotKey).Take(2).Select(KeyString).Distinct().ToList();
            var textures = isSkin ? new Lazy<TextureSet>(options.Skin?.For(gender) ?? TextureSet.Empty)
                : bySynonym && options.ExternalOverlay != null
                    ? new Lazy<TextureSet>(() => options.ExternalOverlay(exactKeys) ?? Textures(overlay))
                : overlay != null ? new Lazy<TextureSet>(() => Textures(overlay))
                : new Lazy<TextureSet>(() => options.ExternalOverlay?.Invoke(Variants(slotKey).Select(KeyString).Distinct().ToList()) ?? TextureSet.Empty);
            var kind = material.Contains("CutOff", StringComparison.OrdinalIgnoreCase) || material.Contains("AvatarHair") ? MaterialKind.Cutout
                : material.Contains("Glass", StringComparison.OrdinalIgnoreCase) ? MaterialKind.Transparent
                : MaterialKind.Opaque;
            outfit.Parts.Add(new OutfitPart(slot.SlotName, mesh, slot.SubmeshIndex, isSkin, kind, PieceOf(m.Groups["rest"].Value))
                { TextureSource = textures });
        }
        AddColorVariants();
        return outfits.Values.Where(o => o.Parts.Count > 0).ToList();

        // Variantes de couleur : le jeu habille souvent les maillages d'une tenue (« Cos_Cobra_Red ») avec
        // les overlays d'autres coloris (« Cos_Cobra_Blue ») qui n'ont pas de slot à eux.
        void AddColorVariants()
        {
            foreach (var outfit in outfits.Values.ToList())
            {
                var nameWords = outfit.Name.ToLowerInvariant().Split('_').ToHashSet();
                var variants = new Dictionary<string, (string From, string To)>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in outfit.Parts.Where(p => !p.IsSkin))
                {
                    var words = KeyOf(part.SlotName).Words.Split('_');
                    foreach (var (o, k) in overlays)
                    {
                        var other = k.Words.Split('_');
                        if (other.Length != words.Length) continue;
                        var removed = words.Except(other).ToList();
                        var added = other.Except(words).ToList();
                        if (removed.Count != 1 || added.Count != 1 || !nameWords.Contains(removed[0])) continue;
                        if (PartNames.Contains(removed[0]) || PartNames.Contains(added[0])
                            || EffectWords.Any(added[0].Contains) || added[0].All(char.IsDigit)) continue;
                        // Le mot tel qu'écrit dans le nom de l'overlay (« Blue »)
                        string to = o.Name.Split('_').First(w => w.Equals(added[0], StringComparison.OrdinalIgnoreCase));
                        string variantName = string.Join('_', outfit.Name.Split('_')
                            .Select(w => w.Equals(removed[0], StringComparison.OrdinalIgnoreCase) ? to : w));
                        variants.TryAdd(variantName, (removed[0], added[0]));
                    }
                }
                foreach (var (variantName, (from, to)) in variants)
                {
                    string key = outfit.Gender + "_" + variantName;
                    if (outfits.ContainsKey(key)) continue;
                    var parts = outfit.Parts.Select(part =>
                    {
                        if (part.IsSkin) return part;
                        var k = KeyOf(part.SlotName);
                        var words = k.Words.Split('_').Select(w => w == from ? to : w).OrderBy(w => w, StringComparer.Ordinal);
                        var vk = k with { Words = string.Join('_', words) };
                        var overlay = ResolveOverlay(vk);
                        return overlay == null ? part : part with { TextureSource = new(() => Textures(overlay)) };
                    }).ToList();
                    outfits[key] = new Outfit(variantName, outfit.Gender, parts);
                }
            }
        }

        UmaObject? ResolveOverlay(Key slotKey)
        {
            int slotIndex = slotKey.Index ?? 0;
            return (slotIndex > 0 ? FindOverlay(slotKey, slotIndex + 1) ?? GlassOverlay(slotKey) : null) ?? FindAny(slotKey);
        }

        UmaObject? FindAny(Key slotKey)
        {
            foreach (var k in Variants(slotKey))
                if (FindOverlay(k, null) is { } o) return o;
            return null;
        }

        // Second morceau en verre (visière, lunettes) : overlay au nom du slot plus « Glass »
        // (« Male_Top_Cos_Fishtank_N_1 » → « Male_Top_Cos_Glass_Fishtank_Overlay_N »)
        UmaObject? GlassOverlay(Key slotKey)
        {
            foreach (var glass in GlassWords)
            {
                var words = string.Join('_', slotKey.Words.Split('_').Append(glass).OrderBy(w => w, StringComparer.Ordinal));
                if (FindOverlay(slotKey with { Words = words, Index = null }, null) is { } o) return o;
            }
            return null;
        }

        UmaObject? CutOffOverlay(string outfitName, string gender)
        {
            var key = outfitName.ToLowerInvariant().Split('_').Where(w => w is not ("sc" or "cos")).ToList();
            return overlays.Select(o => o.Obj).FirstOrDefault(o => o.Name.Contains("CutOff", StringComparison.OrdinalIgnoreCase)
                && o.Name.StartsWith(gender, StringComparison.OrdinalIgnoreCase)
                && key.All(w => o.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

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
            lock (texCache)
            {
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
        }

        bool IsSkinSubmesh(long meshId, UmaMesh mesh, int submesh)
        {
            if (!skinParts.TryGetValue(meshId, out var set))
                skinParts[meshId] = set = UvCoverage.SkinSubmeshes(mesh);
            return set.Contains(submesh);
        }
    }

    static Slot? TryParseSlot(UmaObject o)
    {
        try { return ParseSlot(o); }
        catch (ArgumentOutOfRangeException) { return null; }   // slot d'un format inconnu
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
