using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFSkinExtractor.Core.Items;

/// <summary>Un objet du jeu (vêtement ou personnage), tel que listé par ItemID2.</summary>
public sealed record GameItem(long Id, string Name, string Icon, string Rarity, string Type, string Piece, string? Gender)
{
    /// <summary>Icône hébergée par ff-resources (le dépôt d'images utilisé par ItemID2).</summary>
    public string IconUrl => $"https://raw.githubusercontent.com/0xme/ff-resources/refs/heads/main/pngs/300x300/{Icon}.png";
}

/// <summary>Un objet du catalogue rapproché d'une pièce de tenue extraite.</summary>
public sealed record ItemMatch(GameItem Item, int Score);

/// <summary>
/// Catalogue des ID d'objets, d'après la base ItemID2 (https://github.com/0xme/ItemID2, par Crystal Person).
/// Les fichiers de configuration du jeu sont chiffrés ; on relie donc les tenues aux objets par leurs noms :
/// l'icône d'un objet reprend le nom interne de la tenue (« Icon_avatar_female_sc_top_nanoscientist »
/// pour la tenue « Sc_Nanoscientist »), et le début de l'ID donne la pièce (203 = haut, 204 = bas…).
/// </summary>
public sealed class ItemCatalog
{
    public const string SourceUrl = "https://raw.githubusercontent.com/0xme/ItemID2/main/assets/itemData.json";
    public const string Credit = "ID et noms d'objets : ItemID2 par Crystal Person (github.com/0xme/ItemID2)";

    readonly Dictionary<long, GameItem> _byId = [];
    readonly List<(GameItem Item, HashSet<string> Tokens)> _indexed = [];

    readonly List<(GameItem Item, HashSet<string> Tokens)> _bundles = [];

    public IReadOnlyCollection<GameItem> Items => _byId.Values;

    /// <summary>Bundles de la boutique (ensembles de vêtements), avec leur icône.</summary>
    public IEnumerable<GameItem> Bundles => _bundles.Select(b => b.Item);

    // Mots des icônes qui ne disent rien de la tenue
    static readonly HashSet<string> IconNoise = new(StringComparer.Ordinal)
        { "icon", "avatar", "male", "female", "face", "head", "ui", "ff" };
    // Mots des noms de tenue trop courants pour identifier une tenue à eux seuls
    static readonly HashSet<string> WeakWords = new(StringComparer.Ordinal) { "sc", "cos", "n", "lobby", "ingame" };

