using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core;
using FFSkinExtractor.Core.Items;

namespace FFSkinExtractor.App;

public sealed record ItemRef(long Id, string Name, string Rarity, string IconUrl)
{
    public static ItemRef From(GameItem i) => new(i.Id, i.Name, i.Rarity, i.IconUrl);
}

public sealed record PieceEntry(string Piece, List<string> Slots, ItemRef? Item);

/// <summary>Une tenue trouvée dans les bundles, sans ses maillages ni ses textures (chargés à la demande).</summary>
public sealed record OutfitEntry(string Id, string Gender, string Name, string DisplayName, bool HighDetail,
    string Bundle, List<PieceEntry> Pieces, ItemRef? Character)
{
    internal string BundlePath { get; init; } = "";
}

/// <summary>Pièces choisies dans l'interface : pour chaque type de pièce, la tenue d'où elle vient.</summary>
public sealed record Selection(string Gender, bool HighDetail, Dictionary<string, string> Pieces,
    bool Base = true, bool Skinned = true, bool Pbr = true);

public enum LibraryState { NotConfigured, Scanning, Ready, Error }

/// <summary>
/// Bibliothèque des tenues : recherche des bundles et du paquet du jeu, inventaire des tenues,
/// liaison aux ID d'objets et assemblage des modèles à la demande.
/// </summary>
public sealed class Library : IDisposable
{
    static readonly string[] BundlePrefixes = ["optionalab_avatar", "optionalab_legendcloth", "optionalab_tailor"];
    public static readonly string[] PieceOrder = ["Top", "Bottom", "Shoe", "Head", "Hair", "Accessory", "Hands", "Other"];
    const int OpenBundleLimit = 6;

    readonly SemaphoreSlim _build = new(1, 1);
    readonly object _sync = new();
    readonly LinkedList<(string Key, GameBundle Bundle, List<Outfit> Outfits)> _open = new();
    readonly ConcurrentDictionary<string, byte[]> _modelCache = new();
    readonly Dictionary<(string, bool), List<OutfitPart>> _baseParts = [];
    readonly Dictionary<bool, SkinTextures> _skins = [];
    Dictionary<string, string> _cabToBundle = new(StringComparer.OrdinalIgnoreCase);
    Task<GameData?>? _gameData;

    public AppSettings Settings { get; private set; }
    public LibraryState State { get; private set; } = LibraryState.NotConfigured;
    public double Progress { get; private set; }
    public string Message { get; private set; } = "Indique le dossier des données du jeu.";
    public IReadOnlyList<OutfitEntry> Outfits { get; private set; } = [];
    public ItemCatalog? Catalog { get; private set; }
    public string? GamePackage { get; private set; }
    public int BundleCount { get; private set; }
    public bool BaseReady => _gameData is { IsCompletedSuccessfully: true, Result: not null };

    public Library(AppSettings settings)
    {
        Settings = settings;
        if (!string.IsNullOrEmpty(settings.DataFolder)) StartScan(settings);
    }

    public void StartScan(AppSettings settings)
    {
        lock (_sync)
        {
            if (State == LibraryState.Scanning) return;
            Settings = settings;
            State = LibraryState.Scanning;
            Progress = 0;
        }
        _ = Task.Run(() => Scan(settings));
    }

