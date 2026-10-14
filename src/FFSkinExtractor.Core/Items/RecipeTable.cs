using System.Buffers.Binary;
using System.IO.Compression;

namespace FFSkinExtractor.Core.Items;

/// <summary>Un morceau de recette : un slot (maillage) et l'overlay (textures) qui l'habille.</summary>
public readonly record struct RecipePair(uint Slot, uint Overlay);

/// <summary>
/// Recettes de vêtements du jeu (« ClothesRecipesBytes », dans les bundles de configuration de l'APK).
/// Beaucoup d'objets n'ont pas de maillage à eux : leur recette reprend le slot d'une autre tenue
/// (« male_clothes_tshirt_N ») et l'habille d'un autre overlay (« male_tshirt_camo_001_Overlay_N »).
/// Les noms y sont écrits sous forme de CRC32 ; on les retrouve grâce aux noms lus dans les bundles.
/// </summary>
public sealed class RecipeTable
{
    // Premier octet de l'en-tête qui suit le nom (hash) de chaque recette
    static readonly HashSet<byte> HeaderFlags = [0x01, 0x02, 0x03, 0x20, 0x21, 0x22, 0x23, 0x40, 0x41, 0x42, 0x43, 0x60, 0x61, 0x62, 0x63];

    public IReadOnlyDictionary<uint, List<RecipePair>> Recipes { get; }

    RecipeTable(Dictionary<uint, List<RecipePair>> recipes) => Recipes = recipes;

    public static RecipeTable Empty { get; } = new([]);

    /// <summary>
    /// Lit les recettes. Une recette : hash de son nom, un en-tête de 4 à 16 octets, puis des paires
    /// (hash du slot, hash de l'overlay). <paramref name="slots"/> (hashs des slots connus) sert à
    /// repérer où commencent et s'arrêtent les paires.
    /// </summary>
    public static RecipeTable Parse(ReadOnlySpan<byte> data, IReadOnlySet<uint> slots)
    {
        var recipes = new Dictionary<uint, List<RecipePair>>();
        // En-tête : drapeau, petit entier, type de pièce (2 cheveux, 3 accessoire, 4 visage, 5 haut, 6 bas, 7 chaussures)
        bool IsHeader(ReadOnlySpan<byte> d, int p) => p + 3 <= d.Length && HeaderFlags.Contains(d[p]) && d[p + 1] <= 3 && d[p + 2] is >= 2 and <= 7;
        uint U32(ReadOnlySpan<byte> d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);

        int p = 4;
        while (p < data.Length - 12)
        {
            if (!IsHeader(data, p + 4)) { p++; continue; }
            int q = -1;
            foreach (int headerLength in (ReadOnlySpan<int>)[4, 8, 12, 16])
            {
                int at = p + 4 + headerLength;
                if (at + 4 <= data.Length && slots.Contains(U32(data, at))) { q = at; break; }
            }
            if (q < 0) { p++; continue; }
            var pairs = new List<RecipePair>();
            while (q + 8 <= data.Length)
            {
                uint slot = U32(data, q);
                // Un slot inconnu, ou un hash suivi d'un en-tête (le nom de la recette suivante), termine la recette
                if (!slots.Contains(slot) || IsHeader(data, q + 4)) break;
                pairs.Add(new RecipePair(slot, U32(data, q + 4)));
                q += 8;
            }
            if (pairs.Count == 0) { p++; continue; }
            recipes.TryAdd(U32(data, p), pairs);
            p = q;
        }
        return new RecipeTable(recipes);
    }

    const string ConfigEntry = "assets/android/gameassetbundles/config/clothesrecipesbytes";

