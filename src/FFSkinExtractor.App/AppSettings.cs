using System.Text.Json;

namespace FFSkinExtractor.App;

/// <summary>Réglages enregistrés entre deux lancements (dossier des données, paquet du jeu, dossier d'export).</summary>
public sealed record AppSettings
{
    /// <summary>Dossier qui contient les bundles du jeu (et souvent le .xapk).</summary>
    public string? DataFolder { get; init; }
    /// <summary>.xapk, .apk ou asset_pack_install_time.apk ; cherché dans le dossier des données si vide.</summary>
    public string? GamePackage { get; init; }
    /// <summary>adb.exe pour récupérer les skins d'un téléphone ou d'un émulateur ; cherché automatiquement si vide.</summary>
    public string? AdbPath { get; init; }
    public string ExportFolder { get; init; } = Path.Combine(Documents, "FFSkinStudio");

    static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } d
        ? d : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "FFSkinStudio");

    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Emplacements habituels du dossier des données, pour le préremplir au premier lancement.</summary>
    public static IEnumerable<string> Suggestions()
    {
        string docs = Documents;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { Path.Combine(docs, "FFdata"), Path.Combine(home, "FFdata"), Path.Combine(home, "Downloads", "FFdata") })
            if (Directory.Exists(candidate)) yield return candidate;
    }
}