    async Task Scan(AppSettings settings)
    {
        try
        {
            CloseBundles();
            _modelCache.Clear();
            lock (_baseParts) { _baseParts.Clear(); _skins.Clear(); }
            string folder = settings.DataFolder ?? throw new InvalidOperationException("Aucun dossier choisi.");
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Dossier introuvable : {folder}");

            Message = "Recherche des bundles de tenues…";
            var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .ToList();
            var bundles = files.Where(f => BundlePrefixes.Any(p => Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Where(GameBundle.IsDownloaded).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            BundleCount = bundles.Count;
            if (bundles.Count == 0) throw new InvalidDataException("Aucun bundle de tenue (optionalab_avatar…) dans ce dossier.");

            // Paquet du jeu (.xapk/.apk) : corps de base, visage, peau
            GamePackage = settings.GamePackage is { Length: > 0 } p && File.Exists(p) ? p : FindPackage(files);
            _gameData = GamePackage == null ? Task.FromResult<GameData?>(null) : Task.Run(() => OpenGameData(GamePackage));

            Message = "Chargement des ID d'objets (ItemID2)…";
            try { Catalog = await ItemCatalog.LoadOrDownloadAsync(Path.Combine(AppSettings.Folder, "itemData.json")); }
            catch (Exception) { Catalog = null; }

            var entries = new List<OutfitEntry>();
            var cabs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bundles.Count; i++)
            {
                string label = Path.GetFileName(bundles[i]).Split('.')[0];
                Message = $"Lecture de {label} ({i + 1}/{bundles.Count})";
                Progress = (double)i / bundles.Count;
                try
                {
                    using var bundle = new GameBundle(bundles[i]);
                    cabs.TryAdd(bundle.CabName, bundles[i]);
                    foreach (bool hd in new[] { true, false })
                        foreach (var o in OutfitReader.Read(bundle, new OutfitReadOptions { HighDetail = hd }))
                            entries.Add(Describe(o, hd, label, bundles[i]));
                }
                catch (Exception) { /* bundle illisible : on passe au suivant */ }
            }
            _cabToBundle = cabs;
            Outfits = entries.OrderBy(e => e.Gender).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            Message = "Lecture du corps de base…";
            var data = await _gameData;
            Progress = 1;
            State = LibraryState.Ready;
            Message = $"{Outfits.Count(o => o.HighDetail)} tenues HD, {Outfits.Count(o => !o.HighDetail)} en jeu"
                + (data == null ? " · paquet du jeu absent : pas de corps de base" : "");
        }
        catch (Exception e)
        {
            State = LibraryState.Error;
            Message = e.Message;
        }
    }

    static string? FindPackage(List<string> files) =>
        files.Where(f => f.EndsWith(".xapk", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(f).Equals("asset_pack_install_time.apk", StringComparison.OrdinalIgnoreCase)
                         || (f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 100_000_000))
            .OrderByDescending(f => Path.GetFileName(f).Equals("asset_pack_install_time.apk", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    static GameData? OpenGameData(string path)
    {
        try
        {
            var data = GameData.Open(path);
            data.Find("FemaleBaseRecipe_max_N"); // indexe maintenant plutôt qu'au premier aperçu
            return data;
        }
        catch (Exception) { return null; }
    }

    OutfitEntry Describe(Outfit o, bool hd, string label, string path)
    {
        var items = Catalog?.BestPerPiece(o.Gender, o.Name) ?? [];
        var character = items.TryGetValue("Character", out var c) ? ItemRef.From(c.Item) : null;
        // Une « tête » de tenue de personnage est le visage du personnage lui-même
        var pieces = o.Parts.GroupBy(p => p.Piece)
            .OrderBy(g => Array.IndexOf(PieceOrder, g.Key) is var i and >= 0 ? i : 99)
            .Select(g => new PieceEntry(g.Key, g.Select(p => p.SlotName).Distinct().ToList(),
                ItemFor(items, g.Key) ?? (g.Key == "Head" ? character : null)))
            .ToList();
        string id = $"{(hd ? "hd" : "lo")}:{label}:{o.Gender}:{o.Name}";
        return new OutfitEntry(id, o.Gender, o.Name, DisplayName(o.Name, pieces, character), hd, label, pieces, character)
            { BundlePath = path };
    }

    static ItemRef? ItemFor(Dictionary<string, ItemMatch> items, string piece)
    {
        if (items.TryGetValue(piece, out var m) && m.Score >= 7) return ItemRef.From(m.Item);
        // Le catalogue range cheveux et masques sous « Tête »
        string? alt = piece switch { "Hair" or "Accessory" => "Head", "Head" => "Hair", _ => null };
        return alt != null && items.TryGetValue(alt, out m) && m.Score >= 7 ? ItemRef.From(m.Item) : null;
    }

    static string DisplayName(string name, List<PieceEntry> pieces, ItemRef? character)
    {
        var named = pieces.Select(p => p.Item).FirstOrDefault(i => i != null);
        if (named != null) return Regex.Replace(named.Name, @"\s*\((Top|Bottom|Shoes|Pants|Legs|Feet|Mask|Head)\)\s*$", "").Trim();
        if (character != null) return character.Name;
        return Regex.Replace(name, @"^(sc|cos)_", "", RegexOptions.IgnoreCase).Replace('_', ' ');
    }

    public OutfitEntry? Get(string id) => Outfits.FirstOrDefault(o => o.Id == id);

    /// <summary>Assemble les pièces choisies (et le corps de base) et renvoie le GLB.</summary>
    public async Task<byte[]> BuildAsync(Selection sel, CancellationToken ct = default)
    {
        string key = string.Join("|", sel.Gender, sel.HighDetail, sel.Base, sel.Skinned, sel.Pbr,
            string.Join(",", sel.Pieces.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)));
        if (_modelCache.TryGetValue(key, out var cached)) return cached;
        var glb = await WithPartsAsync(sel, (name, parts) =>
            GlbExporter.Build(name, sel.Gender, parts, new ExportOptions { Skinned = sel.Skinned, Pbr = sel.Pbr })
                .ToGltf2().WriteGLB().ToArray(), ct);
        if (_modelCache.Count > 24) _modelCache.Clear();
        _modelCache[key] = glb;
        return glb;
    }

    /// <summary>Textures des pièces choisies, en PNG.</summary>
    public Task<List<DecodedTexture>> TexturesAsync(Selection sel, CancellationToken ct = default) =>
        WithPartsAsync(sel, (_, parts) => parts
            .SelectMany(p => new[] { p.Textures.BaseColor, p.Textures.Normal, p.Textures.Mask })
            .OfType<DecodedTexture>().DistinctBy(t => t.Name).ToList(), ct);

    /// <summary>Nom de fichier par défaut : la tenue qui fournit le plus de pièces.</summary>
    public string DefaultName(Selection sel) => sel.Pieces.Count == 0 ? $"{sel.Gender}_Base"
        : $"{sel.Gender}_{Get(sel.Pieces.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key)?.Name ?? "Tenue"}"
          + (sel.HighDetail ? "_HD" : "");

    async Task<T> WithPartsAsync<T>(Selection sel, Func<string, List<OutfitPart>, T> use, CancellationToken ct)
    {
        await _build.WaitAsync(ct);
        try
        {
            var data = _gameData == null ? null : await _gameData;
            var skin = data == null ? null : Skin(data, sel.HighDetail);
            var chosen = new List<OutfitPart>();
            foreach (var (piece, outfitId) in sel.Pieces)
            {
                var entry = Get(outfitId) ?? throw new KeyNotFoundException($"Tenue inconnue : {outfitId}");
                var outfit = LoadOutfit(entry, skin);
                chosen.AddRange(outfit.Parts.Where(p => p.Piece == piece));
            }
            // Décodage des textures tant que les bundles sont ouverts
            foreach (var part in chosen) _ = part.Textures;

            var parts = chosen;
            if (sel.Base && data != null)
                parts = BaseCharacter.Compose(BaseParts(data, sel.Gender, sel.HighDetail), chosen);
            return use(DefaultName(sel), parts);
        }
        finally { _build.Release(); }
    }

    /// <summary>Sélection qui reprend toutes les pièces d'une tenue.</summary>
    public static Selection Full(OutfitEntry o, bool withBase = true, bool skinned = true, bool pbr = true) =>
        new(o.Gender, o.HighDetail, o.Pieces.ToDictionary(p => p.Piece, _ => o.Id), withBase, skinned, pbr);

    SkinTextures Skin(GameData data, bool hd)
    {
        lock (_baseParts)
        {
            if (!_skins.TryGetValue(hd, out var s)) _skins[hd] = s = SkinTextures.From(data, hd);
            return s;
        }
    }

    List<OutfitPart> BaseParts(GameData data, string gender, bool hd)
    {
        lock (_baseParts)
        {
            if (!_baseParts.TryGetValue((gender, hd), out var parts))
            {
                parts = BaseCharacter.Build(data, gender, hd);
                foreach (var p in parts) _ = p.Textures;
                _baseParts[(gender, hd)] = parts;
            }
            return parts;
        }
    }

    /// <summary>Tenue complète (maillages, textures à la demande) ; garde ouverts les derniers bundles utilisés.</summary>
    Outfit LoadOutfit(OutfitEntry entry, SkinTextures? skin)
    {
        string key = entry.BundlePath + "|" + entry.HighDetail;
        var node = _open.First;
        while (node != null && node.Value.Key != key) node = node.Next;
        if (node == null)
        {
            var bundle = new GameBundle(entry.BundlePath);
            var outfits = OutfitReader.Read(bundle, new OutfitReadOptions
            {
                HighDetail = entry.HighDetail,
                Skin = skin,
                External = ExternalTexture,
            });
            node = _open.AddFirst((key, bundle, outfits));
            while (_open.Count > OpenBundleLimit)
            {
                _open.Last!.Value.Bundle.Dispose();
                _open.RemoveLast();
            }
        }
        else if (node != _open.First)
        {
            _open.Remove(node);
            _open.AddFirst(node);
        }
        return node.Value.Outfits.FirstOrDefault(o => o.Gender == entry.Gender && o.Name == entry.Name)
            ?? throw new KeyNotFoundException($"{entry.Name} absent de {entry.Bundle}");
    }

    // Texture rangée dans un autre bundle, désigné par son nom CAB
    DecodedTexture? ExternalTexture(string cab, long pathId)
    {
        if (!_cabToBundle.TryGetValue(cab, out var path)) return null;
        try
        {
            using var other = new GameBundle(path);
            var info = other.GetInfo(pathId);
            return info == null ? null : TextureDecoder.Decode(other, info);
        }
        catch (Exception) { return null; }
    }

    void CloseBundles()
    {
        _build.Wait();
        try
        {
            foreach (var (_, b, _) in _open) b.Dispose();
            _open.Clear();
        }
        finally { _build.Release(); }
    }

    public void Dispose()
    {
        CloseBundles();
        if (_gameData is { IsCompletedSuccessfully: true, Result: { } d }) d.Dispose();
    }
}