    internal sealed record RawItem(
        [property: JsonPropertyName("itemID")] string ItemId,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("Rare")] string? Rare,
        [property: JsonPropertyName("itemType")] string? ItemType);

    public static ItemCatalog Parse(Stream json)
    {
        var raw = JsonSerializer.Deserialize(json, CoreJson.Default.ListRawItem) ?? [];
        var catalog = new ItemCatalog();
        foreach (var r in raw)
        {
            if (r.ItemType is not ("CLOTHES" or "AVATAR" or "BUNDLE") || !long.TryParse(r.ItemId, out long id)) continue;
            if (string.IsNullOrEmpty(r.Icon) || r.Icon == "NONE") continue;
            var tokens = Tokens(r.Icon);
            string? gender = tokens.Contains("female") ? "Female" : tokens.Contains("male") ? "Male" : null;
            if (r.ItemType == "BUNDLE")
            {
                // Les caisses génériques (« Icon_box_3 ») ne disent rien de leur contenu
                if (r.Description is null or "NONE" || tokens.Contains("box")) continue;
                var bundle = new GameItem(id, r.Description, r.Icon, r.Rare ?? "NONE", "BUNDLE", "Bundle", gender);
                catalog._byId[id] = bundle;
                catalog._bundles.Add((bundle, BundleTokens(tokens)));
                continue;
            }
            var item = new GameItem(id, r.Description is null or "NONE" ? r.Icon : r.Description, r.Icon,
                r.Rare ?? "NONE", r.ItemType, PieceOf(id, tokens), gender);
            catalog._byId[id] = item;
            catalog._indexed.Add((item, tokens));
        }
        return catalog;
    }

    public static ItemCatalog Load(string path)
    {
        using var s = File.OpenRead(path);
        return Parse(s);
    }

    /// <summary>
    /// Charge le catalogue depuis <paramref name="cachePath"/>, ou le télécharge depuis ItemID2
    /// s'il manque ou date de plus de <paramref name="maxAge"/>.
    /// </summary>
    public static async Task<ItemCatalog> LoadOrDownloadAsync(string cachePath, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var info = new FileInfo(cachePath);
        bool fresh = info.Exists && info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc < (maxAge ?? TimeSpan.FromDays(7));
        if (!fresh)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("FFSkinExtractor");
                var bytes = await http.GetByteArrayAsync(SourceUrl, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
                await File.WriteAllBytesAsync(cachePath, bytes, ct);
            }
            catch (Exception) when (info.Exists && info.Length > 0)
            {
                // Hors ligne : l'ancienne copie fera l'affaire
            }
        }
        return Load(cachePath);
    }

    public GameItem? Get(long id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Objets correspondant à une pièce de tenue, meilleurs d'abord.
    /// <paramref name="piece"/> vide = toutes les pièces de la tenue.
    /// </summary>
    public List<ItemMatch> Match(string gender, string outfitName, string piece = "")
    {
        var words = Tokens(outfitName);
        var key = words.Where(w => !WeakWords.Contains(w)).ToList();
        if (key.Count == 0) return [];
        var result = new List<ItemMatch>();
        foreach (var (item, tokens) in _indexed)
        {
            if (item.Gender != null && !item.Gender.Equals(gender, StringComparison.OrdinalIgnoreCase)) continue;
            if (piece != "" && !SamePiece(item.Piece, piece)) continue;
            if (!key.All(tokens.Contains)) continue;
            // Mots en trop dans l'icône (« aurora_black » contre « aurora_white ») : moins bon score
            int extra = tokens.Count(t => !words.Contains(t) && !IconNoise.Contains(t) && !PieceWords.Contains(t) && !t.All(char.IsDigit));
            int score = 10 * key.Count + words.Count(w => WeakWords.Contains(w) && tokens.Contains(w)) - 3 * extra;
            result.Add(new ItemMatch(item, score));
        }
        return result.OrderByDescending(m => m.Score).ThenBy(m => m.Item.Id).ToList();
    }

    /// <summary>Meilleur objet pour chaque pièce d'une tenue (le personnage lié est rangé sous « Character »).</summary>
    public Dictionary<string, ItemMatch> BestPerPiece(string gender, string outfitName) =>
        Match(gender, outfitName).Where(m => m.Score > 0)
            .GroupBy(m => m.Item.Piece).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// Bundle de la boutique qui contient une tenue : par l'icône (« Icon_bundle_male_Cobra_Red_26 » pour
    /// « Cos_Cobra_Red », « FF_Icon_sc_hitmanPackage » pour « Sc_Hitman ») ou par le nom
    /// (« Aurora Calling Bundle » pour la tenue « Aurora Calling »).
    /// </summary>
    public ItemMatch? BundleFor(string gender, string outfitName, string? displayName = null)
    {
        var words = Tokens(outfitName);
        var key = words.Where(w => !WeakWords.Contains(w)).ToList();
        string? title = displayName?.Trim().ToLowerInvariant();
        ItemMatch? best = null;
        foreach (var (item, tokens) in _bundles)
        {
            if (item.Gender != null && !item.Gender.Equals(gender, StringComparison.OrdinalIgnoreCase)) continue;
            int score;
            string name = item.Name.ToLowerInvariant();
            if (title != null && title.Length > 2 && (name == title + " bundle" || name == title))
                score = 30;
            else if (key.Count > 0 && key.All(tokens.Contains))
            {
                int extra = tokens.Count(t => !words.Contains(t));
                score = 10 * key.Count + words.Count(w => WeakWords.Contains(w) && tokens.Contains(w)) - 4 * extra;
            }
            else continue;
            if (score > 0 && (best == null || score > best.Score || (score == best.Score && item.Id < best.Item.Id)))
                best = new ItemMatch(item, score);
        }
        return best;
    }

    // Mots d'icône de bundle sans rapport avec la tenue ; « hitmanPackage » → « hitman »
    static HashSet<string> BundleTokens(HashSet<string> tokens)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            if (t is "icon" or "ff" or "bundle" or "male" or "female" or "ui" || t.All(char.IsDigit)) continue;
            result.Add(t.EndsWith("package") && t.Length > 7 ? t[..^7] : t);
        }
        return result;
    }

    static bool SamePiece(string itemPiece, string partPiece) =>
        itemPiece == partPiece
        || (itemPiece == "Head" && partPiece is "Hair" or "Accessory")
        || (partPiece == "Head" && itemPiece is "Hair" or "Accessory" or "Character");

    static readonly HashSet<string> PieceWords = new(StringComparer.Ordinal)
    {
        "top", "bottom", "shoe", "shoes", "hair", "accessory", "mask", "headadditive", "updress", "pants",
        "jacket", "skirt", "boots", "jeans", "shirt",
    };

    /// <summary>Pièce d'après le début de l'ID (catégorie du jeu), affinée par les mots de l'icône.</summary>
    static string PieceOf(long id, HashSet<string> tokens)
    {
        long prefix = id;
        while (prefix >= 1000) prefix /= 10;
        return prefix switch
        {
            101 or 102 or 103 => "Character",
            203 => "Top",
            204 => "Bottom",
            205 => "Shoe",
            211 => tokens.Contains("hair") ? "Hair" : tokens.Contains("accessory") || tokens.Contains("mask") ? "Accessory" : "Head",
            214 => "Facepaint",
            _ => "Other",
        };
    }

    static readonly char[] Separators = ['_', '-', ' ', '.'];

    static HashSet<string> Tokens(string name) =>
        name.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}
