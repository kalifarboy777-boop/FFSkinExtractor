using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core;
using FFSkinExtractor.Core.Items;
using FFSkinExtractor.Core.Uma;

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
    /// <summary>Clé indépendante de la qualité : « Male:Sc_Hitman ».</summary>
    public string Key => Gender + ":" + Name;
    /// <summary>Tenue d'un personnage (Rafael, Kelly…) : son visage et sa coiffure font le personnage.</summary>
    public bool IsCharacter { get; init; }
    /// <summary>Tous les ID d'objets reliés à chaque pièce (plusieurs objets peuvent partager un maillage).</summary>
    internal Dictionary<string, List<ItemRef>> PieceItems { get; init; } = [];
    /// <summary>
    /// Tenue d'où vient chaque pièce : dans les bundles numérotés, une même tenue est souvent répartie
    /// sur plusieurs bundles (le haut dans l'un, le bas dans un autre).
    /// </summary>
    internal Dictionary<string, string> PieceSources { get; init; } = [];
    internal string SourceOf(string piece) => PieceSources.GetValueOrDefault(piece) ?? Id;
    /// <summary>Tenue tirée d'une recette du jeu : slots d'autres tenues habillés d'autres overlays.</summary>
    internal List<RecipePart>? Recipe { get; init; }
    // Aucune pièce lue dans un bundle : la tenue ne sert qu'à porter des ID d'objets
    internal bool RecipeOnly { get; init; }
}

/// <summary>Morceau d'une recette : un slot (et son bundle) et l'overlay qui l'habille, s'il est connu.</summary>
sealed record RecipePart(string SlotName, string SlotBundle, bool SlotHd, string? Overlay, string? OverlayBundle);

/// <summary>Un slot lu dans les bundles, retrouvé par le hash de son nom dans les recettes.</summary>
sealed record SlotRef(string SlotName, string Bundle, bool Hd, string Gender, string Key, string Piece);

/// <summary>Un personnage du jeu, choisi tel quel (visage, coiffure et tenue d'origine).</summary>
public sealed record CharacterEntry(string Key, string Gender, string Name, string Internal, ItemRef? Item);

/// <summary>Un objet qu'on peut mettre dans un emplacement du constructeur de skin.</summary>
/// <param name="Value">ID d'objet, ou « o:Genre:Tenue:Pièce » pour une pièce sans ID connu.</param>
public sealed record SlotItem(string Value, long? Id, string Name, string Rarity, string? IconUrl, string Slot,
    string Gender, string Outfit);

/// <summary>Un bundle de la boutique et les objets qu'il met dans chaque emplacement.</summary>
public sealed record BundleEntry(string Value, long? Id, string Name, string Rarity, string? IconUrl, string? Gender,
    bool Available, Dictionary<string, string> Slots, string? Outfit);

/// <summary>Skin arrivé de l'appareil : le bundle à choisir, ou à défaut les objets à mettre par emplacement.</summary>
public sealed record Arrival(string Name, string Gender, string? Bundle, Dictionary<string, string> Slots, string? IconUrl);

/// <summary>Ce que l'interface demande : un personnage, et des objets par emplacement (Cheveux, Masque, Haut…).</summary>
public sealed record Look(string Gender, bool HighDetail, string? Character, Dictionary<string, string>? Slots,
    bool Base = true, bool Skinned = true, bool Pbr = true);

/// <summary>Pièces choisies : pour chaque type de pièce, la tenue d'où elle vient.</summary>
public sealed record Selection(string Gender, bool HighDetail, Dictionary<string, string> Pieces,
    bool Base = true, bool Skinned = true, bool Pbr = true)
{
    public string? Name { get; init; }
    /// <summary>Tenue interne du personnage choisi (« Sc_Hitman »), pour son maquillage et ses racines de cheveux.</summary>
    public string? Character { get; init; }
    /// <summary>Maquillage choisi (nom interne « Cos_Aurora_White »), ou null.</summary>
    public string? FacePaint { get; init; }
}

/// <summary>Overlay de maquillage ou de racines de cheveux, trouvé dans un bundle (ou dans l'APK si Bundle est null).</summary>
sealed record HeadLayerRef(string Gender, HeadLayerKind Kind, string Name, string? Bundle, string Overlay, bool Atlas);

/// <summary>Un objet du catalogue ItemID2 et ce qu'on peut en faire avec les fichiers du joueur.</summary>
/// <param name="Action">« bundle », « slot » ou « character » si le modèle 3D est dans les fichiers, sinon null.</param>
public sealed record BrowseItem(long Id, string Name, string IconUrl, string Rarity, string Type, string Piece, string? Gender,
    string? Action, string? Value, string? Slot);

public sealed record BrowsePage(int Total, int Page, int Pages, int Available, List<BrowseItem> Items);

public enum LibraryState { NotConfigured, Scanning, Ready, Error }

/// <summary>
/// Bibliothèque des tenues : recherche des bundles et du paquet du jeu, inventaire des tenues,
/// liaison aux ID d'objets et assemblage des modèles à la demande.
/// </summary>
public sealed partial class Library : IDisposable
{
    public static readonly string[] PieceOrder = ["Top", "Bottom", "Shoe", "Head", "Hair", "Accessory", "Hands", "Other"];
    const int OpenBundleLimit = 6;

    readonly SemaphoreSlim _build = new(1, 1);
    readonly object _sync = new();
    readonly LinkedList<(string Key, GameBundle Bundle, List<Outfit> Outfits)> _open = new();
    readonly ConcurrentDictionary<string, byte[]> _modelCache = new();
    readonly Dictionary<(string, bool), List<OutfitPart>> _baseParts = [];
    readonly Dictionary<bool, SkinTextures> _skins = [];
    Dictionary<string, string> _cabToBundle = new(StringComparer.OrdinalIgnoreCase);
    List<HeadLayerRef> _headLayers = [];
    // Overlays de tous les bundles, par clé de rapprochement : un slot peut prendre ses textures dans un autre bundle
    Dictionary<string, (string Bundle, string Overlay)> _overlayIndex = [];
    readonly Dictionary<string, TextureSet?> _externalOverlays = [];
    readonly Dictionary<(HeadLayerKind, string, string), List<HeadLayer>> _layerCache = [];
    Dictionary<long, List<(string Gender, string Name)>> _facePaintIndex = [];
    // ID d'objet → tenue et pièce, d'après la table « Clothes » et les recettes du jeu
    Dictionary<long, List<(string Key, string Piece)>> _tableLinks = [];
    // Slots des vêtements de base (débardeur, T-shirt col V…) rangés dans les données de l'APK, par slotName
    Dictionary<string, (DataObject Obj, SlotInfo Info)> _apkSlots = [];
    Task<GameData?>? _gameData;

    public AppSettings Settings { get; private set; }
    public LibraryState State { get; private set; } = LibraryState.NotConfigured;
    public double Progress { get; private set; }
    public string Message { get; private set; } = "Indique le dossier des données du jeu.";
    /// <summary>Tenues réunies par nom et qualité, toutes pièces confondues.</summary>
    public IReadOnlyList<OutfitEntry> Outfits { get; private set; } = [];
    // Tenues telles que lues, une par bundle (les pièces d'une sélection renvoient à celles-ci)
    Dictionary<string, OutfitEntry> _entryById = [];
    public IReadOnlyList<CharacterEntry> Characters { get; private set; } = [];
    public IReadOnlyList<SlotItem> SlotItems { get; private set; } = [];
    public IReadOnlyList<BundleEntry> Bundles { get; private set; } = [];
    public ItemCatalog? Catalog { get; private set; }
    public string? GamePackage { get; private set; }
    public int BundleCount { get; private set; }
    public bool BaseReady => _gameData is { IsCompletedSuccessfully: true, Result: not null };

