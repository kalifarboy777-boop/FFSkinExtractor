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
    version = assembly.GetName().Version?.ToString(3),
});

app.MapPost("/api/settings", (AppSettings s) =>
{
    var clean = s with
    {
        DataFolder = Clean(s.DataFolder),
        GamePackage = Clean(s.GamePackage),
        ExportFolder = Clean(s.ExportFolder) ?? new AppSettings().ExportFolder,
    };
    clean.Save();
    library.StartScan(clean);
    return Results.Ok();
});

app.MapGet("/api/outfits", () => library.Outfits);

app.MapPost("/api/model", async (Selection sel, CancellationToken ct) =>
{
    try { return Results.File(await library.BuildAsync(sel, ct), "model/gltf-binary"); }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.Problem(e.Message); }
});

app.MapPost("/api/export", async (ExportRequest req, CancellationToken ct) =>
{
    try
    {
        var glb = await library.BuildAsync(req.Selection, ct);
        string dir = library.Settings.ExportFolder;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, SafeName(req.FileName ?? library.DefaultName(req.Selection)) + ".glb");
        await File.WriteAllBytesAsync(path, glb, ct);
        if (req.Textures)
        {
            string texDir = Path.ChangeExtension(path, null) + "_textures";
            Directory.CreateDirectory(texDir);
            foreach (var t in await library.TexturesAsync(req.Selection, ct))
                await File.WriteAllBytesAsync(Path.Combine(texDir, SafeName(t.Name) + ".png"), t.Png, ct);
        }
        return Results.Ok(new { path });
    }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.Problem(e.Message); }
});

app.MapPost("/api/textures", async (Selection sel, CancellationToken ct) =>
{
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
    var list = library.Outfits.Where(o => o.HighDetail == req.HighDetail && (req.Gender == null || o.Gender == req.Gender)).ToList();
    if (!batch.TryStart(list.Count)) return Results.Conflict("Un export est déjà en cours.");
    string dir = Path.Combine(library.Settings.ExportFolder, req.HighDetail ? "Toutes_HD" : "Toutes_en_jeu");
    _ = Task.Run(async () =>
    {
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var o in list)
            {
                if (batch.Cancelled) break;
                batch.Current = o.DisplayName;
                try
                {
                    var glb = await library.BuildAsync(Library.Full(o, req.Base));
                    await File.WriteAllBytesAsync(Path.Combine(dir, SafeName($"{o.Gender}_{o.Name}{(o.HighDetail ? "_HD" : "")}") + ".glb"), glb);
                }
                catch (Exception e) { batch.Errors.Add($"{o.Name} : {e.Message}"); }
                batch.Done++;
            }
        }
        catch (Exception e) { batch.Errors.Add(e.Message); }
        finally { batch.Finish(dir); }
    });
    return Results.Ok(new { dir });
});

app.MapPost("/api/export-all/cancel", () => { batch.Cancelled = true; return Results.Ok(); });

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

record ExportRequest(Selection Selection, string? FileName, bool Textures = false);
record BatchRequest(bool HighDetail = true, string? Gender = null, bool Base = true);

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
