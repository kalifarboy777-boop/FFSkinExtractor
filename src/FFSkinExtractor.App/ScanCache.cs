using System.Text.Json;
using FFSkinExtractor.Core;

namespace FFSkinExtractor.App;

/// <summary>
/// Inventaire des bundles déjà lus, gardé entre deux lancements : un bundle dont la taille et la date
/// n'ont pas changé n'est pas relu (le jeu en télécharge plusieurs centaines).
/// </summary>
sealed class ScanCache
{
    // À augmenter quand la lecture des tenues change, pour relire tous les bundles
    const int Version = 2;

    public sealed record PartSummary(string Piece, string SlotName);
    public sealed record OutfitSummary(string Gender, string Name, bool HighDetail, List<PartSummary> Parts);
    public sealed record Layer(string Gender, HeadLayerKind Kind, string Name, string Overlay, bool Atlas);
    public sealed record BundleScan(string? Cab, List<OutfitSummary> Outfits, List<Layer> Layers, List<(string Key, string Overlay)> Overlays);

    sealed record Entry(long Size, long Modified, string? Cab, List<OutfitSummary> Outfits, List<Layer> Layers, List<string[]> Overlays);
    sealed record FileData(int Version, Dictionary<string, Entry> Bundles);

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static string FilePath => Path.Combine(AppSettings.Folder, "scan-cache.json");

    readonly Dictionary<string, Entry> _entries;

    ScanCache(Dictionary<string, Entry> entries) => _entries = entries;

    public static ScanCache Load()
    {
        try
        {
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath), Json);
            if (data?.Version == Version) return new ScanCache(data.Bundles);
        }
        catch (Exception) { /* cache absent ou abîmé : tout est relu */ }
        return new ScanCache([]);
    }

    public BundleScan? Get(string path)
    {
        if (!_entries.TryGetValue(path, out var e)) return null;
        var info = new FileInfo(path);
        if (info.Length != e.Size || info.LastWriteTimeUtc.Ticks != e.Modified) return null;
        return new BundleScan(e.Cab, e.Outfits, e.Layers, e.Overlays.Select(o => (o[0], o[1])).ToList());
    }

    public void Save(IReadOnlyList<string> paths, IReadOnlyList<BundleScan?> scans)
    {
        var bundles = new Dictionary<string, Entry>();
        for (int i = 0; i < paths.Count; i++)
        {
            if (scans[i] is not { } s) continue;
            var info = new FileInfo(paths[i]);
            bundles[paths[i]] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, s.Cab, s.Outfits, s.Layers,
                s.Overlays.Select(o => new[] { o.Key, o.Overlay }).ToList());
        }
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileData(Version, bundles), Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception) { /* pas de cache : la prochaine analyse relira tout */ }
    }
}