    public Library(AppSettings settings)
    {
        Settings = settings;
        if (!string.IsNullOrEmpty(settings.DataFolder)) StartScan(settings);
    }

    /// <summary>Skins arrivés de l'appareil lors du dernier suivi en direct ; <see cref="ArrivalStamp"/> change à chaque arrivée.</summary>
    public IReadOnlyList<Arrival> Arrivals { get; private set; } = [];
    public int ArrivalStamp { get; private set; }
    readonly HashSet<string> _pendingArrivals = new(StringComparer.OrdinalIgnoreCase);
    bool _rescanQueued;

    /// <summary>Relance l'analyse après l'arrivée de nouveaux bundles, puis signale les skins qu'ils contiennent.</summary>
    public void AddArrivals(IEnumerable<string> bundlePaths)
    {
        lock (_sync)
        {
            _pendingArrivals.UnionWith(bundlePaths);
            // Analyse déjà en cours : elle n'a peut-être pas vu ces fichiers, on en relance une à la fin
            if (State == LibraryState.Scanning) { _rescanQueued = true; return; }
        }
        StartScan(Settings);
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
            // Un même bundle peut être présent plusieurs fois (ancienne copie, version récupérée de l'appareil) :
            // on garde la plus récente
            var bundles = files.Where(GameBundle.IsOutfitBundle).Where(GameBundle.IsDownloaded)
                .GroupBy(f => Path.GetFileName(f).Split('.')[0], StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(File.GetLastWriteTimeUtc).First())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            BundleCount = bundles.Count;
            if (bundles.Count == 0) throw new InvalidDataException("Aucun bundle de tenue (optionalab_avatar…) dans ce dossier.");

            // Paquet du jeu (.xapk/.apk) : corps de base, visage, peau
            GamePackage = settings.GamePackage is { Length: > 0 } p && File.Exists(p) ? p : FindPackage(files);
            _gameData = GamePackage == null ? Task.FromResult<GameData?>(null) : Task.Run(() => OpenGameData(GamePackage));
            string? package = GamePackage;
            var recipeBytes = Task.Run(() => RecipeTable.LoadBytes(package, files));

            Message = "Chargement des ID d'objets (ItemID2)…";
            try { Catalog = await ItemCatalog.LoadOrDownloadAsync(Path.Combine(AppSettings.Folder, "itemData.json")); }
            catch (Exception) { Catalog = null; }

            // Lecture en parallèle (les bundles numérotés se comptent par centaines) ; un cache évite de relire
            // à chaque lancement les bundles qui n'ont pas changé
            var cache = ScanCache.Load();
            var results = new ScanCache.BundleScan?[bundles.Count];
            int done = 0;
            Parallel.For(0, bundles.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                results[i] = cache.Get(bundles[i]) ?? ReadBundle(bundles[i]);
                int n = Interlocked.Increment(ref done);
                Progress = (double)n / bundles.Count;
                Message = $"Lecture des bundles ({n}/{bundles.Count})";
            });
            cache.Save(bundles, results);

            var entries = new List<OutfitEntry>();
            var cabs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var layers = new List<HeadLayerRef>();
            var overlayIndex = new Dictionary<string, (string, string)>();
            for (int i = 0; i < bundles.Count; i++)
            {
                if (results[i] is not { } r) continue;
                string label = Path.GetFileName(bundles[i]).Split('.')[0];
                foreach (var o in r.Outfits)
                    if (!IsAccessoryOnly(o.Name)) entries.Add(Describe(o, label, bundles[i]));
                if (r.Cab != null) cabs.TryAdd(r.Cab, bundles[i]);
                layers.AddRange(r.Layers.Select(l => new HeadLayerRef(l.Gender, l.Kind, l.Name, bundles[i], l.Overlay, l.Atlas)));
                foreach (var (key, overlay) in r.Overlays) overlayIndex.TryAdd(key, (bundles[i], overlay));
            }
            // Recettes du jeu : objets qui reprennent le maillage d'une tenue avec les textures d'une autre
            Message = "Lecture des recettes du jeu…";
            var data = await _gameData;
            var slotRefs = SlotRefs(bundles, results);
            var overlayByHash = new Dictionary<uint, (string Bundle, string Overlay)>();
            for (int i = 0; i < bundles.Count; i++)
                foreach (var (_, overlay) in results[i]?.Overlays ?? [])
                    overlayByHash.TryAdd(Crc32.Hash(overlay), (bundles[i], overlay));
            _apkSlots = AddApkParts(data, slotRefs, overlayByHash);
            var recipes = await recipeBytes is { } rb ? RecipeTable.Parse(rb, slotRefs.Keys.ToHashSet()) : RecipeTable.Empty;
            var byRecipe = new Dictionary<uint, (string Key, string Piece)>();
            entries.AddRange(RecipeEntries(recipes, slotRefs, overlayByHash, byRecipe));
            var tableLinks = new Dictionary<long, List<(string Key, string Piece)>>();
            byte[]? clothes = null;
            try { clothes = data?.TextAsset("Clothes"); } catch (Exception) { /* table illisible : rapprochement par les noms */ }
            foreach (var (id, names) in clothes == null ? [] : ClothesTable.Parse(clothes))
                foreach (var name in names)
                    if (name.Length > 0 && byRecipe.TryGetValue(Crc32.Hash(name), out var link))
                    {
                        if (!tableLinks.TryGetValue(id, out var list)) tableLinks[id] = list = [];
                        if (!list.Contains(link)) list.Add(link);
                    }
            _tableLinks = tableLinks;

            _cabToBundle = cabs;
            lock (_layerCache) _layerCache.Clear();
            _headLayers = layers;
            _overlayIndex = overlayIndex;
            lock (_externalOverlays) _externalOverlays.Clear();
            _entryById = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            Outfits = Merge(entries).OrderBy(e => e.Gender).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            BuildCatalogViews();
            BuildFacePaintViews();

