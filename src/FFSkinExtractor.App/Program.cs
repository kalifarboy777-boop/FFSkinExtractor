using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFSkinExtractor.App;
using FFSkinExtractor.Core.Items;

// FF Skin Studio : interface locale (navigateur) pour parcourir les tenues, les prévisualiser en 3D
// et les exporter en GLB. Utilisation : FFSkinStudio [dossier des données] [--port N] [--no-browser]
var settings = AppSettings.Load();
string? folderArg = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
if (folderArg != null)
{
    settings = settings with { DataFolder = Path.GetFullPath(folderArg) };
    settings.Save();
}
else if (settings.DataFolder == null && AppSettings.Suggestions().FirstOrDefault() is { } guess)
    settings = settings with { DataFolder = guess };
int port = int.TryParse(Opt("--port"), out var p) ? p : 5178;

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
});
var app = builder.Build();
using var library = new Library(settings);
var batch = new BatchJob();
var pull = new DevicePull();

// ---- Interface (fichiers intégrés à l'exécutable) ----
var assembly = Assembly.GetExecutingAssembly();
app.MapGet("/", () => Embedded("index.html"));
app.MapGet("/{**path}", (string path) => Embedded(path));

// ---- API ----
app.MapGet("/api/status", () => new
{
    state = library.State.ToString(),
    library.Progress,
    library.Message,
    settings = library.Settings,
    gamePackage = library.GamePackage,
    library.BundleCount,
    baseReady = library.BaseReady,
    catalog = library.Catalog?.Items.Count ?? 0,
    credit = ItemCatalog.Credit,
    suggestions = AppSettings.Suggestions().ToList(),
    batch = batch.Snapshot(),
    pull = pull.Snapshot(),
    arrivals = new { stamp = library.ArrivalStamp, items = library.Arrivals },
    version = assembly.GetName().Version?.ToString(3),
});

app.MapPost("/api/settings", (AppSettings s) =>
{
    var clean = s with
    {
        DataFolder = Clean(s.DataFolder),
        GamePackage = Clean(s.GamePackage),
        AdbPath = Clean(s.AdbPath),
        ExportFolder = Clean(s.ExportFolder) ?? new AppSettings().ExportFolder,
    };
    clean.Save();
    library.StartScan(clean);
    return Results.Ok();
});

app.MapGet("/api/outfits", () => library.Outfits);

app.MapGet("/api/catalog", () => new
{
    characters = library.Characters,
    items = library.SlotItems,
    bundles = library.Bundles,
});

// Tout le catalogue ItemID2, par pages
app.MapGet("/api/browse", (string? q, string? type, string? gender, bool? available, int? page) =>
    library.Browse(q, type, gender, available ?? false, page ?? 1));

// Objet du catalogue ItemID2, même s'il n'est pas dans les fichiers téléchargés
app.MapGet("/api/lookup/{id:long}", (long id) => library.Catalog?.Get(id) is { } i
    ? Results.Ok(new { i.Id, i.Name, i.Rarity, i.IconUrl, i.Piece, i.Type })
    : Results.NotFound());

app.MapPost("/api/model", async (Look look, CancellationToken ct) =>
{
    try
    {
        var (sel, unknown) = library.Resolve(look);
        var glb = await library.BuildAsync(sel, ct);
        return Results.File(glb, "model/gltf-binary", sel.Name + ".glb");
    }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.Problem(e.Message); }
});

app.MapPost("/api/resolve", (Look look) =>
{
    var (sel, unknown) = library.Resolve(look);
    return new { name = library.DefaultName(sel), unknown };
});