    /// <summary>
    /// Contenu de « ClothesRecipesBytes » : dans l'APK principal du jeu (rangé dans le .xapk),
    /// sinon dans un fichier « clothesrecipesbytes… » du dossier des données.
    /// </summary>
    public static byte[]? LoadBytes(string? package, IEnumerable<string> folderFiles)
    {
        var files = folderFiles.ToList();
        // Le paquet choisi peut n'être que le paquet de données : on essaie aussi les .xapk/.apk du dossier
        var packages = new[] { package }.Concat(files.Where(f => f.EndsWith(".xapk", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc))
            .Concat(files.Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length < 200_000_000))
            .OfType<string>().Distinct();
        foreach (var path in packages)
            if (FromPackage(path) is { } found) return found;
        var file = files.Where(f => Path.GetFileName(f).StartsWith("clothesrecipesbytes", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file == null) return null;
        try
        {
            using var bundle = new GameBundle(file);
            return FromBundle(bundle);
        }
        catch (Exception) { return null; }
    }

    static byte[]? FromPackage(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (FromApk(zip) is { } direct) return direct;
            // .xapk : l'APK principal est petit, le paquet de données (plusieurs centaines de Mo) n'a pas la config
            foreach (var inner in zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                                                         && e.Length < 200_000_000).OrderBy(e => e.Length))
            {
                var mem = new MemoryStream((int)inner.Length);
                using (var s = inner.Open()) s.CopyTo(mem);
                mem.Position = 0;
                using var apk = new ZipArchive(mem, ZipArchiveMode.Read);
                if (FromApk(apk) is { } found) return found;
            }
        }
        catch (Exception) { /* paquet illisible : on essaie le suivant */ }
        return null;
    }

    static byte[]? FromApk(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith(ConfigEntry, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        var mem = new MemoryStream((int)entry.Length);
        using (var s = entry.Open()) s.CopyTo(mem);
        mem.Position = 0;
        using var bundle = new GameBundle(mem, entry.FullName);
        return FromBundle(bundle);
    }

    static byte[]? FromBundle(GameBundle bundle) =>
        bundle.TextAssets().Where(t => t.Name.Equals("ClothesRecipesBytes", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Data).FirstOrDefault();
}

/// <summary>
/// Table « Clothes » des données de base du jeu : pour chaque ID de haut (203…), le nom de ses recettes
/// (salon et partie, homme et femme). Les noms sont en clair, chacun suivi de son CRC32.
/// </summary>
public static class ClothesTable
{
    /// <summary>ID d'objet → noms de recettes (salon homme, partie homme, salon femme, partie femme).</summary>
    public static Dictionary<long, string[]> Parse(ReadOnlySpan<byte> d)
    {
        var result = new Dictionary<long, string[]>();
        // Entrée : int32 ID, int32 type, octet 3, puis 4 × (longueur sur un octet, texte ASCII, CRC32 du texte)
        int p = 0;
        while (p + 9 < d.Length)
        {
            if (d[p + 8] == 3 && ReadValues(d, p + 9) is { } values)
            {
                result.TryAdd(BinaryPrimitives.ReadInt32LittleEndian(d[p..]), values.Names);
                p = values.End;
                continue;
            }
            p++;
        }
        return result;
    }

    static (string[] Names, int End)? ReadValues(ReadOnlySpan<byte> d, int p)
    {
        var names = new string[4];
        for (int i = 0; i < 4; i++)
        {
            if (p >= d.Length) return null;
            int len = d[p];
            if (p + 1 + len + 4 > d.Length) return null;
            var text = d.Slice(p + 1, len);
            foreach (byte b in text) if (b is < 0x20 or > 0x7e) return null;
            if (Crc32.Hash(text) != BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 1 + len)..])) return null;
            names[i] = System.Text.Encoding.ASCII.GetString(text);
            p += 1 + len + 4;
        }
        return (names, p);
    }
}

/// <summary>CRC32 (polynôme 0xEDB88320), celui que le jeu utilise pour écrire les noms.</summary>
public static class Crc32
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Hash(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public static uint Hash(string text) => Hash(System.Text.Encoding.UTF8.GetBytes(text));
}