            if (data != null)
            {
                // Maquillages et racines de cheveux rangés dans l'APK (personnages de départ)
                var known = _headLayers.Select(l => l.Overlay).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var o in data.FindAll(n => HeadPaint.Classify(n) != null))
                    if (o.Type == AssetsTools.NET.Extra.AssetClassID.MonoBehaviour && known.Add(o.Name) && HeadPaint.Classify(o.Name) is { } c)
                        _headLayers.Add(new HeadLayerRef(c.Gender, c.Kind, c.Name, null, o.Name, false));
                BuildFacePaintViews();
            }
            Progress = 1;
            Message = $"{Characters.Count} personnages, {Bundles.Count(b => b.Available)} bundles, {SlotItems.Count} objets"
                + (data == null ? " · paquet du jeu absent : pas de corps de base" : "");
            bool again;
            lock (_sync)
            {
                again = _rescanQueued;
                _rescanQueued = false;
                if (!again && _pendingArrivals.Count > 0)
                {
                    var found = FindArrivals(_pendingArrivals);
                    _pendingArrivals.Clear();
                    if (found.Count > 0) { Arrivals = found; ArrivalStamp++; }
                }
                State = again ? LibraryState.Scanning : LibraryState.Ready;
            }
            if (again) _ = Task.Run(() => Scan(settings));
        }
        catch (Exception e)
        {
            lock (_sync) { _rescanQueued = false; _pendingArrivals.Clear(); }
            State = LibraryState.Error;
            Message = e.Message;
        }
    }

    // Skins contenus dans les bundles arrivés : le bundle de la tenue s'il existe, sinon ses objets un par un
    List<Arrival> FindArrivals(IReadOnlySet<string> paths)
    {
        var result = new List<Arrival>();
        var keys = Outfits.Where(o => paths.Contains(o.BundlePath) && !o.RecipeOnly).Select(o => (o.Key, o.Gender, o.Name, o.DisplayName)).Distinct();
        foreach (var (key, gender, name, display) in keys)
        {
            if (Bundles.FirstOrDefault(b => b.Value == "b:" + key) is { } b)
            {
                result.Add(new Arrival(b.Name, gender, b.Value, b.Slots, b.IconUrl));
                continue;
            }
            var slots = SlotItems.Where(i => i.Gender == gender && i.Outfit == name)
                .GroupBy(i => i.Slot).ToDictionary(g => g.Key, g => g.First().Value);
            if (slots.Count > 0)
                result.Add(new Arrival(display, gender, null, slots, SlotItems.FirstOrDefault(i => i.Gender == gender && i.Outfit == name && i.IconUrl != null)?.IconUrl));
        }
        return result;
    }

    // Sacs à dos, parachutes… rangés avec les tenues dans les bundles numérotés : ce ne sont pas des vêtements
    static bool IsAccessoryOnly(string outfitName) =>
        outfitName.Contains("backpack", StringComparison.OrdinalIgnoreCase)
        || outfitName.Contains("parachute", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Réunit les morceaux d'une même tenue lus dans plusieurs bundles.</summary>
    static List<OutfitEntry> Merge(List<OutfitEntry> entries) =>
        entries.GroupBy(e => (e.Key, e.HighDetail)).Select(g =>
        {
            // La version la plus complète d'abord, puis les pièces qui lui manquent
            var ordered = g.OrderByDescending(e => e.Pieces.Count).ToList();
            var first = ordered[0];
            var sources = new Dictionary<string, string>();
            var pieces = new List<PieceEntry>();
            var items = new Dictionary<string, List<ItemRef>>();
            foreach (var e in ordered)
                foreach (var p in e.Pieces)
                    if (sources.TryAdd(p.Piece, e.Id))
                    {
                        pieces.Add(p);
                        items[p.Piece] = e.PieceItems.GetValueOrDefault(p.Piece) ?? [];
                    }
            // La recette du jeu dit quel overlay habille chaque slot (« Male_Hair_Cos_Hurricane_IN_N » reçoit
            // l'overlay de la casquette, pas celui des cheveux) : elle passe avant l'appariement par les noms
            for (int i = 0; i < pieces.Count; i++)
            {
                var chosen = pieces[i];
                if (ordered.Any(e => e.Id == sources[chosen.Piece] && e.Recipe != null)) continue;
                var recipe = ordered.FirstOrDefault(e => e.Recipe != null && e.Recipe.Any(CrossedOverlay)
                    && e.Pieces.FirstOrDefault(p => p.Piece == chosen.Piece) is { } rp && chosen.Slots.All(rp.Slots.Contains));
                if (recipe == null) continue;
                sources[chosen.Piece] = recipe.Id;
                pieces[i] = chosen with { Slots = recipe.Pieces.First(p => p.Piece == chosen.Piece).Slots };
            }
            bool recipeOnly = ordered.All(e => e.Recipe != null);
            if (ordered.Count == 1) return first with { PieceSources = sources, RecipeOnly = recipeOnly };
            pieces = pieces.OrderBy(p => Array.IndexOf(PieceOrder, p.Piece) is var i and >= 0 ? i : 99).ToList();
            var character = ordered.Select(e => e.Character).FirstOrDefault(c => c != null);
            bool isCharacter = ordered.Any(e => e.IsCharacter) || (pieces.Any(p => p.Piece == "Head")
                && (character != null || first.Name.StartsWith("sc_", StringComparison.OrdinalIgnoreCase)));
            string bundles = string.Join("+", ordered.Select(e => e.Bundle).Distinct());
            return first with
            {
                Id = $"{(first.HighDetail ? "hd" : "lo")}:{bundles}:{first.Gender}:{first.Name}",
                Bundle = bundles, Pieces = pieces, Character = character,
                DisplayName = DisplayName(first.Name, pieces, character),
                IsCharacter = isCharacter, PieceItems = items, PieceSources = sources, RecipeOnly = recipeOnly,
            };
        }).ToList();

    // Le slot reçoit un overlay d'un autre nom que le sien (cheveux habillés de l'overlay du chapeau) :
    // seule la recette le sait. Les overlays de peau et les autres cas restent appariés par les noms (PBR compris).
    static bool CrossedOverlay(RecipePart rp) => rp.Overlay != null && !BaseOverlay().IsMatch(rp.Overlay)
        && Words(rp.SlotName) != Words(rp.Overlay);

    static string Words(string name) => OutfitReader.OverlayKey(name).Split('|')[0];

    const string RecipeLabel = "recette";
    // Source des slots et overlays rangés dans les données de l'APK plutôt que dans un bundle
    const string ApkSource = "apk";

    // Overlays de la peau : corps de base, ou peau propre à la tenue (« male_body_cos_summer20 », « Skin_Cos_Fastbreaking »)
    [GeneratedRegex(@"(^|_)(base|body|skin)_", RegexOptions.IgnoreCase)]
    private static partial Regex BaseOverlay();

    [GeneratedRegex(@"_N(_\d+)?$")]
    private static partial Regex HighDetailSlot();

    /// <summary>
    /// Ajoute aux index les slots et overlays des données de base de l'APK : les vêtements simples
    /// (débardeur, T-shirt col V…) y ont leur maillage, et leurs recettes y renvoient.
    /// </summary>
    static Dictionary<string, (DataObject, SlotInfo)> AddApkParts(GameData? data,
        Dictionary<uint, SlotRef> slots, Dictionary<uint, (string Bundle, string Overlay)> overlays)
    {
        var apkSlots = new Dictionary<string, (DataObject, SlotInfo)>(StringComparer.Ordinal);
        if (data == null) return apkSlots;
        foreach (var o in data.FindAll(n => n.EndsWith("_Slot", StringComparison.Ordinal) || n.Contains("overlay", StringComparison.OrdinalIgnoreCase)))
        {
            if (o.Type != AssetsTools.NET.Extra.AssetClassID.MonoBehaviour) continue;
            if (!o.Name.EndsWith("_Slot", StringComparison.Ordinal))
            {
                overlays.TryAdd(Crc32.Hash(o.Name), (ApkSource, o.Name));
                continue;
            }
            SlotInfo info;
            try { info = UmaAssets.ParseSlot(data.Raw(o)); }
            catch (Exception) { continue; }
            var words = info.SlotName.Split('_', 2);
            string? gender = words[0].ToLowerInvariant() switch { "male" => "Male", "female" => "Female", _ => null };
            if (gender == null || words.Length < 2 || !apkSlots.TryAdd(info.SlotName, (o, info))) continue;
            slots.TryAdd(Crc32.Hash(info.SlotName), new SlotRef(info.SlotName, ApkSource, HighDetailSlot().IsMatch(info.SlotName),
                gender, $"{gender}:{ApkSource}:{info.SlotName}", OutfitReader.PieceOf(words[1])));
        }
        return apkSlots;
    }
    // Rapprochement par le nom sans mot en trop dans l'icône : « warringstates_purple » ne prend pas le coloris de base
    const int MinNameScore = 10;

    // Slots de toutes les tenues lues, par hash de leur nom. Les recettes « _PBR » désignent le slot « _N »
    // (le même maillage), que la lecture des tenues garde à leur place.
    static Dictionary<uint, SlotRef> SlotRefs(List<string> bundles, ScanCache.BundleScan?[] results)
    {
        var refs = new Dictionary<uint, SlotRef>();
        for (int i = 0; i < bundles.Count; i++)
            foreach (var o in results[i]?.Outfits ?? [])
                foreach (var p in o.Parts)
                {
                    var r = new SlotRef(p.SlotName, bundles[i], o.HighDetail, o.Gender, o.Gender + ":" + o.Name, p.Piece);
                    refs.TryAdd(Crc32.Hash(p.SlotName), r);
                    if (Regex.Match(p.SlotName, @"_N(_\d+)?$") is { Success: true } m)
                        refs.TryAdd(Crc32.Hash(p.SlotName[..m.Index] + "_PBR" + m.Groups[1].Value), r);
                }
        return refs;
    }

    static readonly HashSet<string> RecipeNameNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "male", "female", "mle", "shared", "overlay", "n", "pbr", "slot", "lobby", "ingame",
        "top", "bottom", "shoe", "shoes", "feet", "hair", "head", "leg", "accessory", "mask", "face", "hat",
        "clothes", "hand", "hands", "headadditive",
    };

    static readonly string[] RecipePieces = ["Top", "Bottom", "Shoe", "Head", "Hair", "Accessory"];

    /// <summary>
    /// Une tenue par recette dont l'overlay est connu, nommée d'après cet overlay
    /// (« Male_Top_Cos_Goldenleo_A_Overlay_N » → « Cos_Goldenleo_A »). <paramref name="byRecipe"/> reçoit,
    /// pour chaque recette, la tenue et la pièce qui la portent (la tenue du slot si la recette n'a pas d'overlay à elle).
    /// </summary>
    static List<OutfitEntry> RecipeEntries(RecipeTable table, Dictionary<uint, SlotRef> slots,
        Dictionary<uint, (string Bundle, string Overlay)> overlays, Dictionary<uint, (string Key, string Piece)> byRecipe)
    {
        var result = new List<OutfitEntry>();
        foreach (var (hash, pairs) in table.Recipes)
        {
            var first = slots[pairs[0].Slot];
            if (!RecipePieces.Contains(first.Piece) || IsAccessoryOnly(first.SlotName)) continue;
            // L'overlay qui nomme la recette : pas celui du corps de base (« Male_Base_body… ») que reçoivent les morceaux de peau
            string? named = pairs.Select(p => overlays.TryGetValue(p.Overlay, out var o) ? o.Overlay : null)
                .FirstOrDefault(o => o != null && !BaseOverlay().IsMatch(o));
            if (named == null)
            {
                byRecipe[hash] = (first.Key, first.Piece);
                continue;
            }
            string name = string.Join('_', named.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => !RecipeNameNoise.Contains(w)));
            if (name.Length == 0 || IsAccessoryOnly(name)) continue;
            var parts = pairs.Select(p =>
            {
                var s = slots[p.Slot];
                return overlays.TryGetValue(p.Overlay, out var o)
                    ? new RecipePart(s.SlotName, s.Bundle, s.Hd, o.Overlay, o.Bundle)
                    : new RecipePart(s.SlotName, s.Bundle, s.Hd, null, null);
            }).ToList();
            var pieces = new List<PieceEntry> { new(first.Piece, parts.Select(p => p.SlotName).Distinct().ToList(), null) };
            // Une recette par pièce : le haut, le bas et les chaussures d'une même tenue se réunissent ensuite par leur nom
            var entry = new OutfitEntry($"{(first.Hd ? "hd" : "lo")}:{RecipeLabel}-{first.Piece}:{first.Gender}:{name}", first.Gender, name,
                DisplayName(name, pieces, null), first.Hd, RecipeLabel, pieces, null) { Recipe = parts };
            result.Add(entry);
            byRecipe[hash] = (entry.Key, first.Piece);
        }
        return result;
    }

    /// <summary>Inventaire d'un bundle : tenues (sans maillages ni textures), calques du visage et overlays.</summary>
    static ScanCache.BundleScan? ReadBundle(string path)
    {
        try
        {
            using var bundle = new GameBundle(path);
            var layers = new List<ScanCache.Layer>();
            var overlays = new List<(string, string)>();
            foreach (var o in bundle.UmaObjects())
            {
                if (!o.ScriptClass.StartsWith("OverlayDataAsset")) continue;
                if (HeadPaint.Classify(o.Name) is { } c)
                    layers.Add(new ScanCache.Layer(c.Gender, c.Kind, c.Name, o.Name, o.ScriptClass.EndsWith("Atlas")));
                else overlays.Add((OutfitReader.OverlayKey(o.Name), o.Name));
            }
            var outfits = new List<ScanCache.OutfitSummary>();
            foreach (bool hd in new[] { true, false })
                foreach (var o in OutfitReader.Read(bundle, new OutfitReadOptions { HighDetail = hd }))
                    outfits.Add(new ScanCache.OutfitSummary(o.Gender, o.Name, hd, o.Parts.Select(p => new ScanCache.PartSummary(p.Piece, p.SlotName)).ToList()));
            return new ScanCache.BundleScan(bundle.CabName, outfits, layers, overlays);
        }
        catch (Exception) { return null; /* bundle illisible : on passe au suivant */ }
    }

    OutfitEntry Describe(ScanCache.OutfitSummary o, string label, string path)
    {
        bool hd = o.HighDetail;
        var matches = Catalog?.Match(o.Gender, o.Name) ?? [];
        var items = matches.Where(m => m.Score > 0).GroupBy(m => m.Item.Piece).ToDictionary(g => g.Key, g => g.First());
        var character = items.TryGetValue("Character", out var c) ? ItemRef.From(c.Item) : null;
        // Une « tête » de tenue de personnage est le visage du personnage lui-même
        var pieces = o.Parts.GroupBy(p => p.Piece)
            .OrderBy(g => Array.IndexOf(PieceOrder, g.Key) is var i and >= 0 ? i : 99)
            .Select(g => new PieceEntry(g.Key, g.Select(p => p.SlotName).Distinct().ToList(),
                ItemFor(items, g.Key) ?? (g.Key == "Head" ? character : null)))
            .ToList();
        var pieceItems = pieces.ToDictionary(p => p.Piece, p => AllItemsFor(matches, p.Piece, p.Item));
        bool isCharacter = pieces.Any(p => p.Piece == "Head")
            && (character != null || o.Name.StartsWith("sc_", StringComparison.OrdinalIgnoreCase));
        string id = $"{(hd ? "hd" : "lo")}:{label}:{o.Gender}:{o.Name}";
        return new OutfitEntry(id, o.Gender, o.Name, DisplayName(o.Name, pieces, character), hd, label, pieces, character)
            { BundlePath = path, IsCharacter = isCharacter, PieceItems = pieceItems };
    }

    // Tous les objets au meilleur score pour une pièce (« Olivia's Nurse Uniform » et « Intern Uniform »
    // partagent le même maillage)
    static List<ItemRef> AllItemsFor(List<ItemMatch> matches, string piece, ItemRef? main)
    {
        if (main == null) return [];
        var mainMatch = matches.First(m => m.Item.Id == main.Id);
        return matches.Where(m => m.Item.Piece == mainMatch.Item.Piece && m.Score == mainMatch.Score)
            .Select(m => ItemRef.From(m.Item)).ToList();
    }

    /// <summary>Emplacement du constructeur de skin pour une pièce (null : la pièce fait partie du personnage).</summary>
    public static string? SlotOf(string piece, bool character) => piece switch
    {
        "Head" or "Hair" when character => null,
        "Accessory" => "Mask",
        _ => piece,
    };

    // Vues pour l'interface : personnages, objets par emplacement, bundles de la boutique
    void BuildCatalogViews()
    {
        // Une entrée par tenue, de préférence en HD
        var unique = Outfits.GroupBy(o => o.Key).Select(g => g.OrderByDescending(o => o.HighDetail).First()).ToList();

        Characters = unique.Where(o => o.IsCharacter)
            .Select(o => new CharacterEntry(o.Key, o.Gender, o.Character?.Name ?? o.DisplayName, o.Name,
                o.Character ?? o.Pieces.First(p => p.Piece == "Head").Item))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var slotItems = new List<SlotItem>();
        var index = new Dictionary<long, List<(string Key, string Piece)>>();
        var bundles = new List<BundleEntry>();
        var usedBundles = new HashSet<long>();
        void Link(GameItem item, string key, string piece, string slot)
        {
            string gender = key[..key.IndexOf(':')], name = key[(key.IndexOf(':') + 1)..];
            slotItems.Add(new SlotItem(item.Id.ToString(), item.Id, item.Name, item.Rarity, item.IconUrl, slot, gender, name));
            if (!index.TryGetValue(item.Id, out var list)) index[item.Id] = list = [];
            if (!list.Contains((key, piece))) list.Add((key, piece));
        }
        var tableLinks = WithSetSiblings(_tableLinks, unique);
        bool InTable(long id, string gender) =>
            tableLinks.TryGetValue(id, out var links) && links.Any(l => l.Key.StartsWith(gender + ":"));

        foreach (var o in unique)
        {
            // Tenues tirées des recettes : elles ne servent qu'à porter des ID d'objets (voir plus bas)
            if (o.RecipeOnly) continue;
            var slots = new Dictionary<string, string>();
            foreach (var p in o.Pieces)
            {
                if (SlotOf(p.Piece, o.IsCharacter) is not { } slot) continue;
                var ids = o.PieceItems.GetValueOrDefault(p.Piece) ?? [];
                if (ids.Count == 0)
                {
                    string value = $"o:{o.Key}:{p.Piece}";
                    slotItems.Add(new SlotItem(value, null, $"{o.DisplayName} ({SlotLabel(slot)})", "NONE", null, slot, o.Gender, o.Name));
                    slots.TryAdd(slot, value);
                }
                foreach (var item in ids)
                {
                    // Déjà relié par la table du jeu (plus sûre que le nom)
                    if (InTable(item.Id, o.Gender)) { slots.TryAdd(slot, item.Id.ToString()); continue; }
                    slotItems.Add(new SlotItem(item.Id.ToString(), item.Id, item.Name, item.Rarity, item.IconUrl, slot, o.Gender, o.Name));
                    if (!index.TryGetValue(item.Id, out var list)) index[item.Id] = list = [];
                    list.Add((o.Key, p.Piece));
                    slots.TryAdd(slot, item.Id.ToString());
                }
            }
            if (slots.Count == 0) continue;
            var shop = Catalog?.BundleFor(o.Gender, o.Name, o.DisplayName);
            if (shop is { Score: >= 10 }) usedBundles.Add(shop.Item.Id);
            var bi = shop is { Score: >= 10 } ? shop.Item : null;
            bundles.Add(new BundleEntry("b:" + o.Key, bi?.Id, bi?.Name ?? o.DisplayName, bi?.Rarity ?? "NONE",
                bi?.IconUrl ?? o.Pieces.Select(p => p.Item).FirstOrDefault(i => i != null)?.IconUrl,
                o.Gender, true, slots, o.Name));
        }
        if (Catalog != null)
        {
            // Objets reliés exactement par la table « Clothes » du jeu et ses recettes
            var byKey = unique.ToDictionary(o => o.Key);
            foreach (var (id, links) in tableLinks)
                if (Catalog.Get(id) is { } item)
                    foreach (var (key, piece) in links)
                        if (byKey.TryGetValue(key, out var o) && o.PieceSources.ContainsKey(piece) && SlotOf(piece, o.IsCharacter) is { } slot)
                            Link(item, key, piece, slot);
            // Puis par le nom : chaque vêtement encore sans modèle prend la pièce dont le nom ressemble le plus à son icône
            // (coloris « hunter_A » d'une recette, ou tenue dont une autre icône avait le meilleur score)
            foreach (var gender in new[] { "Male", "Female" })
            {
                var sources = unique.Where(o => o.Gender == gender)
                    .SelectMany(o => o.Pieces.Where(p => SlotOf(p.Piece, o.IsCharacter) != null).Select(p => (Outfit: o, p.Piece))).ToList();
                var best = Catalog.BestSources(gender, sources.Select(s => (s.Outfit.Name, s.Piece)).ToList(),
                    i => i.Type == "CLOTHES" && !(index.TryGetValue(i.Id, out var l) && l.Any(t => t.Key.StartsWith(gender + ":"))), MinNameScore);
                foreach (var (id, i) in best)
                    Link(Catalog.Get(id)!, sources[i].Outfit.Key, sources[i].Piece, SlotOf(sources[i].Piece, false)!);
            }
        }

        var slotsById = slotItems.Where(i => i.Id != null).GroupBy(i => i.Id!.Value).ToDictionary(g => g.Key, g => g.ToList());
        if (Catalog != null)
            foreach (var b in Catalog.Bundles.Where(b => !usedBundles.Contains(b.Id)))
            {
                // Bundle sans tenue à son nom : on le compose des vêtements dont l'icône reprend la sienne
                var best = Catalog.BundleContents(b).SelectMany(i => slotsById.GetValueOrDefault(i.Id) ?? [])
                    .GroupBy(i => i.Gender).Select(g => (Gender: g.Key, Slots: g.GroupBy(i => i.Slot).ToDictionary(s => s.Key, s => s.Min(i => i.Id!.Value).ToString())))
                    .OrderByDescending(g => g.Slots.Count).FirstOrDefault();
                if (best.Slots?.Count >= 2)
                    bundles.Add(new BundleEntry("i:" + b.Id, b.Id, b.Name, b.Rarity, b.IconUrl, best.Gender, true, best.Slots, null));
                // Bundles dont les fichiers ne sont pas (encore) téléchargés par le jeu
                else bundles.Add(new BundleEntry("i:" + b.Id, b.Id, b.Name, b.Rarity, b.IconUrl, b.Gender, false, [], null));
            }

        _itemIndex = index;
        SlotItems = slotItems.DistinctBy(i => (i.Value, i.Gender)).ToList();
        Bundles = bundles.OrderByDescending(b => b.Available).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    Dictionary<long, List<(string Key, string Piece)>> _itemIndex = [];

    /// <summary>
    /// La table « Clothes » ne relie que les hauts. Les autres pièces du même ensemble (« The Streets (Shoes) »,
    /// « The Streets (Pants) ») suivent le haut (« The Streets (Jacket) ») vers la même tenue, plutôt qu'une tenue
    /// au nom voisin (« Cos_Hip_hop » au lieu de « Cos_Hip_hop2 »).
    /// </summary>
    Dictionary<long, List<(string Key, string Piece)>> WithSetSiblings(
        Dictionary<long, List<(string Key, string Piece)>> links, List<OutfitEntry> outfits)
    {
        if (Catalog == null) return links;
        var result = links.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var byKey = outfits.ToDictionary(o => o.Key);
        foreach (var set in Catalog.Items.Where(i => i.Type == "CLOTHES" && i.Gender != null && i.Name.EndsWith(')'))
                     .GroupBy(i => (i.Gender, Name: SetName(i.Name))))
        {
            var keys = set.SelectMany(i => links.GetValueOrDefault(i.Id) ?? []).Select(l => l.Key)
                .Where(k => k.StartsWith(set.Key.Gender + ":")).Distinct().ToList();
            if (keys.Count != 1 || !byKey.TryGetValue(keys[0], out var outfit)) continue;
            foreach (var item in set)
                if (!result.ContainsKey(item.Id) && outfit.PieceSources.ContainsKey(item.Piece))
                    result[item.Id] = [(keys[0], item.Piece)];
        }
        return result;
    }

    static string SetName(string name) => name[..name.LastIndexOf('(')].Trim();

    static readonly Dictionary<string, string[]> BrowseTypes = new()
    {
        ["bundle"] = ["Bundle"], ["character"] = ["Character"], ["top"] = ["Top"], ["bottom"] = ["Bottom"],
        ["shoe"] = ["Shoe"], ["head"] = ["Head", "Hair", "Accessory"], ["facepaint"] = ["Facepaint"],
    };

    /// <summary>
    /// Tout le catalogue ItemID2 (bundles, vêtements, personnages), page par page comme sur le site,
    /// avec pour chaque objet ce qu'il fait dans l'appli quand son modèle est dans les fichiers.
    /// </summary>
    public BrowsePage Browse(string? query, string? type, string? gender, bool onlyAvailable, int page, int size = 60)
    {
        if (Catalog == null) return new(0, 1, 1, 0, []);
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[]? pieces = type != null && BrowseTypes.TryGetValue(type, out var t) ? t : null;
        var bundleById = Bundles.Where(b => b.Available && b.Id != null).GroupBy(b => b.Id!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var slotById = SlotItems.Where(i => i.Id != null).GroupBy(i => i.Id!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var charById = Characters.Where(c => c.Item != null).GroupBy(c => c.Item!.Id).ToDictionary(g => g.Key, g => g.ToList());

        BrowseItem Describe(GameItem i)
        {
            string? action = null, value = null, slot = null;
            bool G(string? g) => gender == null || g == gender;
            if (i.Piece == "Bundle" && bundleById.TryGetValue(i.Id, out var bl) && bl.FirstOrDefault(b => G(b.Gender)) is { } b)
                (action, value) = ("bundle", b.Value);
            else if (i.Piece == "Character" && charById.TryGetValue(i.Id, out var cl) && cl.FirstOrDefault(c => G(c.Gender)) is { } c)
                (action, value) = ("character", c.Key);
            else if (slotById.TryGetValue(i.Id, out var sl) && sl.FirstOrDefault(x => G(x.Gender)) is { } si)
                (action, value, slot) = ("slot", si.Value, si.Slot);
            return new BrowseItem(i.Id, i.Name, i.IconUrl, i.Rarity, i.Type, i.Piece, i.Gender, action, value, slot);
        }

        var all = Catalog.Items
            .Where(i => i.Piece is not "Other")
            .Where(i => pieces == null || pieces.Contains(i.Piece))
            .Where(i => gender == null || i.Gender == null || i.Gender == gender)
            .Where(i => words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Icon.Contains(w, StringComparison.OrdinalIgnoreCase)
                                       || i.Id.ToString().StartsWith(w) || i.Rarity.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Id)
            .Select(Describe).ToList();
        int available = all.Count(i => i.Action != null);
        if (onlyAvailable) all = all.Where(i => i.Action != null).ToList();
        int pages = Math.Max(1, (all.Count + size - 1) / size);
        page = Math.Clamp(page, 1, pages);
        return new(all.Count, page, pages, available, all.Skip((page - 1) * size).Take(size).ToList());
    }

    // Maquillages (ID 214…) : un par tenue qui a des overlays « Headadditive », relié aux objets ItemID2
    void BuildFacePaintViews()
    {
        var items = new List<SlotItem>();
        var index = new Dictionary<long, List<(string, string)>>();
        foreach (var g in _headLayers.Where(l => l.Kind == HeadLayerKind.FacePaint)
                     .GroupBy(l => (l.Gender, Name: l.Name.ToLowerInvariant())))
        {
            var (gender, name) = (g.Key.Gender, g.First().Name);
            var matches = Catalog?.Match(gender, name, "Facepaint").Where(m => m.Score >= 7).ToList() ?? [];
            int best = matches.Count > 0 ? matches[0].Score : 0;
            var linked = matches.Where(m => m.Score == best).ToList();
            if (linked.Count == 0)
                items.Add(new SlotItem($"f:{gender}:{name}", null, Regex.Replace(name, @"^(sc|cos)_", "", RegexOptions.IgnoreCase).Replace('_', ' ') + " (maquillage)",
                    "NONE", null, "Facepaint", gender, name));
            foreach (var m in linked)
            {
                items.Add(new SlotItem(m.Item.Id.ToString(), m.Item.Id, m.Item.Name, m.Item.Rarity, m.Item.IconUrl, "Facepaint", gender, name));
                if (!index.TryGetValue(m.Item.Id, out var list)) index[m.Item.Id] = list = [];
                list.Add((gender, name));
            }
        }
        _facePaintIndex = index;
        // Un bundle dont la tenue a un maquillage le met aussi dans l'emplacement Maquillage
        foreach (var b in Bundles.Where(b => b.Available && b.Outfit != null && b.Gender != null))
        {
            var fp = items.FirstOrDefault(i => i.Gender == b.Gender && i.Outfit.Equals(b.Outfit, StringComparison.OrdinalIgnoreCase));
            if (fp != null) b.Slots.TryAdd("Facepaint", fp.Value);
        }
        SlotItems = SlotItems.Where(i => i.Slot != "Facepaint").Concat(items.DistinctBy(i => (i.Value, i.Gender))).ToList();
    }

    /// <summary>Calques d'une tenue (maquillage ou racines), décodés une fois puis gardés en mémoire.</summary>
    List<HeadLayer> HeadLayers(HeadLayerKind kind, string gender, string name, GameData? data)
    {
        var key = (kind, gender, name.ToLowerInvariant());
        lock (_layerCache)
            if (_layerCache.TryGetValue(key, out var cached)) return cached;
        var refs = _headLayers.Where(l => l.Kind == kind && l.Gender == gender && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Overlay, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<HeadLayer>();
        foreach (var group in refs.GroupBy(r => r.Bundle))
        {
            try
            {
                if (group.Key == null)
                {
                    if (data == null) continue;
                    foreach (var r in group)
                        if (data.Find(r.Overlay, AssetsTools.NET.Extra.AssetClassID.MonoBehaviour) is { } ov)
                        {
                            var raw = data.Raw(ov);
                            bool IsTex(FFSkinExtractor.Core.PPtr p) => data.Resolve(ov.File, p.FileId, p.PathId)?.Type == AssetsTools.NET.Extra.AssetClassID.Texture2D;
                            DecodedTexture? Tex(FFSkinExtractor.Core.PPtr p) => data.Resolve(ov.File, p.FileId, p.PathId) is { } t ? data.Texture(t) : null;
                            if ((HeadPaint.Parse(r.Overlay, raw, false, IsTex, Tex) ?? HeadPaint.Parse(r.Overlay, raw, true, IsTex, Tex)) is { } layer)
                                result.Add(layer);
                        }
                    continue;
                }
                using var bundle = new GameBundle(group.Key);
                var wanted = group.ToDictionary(r => r.Overlay, StringComparer.Ordinal);
                foreach (var o in bundle.UmaObjects())
                {
                    if (!wanted.TryGetValue(o.Name, out var r)) continue;
                    var layer = HeadPaint.Parse(r.Overlay, o.Raw, r.Atlas,
                        p => p.FileId != 0 || bundle.GetInfo(p.PathId)?.TypeId == (int)AssetsTools.NET.Extra.AssetClassID.Texture2D,
                        p => p.FileId != 0
                            ? (bundle.ExternalName(p.FileId) is { } cab ? ExternalTexture(cab, p.PathId) : null)
                            : bundle.GetInfo(p.PathId) is { } info ? TextureDecoder.Decode(bundle, info) : null);
                    if (layer != null) result.Add(layer);
                }
            }
            catch (Exception) { /* calque illisible : le visage reste tel quel */ }
        }
        lock (_layerCache) _layerCache[key] = result;
        return result;
    }

    /// <summary>
    /// Calques à peindre sur le visage : racines des cheveux portés (ceux de la tenue choisie, du personnage,
    /// ou du personnage de base), puis le maquillage choisi ou celui du personnage.
    /// </summary>
    List<HeadLayer> FaceLayersFor(Selection sel, GameData? data)
    {
        var layers = new List<HeadLayer>();
        string? hairFrom = sel.Pieces.TryGetValue("Hair", out var hairId) ? Get(hairId)?.Name : sel.Character;
        if (hairFrom != null) layers.AddRange(HeadLayers(HeadLayerKind.HairRoot, sel.Gender, hairFrom, data));
        // Coiffure choisie sans racines à elle (casquette, chapeau) : le jeu garde dessous les cheveux ras du personnage
        // de base. Pas pour un personnage sans coiffure choisie : il peut être chauve.
        if (layers.Count == 0 && (hairId != null || sel.Character == null) && sel.Base && data != null && BaseHairRoot(data, sel.Gender) is { } baseRoot)
            layers.AddRange(HeadLayers(HeadLayerKind.HairRoot, sel.Gender, baseRoot, data));
        string? paint = sel.FacePaint ?? sel.Character;
        if (paint != null) layers.AddRange(HeadLayers(HeadLayerKind.FacePaint, sel.Gender, paint, data));
        return layers;
    }

    // Racines de cheveux de la recette du personnage de base (« Female_Hairroot_Sc_Agent_Overlay »)
    static string? BaseHairRoot(GameData data, string gender)
    {
        var recipe = data.Find($"{gender}BaseRecipe_max_N", AssetsTools.NET.Extra.AssetClassID.MonoBehaviour);
        if (recipe == null) return null;
        foreach (var (_, overlay) in UmaAssets.ParseBaseRecipe(data.Raw(recipe.Value)))
            if (overlay != null && HeadPaint.Classify(overlay) is { Kind: HeadLayerKind.HairRoot } c) return c.Name;
        return null;
    }

    public static string SlotLabel(string slot) => slot switch
    {
        "Hair" => "Cheveux", "Mask" => "Masque", "Head" => "Tête", "Top" => "Haut", "Bottom" => "Bas",
        "Shoe" => "Chaussures", "Hands" => "Mains", "Facepaint" => "Maquillage", _ => "Autre",
    };

    // Tenue qui fournit cette pièce, de préférence dans la qualité demandée
    OutfitEntry? ByPiece(string key, string piece, bool hd) =>
        Outfits.Where(o => o.Key == key && o.PieceSources.ContainsKey(piece)).OrderByDescending(o => o.HighDetail == hd).FirstOrDefault();

    OutfitEntry? ByKey(string key, bool hd) =>
        Outfits.FirstOrDefault(o => o.Key == key && o.HighDetail == hd) ?? Outfits.FirstOrDefault(o => o.Key == key);

    /// <summary>
    /// Traduit un personnage et des ID d'objets en pièces : la tenue d'origine du personnage,
    /// puis chaque ID remplace la pièce de son emplacement. Renvoie aussi les valeurs non reconnues.
    /// </summary>
    public (Selection Selection, List<string> Unknown) Resolve(Look look)
    {
        var pieces = new Dictionary<string, string>();
        var unknown = new List<string>();
        string? name = null;
        if (look.Character is { Length: > 0 } ck && ByKey(ck, look.HighDetail) is { } ch)
        {
            foreach (var p in ch.Pieces) pieces[p.Piece] = ch.SourceOf(p.Piece);
            name = ch.Name;
        }
        string? character = null, facePaint = null;
        if (look.Character is { Length: > 0 } ck2 && ByKey(ck2, look.HighDetail) is { } chr) character = chr.Name;
        foreach (var (slot, raw) in look.Slots ?? [])
        {
            string value = raw.Trim();
            if (value.Length == 0) continue;
            if (slot == "Facepaint" || value.StartsWith("f:") || (long.TryParse(value, out long fid) && _facePaintIndex.ContainsKey(fid)))
            {
                string? fp = value.StartsWith("f:") && value.Split(':') is [_, var fg, var fn] && fg == look.Gender ? fn
                    : long.TryParse(value, out fid) && _facePaintIndex.TryGetValue(fid, out var fl)
                        ? fl.Where(f => f.Gender == look.Gender).Select(f => f.Name).FirstOrDefault() : null;
                if (fp == null) unknown.Add(value); else facePaint = fp;
                continue;
            }
            (string Key, string Piece)? target = null;
            if (long.TryParse(value, out long id) && _itemIndex.TryGetValue(id, out var list))
                target = list.Where(t => t.Key.StartsWith(look.Gender + ":")).Cast<(string, string)?>().FirstOrDefault();
            else if (value.StartsWith("o:"))
            {
                int cut = value.LastIndexOf(':');
                target = (value[2..cut], value[(cut + 1)..]);
            }
            if (target is not { } t || !t.Key.StartsWith(look.Gender + ":") || ByPiece(t.Key, t.Piece, look.HighDetail) is not { } entry)
            {
                unknown.Add(value);
                continue;
            }
            pieces[t.Piece] = entry.SourceOf(t.Piece);
            name ??= entry.Name;
        }
        var sel = new Selection(look.Gender, look.HighDetail, pieces, look.Base, look.Skinned, look.Pbr)
            { Name = $"{look.Gender}_{name ?? "Base"}{(look.HighDetail ? "_HD" : "")}", Character = character, FacePaint = facePaint };
        return (sel, unknown);
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

    public OutfitEntry? Get(string id) => _entryById.GetValueOrDefault(id);

    /// <summary>Assemble les pièces choisies (et le corps de base) et renvoie le GLB.</summary>
    public async Task<byte[]> BuildAsync(Selection sel, CancellationToken ct = default)
    {
        string key = string.Join("|", sel.Gender, sel.HighDetail, sel.Base, sel.Skinned, sel.Pbr,
            string.Join(",", sel.Pieces.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)), sel.Name, sel.Character, sel.FacePaint);
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
    public string DefaultName(Selection sel) => sel.Name ?? (sel.Pieces.Count == 0 ? $"{sel.Gender}_Base"
        : $"{sel.Gender}_{Get(sel.Pieces.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key)?.Name ?? "Tenue"}"
          + (sel.HighDetail ? "_HD" : ""));

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
            parts = HeadPaint.Apply(parts, FaceLayersFor(sel, data));
            return use(DefaultName(sel), parts);
        }
        finally { _build.Release(); }
    }

    /// <summary>Sélection qui reprend toutes les pièces d'une tenue.</summary>
    public static Selection Full(OutfitEntry o, bool withBase = true, bool skinned = true, bool pbr = true) =>
        new(o.Gender, o.HighDetail, o.Pieces.ToDictionary(p => p.Piece, p => o.SourceOf(p.Piece)), withBase, skinned, pbr);

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
        if (entry.Recipe != null) return LoadRecipe(entry, skin);
        return OpenOutfits(entry.BundlePath, entry.HighDetail, skin).FirstOrDefault(o => o.Gender == entry.Gender && o.Name == entry.Name)
            ?? throw new KeyNotFoundException($"{entry.Name} absent de {entry.Bundle}");
    }

    // Tenue d'une recette : chaque slot est pris dans son bundle, puis habillé de l'overlay de la recette
    Outfit LoadRecipe(OutfitEntry entry, SkinTextures? skin)
    {
        string piece = entry.Pieces[0].Piece;
        var parts = new List<OutfitPart>();
        foreach (var rp in entry.Recipe!)
        {
            var part = rp.SlotBundle == ApkSource ? ApkPart(rp.SlotName, entry.Gender, skin)
                : OpenOutfits(rp.SlotBundle, rp.SlotHd, skin).SelectMany(o => o.Parts).FirstOrDefault(p => p.SlotName == rp.SlotName);
            if (part == null) continue;
            var own = part.TextureSource;
            var textures = rp.Overlay == null || part.IsSkin ? own
                : new Lazy<TextureSet>(() => LoadOverlay(rp.OverlayBundle!, rp.Overlay) is { BaseColor: not null } set ? set : own.Value);
            parts.Add(part with { Piece = piece, TextureSource = textures });
        }
        if (parts.Count == 0) throw new KeyNotFoundException($"{entry.Name} : slots de la recette introuvables");
        return new Outfit(entry.Name, entry.Gender, parts);
    }

    // Pièce d'un slot des données de l'APK (mêmes règles de peau et de matériau que dans les bundles)
    OutfitPart? ApkPart(string slotName, string gender, SkinTextures? skin)
    {
        if (_gameData is not { IsCompletedSuccessfully: true, Result: { } data } || !_apkSlots.TryGetValue(slotName, out var slot)) return null;
        var (obj, info) = slot;
        if (data.Resolve(obj.File, info.Mesh.FileId, info.Mesh.PathId) is not { } meshObj) return null;
        UmaMesh mesh;
        try { mesh = UmaMeshDecoder.Decode(meshObj.Name, data.Raw(meshObj)); }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return null; }
        string material = data.Resolve(obj.File, info.Material.FileId, info.Material.PathId)?.Name ?? "";
        bool isSkin = HighDetailSlot().IsMatch(slotName)
            ? material.Contains("AvatarSkin") && !material.Contains("Emit") && mesh.Submeshes.Count > 1
            : UvCoverage.SkinSubmeshes(mesh).Contains(info.SubmeshIndex);
        var kind = material.Contains("CutOff", StringComparison.OrdinalIgnoreCase) || material.Contains("AvatarHair") ? MaterialKind.Cutout
            : material.Contains("Glass", StringComparison.OrdinalIgnoreCase) ? MaterialKind.Transparent
            : MaterialKind.Opaque;
        var textures = isSkin ? skin?.For(gender) ?? TextureSet.Empty : TextureSet.Empty;
        return new OutfitPart(slotName, mesh, info.SubmeshIndex, textures, isSkin, kind, OutfitReader.PieceOf(slotName.Split('_', 2)[^1]));
    }

    List<Outfit> OpenOutfits(string path, bool highDetail, SkinTextures? skin)
    {
        string key = path + "|" + highDetail;
        var node = _open.First;
        while (node != null && node.Value.Key != key) node = node.Next;
        if (node == null)
        {
            var bundle = new GameBundle(path);
            var outfits = OutfitReader.Read(bundle, new OutfitReadOptions
            {
                HighDetail = highDetail,
                Skin = skin,
                External = ExternalTexture,
                ExternalOverlay = ExternalOverlay,
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
        return node.Value.Outfits;
    }

    // Overlay d'un autre bundle (ex. coloris dont les textures sont rangées ailleurs)
    TextureSet? ExternalOverlay(IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!_overlayIndex.TryGetValue(key, out var at)) continue;
            if (LoadOverlay(at.Bundle, at.Overlay) is { BaseColor: not null } set) return set;
        }
        return null;
    }

    // Textures d'un overlay désigné par son nom, gardées en mémoire
    TextureSet? LoadOverlay(string path, string overlay)
    {
        lock (_externalOverlays)
            if (_externalOverlays.TryGetValue(overlay, out var cached)) return cached;
        TextureSet? set = null;
        try
        {
            if (path == ApkSource)
                set = _gameData is { IsCompletedSuccessfully: true, Result: { } data } ? data.Overlay(overlay) : null;
            else
            {
                using var bundle = new GameBundle(path);
                var o = bundle.UmaObjects().FirstOrDefault(x => x.Name == overlay);
                if (o != null)
                {
                    var refs = UmaAssets.OverlayTextures(o.Raw, p => p.FileId != 0
                        || bundle.GetInfo(p.PathId)?.TypeId == (int)AssetsTools.NET.Extra.AssetClassID.Texture2D);
                    set = TextureSet.FromOverlay(refs.Select(p => p.FileId != 0
                        ? (bundle.ExternalName(p.FileId) is { } cab ? ExternalTexture(cab, p.PathId) : null)
                        : bundle.GetInfo(p.PathId) is { } info ? TextureDecoder.Decode(bundle, info) : null).ToList());
                }
            }
        }
        catch (Exception) { set = null; }
        lock (_externalOverlays) _externalOverlays[overlay] = set;
        return set;
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