app.MapPost("/api/export", async (ExportRequest req, CancellationToken ct) =>
{
    try
    {
        var (selection, _) = library.Resolve(req.Look);
        var glb = await library.BuildAsync(selection, ct);
        string dir = library.Settings.ExportFolder;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, SafeName(req.FileName ?? library.DefaultName(selection)) + ".glb");
        await File.WriteAllBytesAsync(path, glb, ct);
        if (req.Textures)
        {
            string texDir = Path.ChangeExtension(path, null) + "_textures";
            Directory.CreateDirectory(texDir);
            foreach (var t in await library.TexturesAsync(selection, ct))
                await File.WriteAllBytesAsync(Path.Combine(texDir, SafeName(t.Name) + ".png"), t.Png, ct);
        }
        return Results.Ok(new { path });
    }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.Problem(e.Message); }
});

app.MapPost("/api/textures", async (Look look, CancellationToken ct) =>
{
    var (sel, _) = library.Resolve(look);
    var mem = new MemoryStream();
    using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, true))
        foreach (var t in await library.TexturesAsync(sel, ct))
        {
            using var s = zip.CreateEntry(SafeName(t.Name) + ".png", CompressionLevel.Fastest).Open();
            await s.WriteAsync(t.Png, ct);
        }
    return Results.File(mem.ToArray(), "application/zip", library.DefaultName(sel) + "_textures.zip");
});

app.MapPost("/api/export-all", (BatchRequest req) =>
{
    // Chaque bundle disponible, porté par le personnage choisi
    var list = library.Bundles.Where(b => b.Available && b.Gender == req.Gender).ToList();
    if (!batch.TryStart(list.Count)) return Results.Conflict("Un export est déjà en cours.");
    string who = req.Character is { Length: > 0 } ck ? library.Characters.FirstOrDefault(c => c.Key == ck)?.Internal ?? "Base" : "Base";
    string dir = Path.Combine(library.Settings.ExportFolder, $"Bundles_{req.Gender}_{who}{(req.HighDetail ? "_HD" : "")}");
    _ = Task.Run(async () =>
    {
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var b in list)
            {
                if (batch.Cancelled) break;
                batch.Current = b.Name;
                try
                {
                    var (sel, _) = library.Resolve(new Look(req.Gender, req.HighDetail, req.Character, b.Slots, req.Base));
                    var glb = await library.BuildAsync(sel);
                    string file = (b.Id is { } id ? $"{id}_" : "") + b.Name;
                    await File.WriteAllBytesAsync(Path.Combine(dir, SafeName(file) + ".glb"), glb);
                }
                catch (Exception e) { batch.Errors.Add($"{b.Name} : {e.Message}"); }
                batch.Done++;
            }
        }
        catch (Exception e) { batch.Errors.Add(e.Message); }
        finally { batch.Finish(dir); }
    });
    return Results.Ok(new { dir });
});

app.MapPost("/api/export-all/cancel", () => { batch.Cancelled = true; return Results.Ok(); });

// ---- Récupération des skins téléchargés par le jeu sur un téléphone ou un émulateur (ADB) ----
app.MapGet("/api/devices", async (CancellationToken ct) =>
{
    if (DevicePull.FindAdb(library.Settings.AdbPath) is not { } adb)
        return Results.Ok(new { adb = (string?)null, devices = new List<AndroidDevice>() });
    var devices = await DevicePull.ListDevicesAsync(adb, ct);
    return Results.Ok(new { adb = (string?)adb, devices });
});

app.MapPost("/api/pull", async (PullRequest req, CancellationToken ct) =>
{
    if (library.Settings.DataFolder is not { } folder || !Directory.Exists(folder))
        return Results.Problem("Choisis d'abord le dossier des données dans les réglages.");
    if (DevicePull.FindAdb(library.Settings.AdbPath) is not { } adb) return Results.Problem("ADB introuvable.");
    var device = (await DevicePull.ListDevicesAsync(adb, ct)).FirstOrDefault(d => d.Serial == req.Serial);
    if (device == null) return Results.Problem("Appareil déconnecté.");
    if (device.Games.Count == 0) return Results.Problem("Free Fire n'est pas installé sur cet appareil (ou n'a encore rien téléchargé).");
    // « Récupérer » pendant le suivi en direct : on arrête le suivi d'abord
    if (pull.Watching)
    {
        pull.Cancel();
        for (int i = 0; i < 50 && pull.Running; i++) await Task.Delay(100, ct);
    }
    // Nouvelle analyse une fois la copie finie, pour que les skins récupérés apparaissent
    if (!pull.TryStart(adb, device, folder, library.AddArrivals))
        return Results.Conflict("Une récupération est déjà en cours.");
    return Results.Ok();
});

// Suivi en direct : chaque skin que le jeu télécharge (équipé ou essayé pour la première fois) arrive tout seul
app.MapPost("/api/watch", async (PullRequest req, CancellationToken ct) =>
{
    if (library.Settings.DataFolder is not { } folder || !Directory.Exists(folder))
        return Results.Problem("Choisis d'abord le dossier des données dans les réglages.");
    if (DevicePull.FindAdb(library.Settings.AdbPath) is not { } adb) return Results.Problem("ADB introuvable.");
    var device = (await DevicePull.ListDevicesAsync(adb, ct)).FirstOrDefault(d => d.Serial == req.Serial);
    if (device == null) return Results.Problem("Appareil déconnecté.");
    if (device.Games.Count == 0) return Results.Problem("Free Fire n'est pas installé sur cet appareil.");
    if (pull.Watching) return Results.Ok();   // déjà en cours
    if (!pull.TryWatch(adb, device, folder, library.AddArrivals))
        return Results.Conflict("Une récupération est déjà en cours.");
    return Results.Ok();
});

app.MapPost("/api/pull/cancel", () => { pull.Cancel(); return Results.Ok(); });

app.MapPost("/api/open-folder", () =>
{
    string dir = library.Settings.ExportFolder;
    Directory.CreateDirectory(dir);
    OpenShell(dir);
    return Results.Ok();
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    string url = $"http://127.0.0.1:{port}/";
    Console.WriteLine($"FF Skin Studio est lancé : {url}");
    Console.WriteLine("Laisse cette fenêtre ouverte pendant que tu utilises l'outil (Ctrl+C pour quitter).");
    if (!args.Contains("--no-browser")) OpenShell(url);
});

app.Run();
return;

IResult Embedded(string path)
{
    if (path.StartsWith("api/")) return Results.NotFound();
    var stream = assembly.GetManifestResourceStream("www/" + path.Replace('\\', '/'));
    if (stream == null) return Results.NotFound();
    string type = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };
    return Results.Stream(stream, type);
}

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string? Clean(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');

static string SafeName(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
        name = name.Replace(c, '_');
    return name.Trim();
}

static void OpenShell(string target)
{
    try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
    catch (Exception) { /* pas de navigateur ou d'explorateur : l'adresse est affichée dans la console */ }
}

record PullRequest(string Serial);
record ExportRequest(Look Look, string? FileName, bool Textures = false);
record BatchRequest(string Gender, bool HighDetail = true, string? Character = null, bool Base = true);

/// <summary>Export de toutes les tenues, suivi par l'interface.</summary>
sealed class BatchJob
{
    readonly object _sync = new();
    public bool Running { get; private set; }
    public int Total { get; private set; }
    public int Done { get; set; }
    public string? Current { get; set; }
    public string? Folder { get; private set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = [];

    public bool TryStart(int total)
    {
        lock (_sync)
        {
            if (Running) return false;
            Running = true; Total = total; Done = 0; Cancelled = false; Folder = null; Current = null;
            Errors.Clear();
            return true;
        }
    }

    public void Finish(string folder) { lock (_sync) { Running = false; Folder = folder; Current = null; } }

    public object Snapshot()
    {
        lock (_sync) return new { running = Running, total = Total, done = Done, current = Current, folder = Folder, errors = Errors.ToList(), cancelled = Cancelled };
    }
}
