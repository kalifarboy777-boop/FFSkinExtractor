using FFSkinExtractor.Core;

// ffextract list   <dossier-ou-bundle>
// ffextract export <bundle> [--outfit NOM] [--hd] [--out dossier] [--apk fichier.xapk]
if (args.Length < 2 || args[0] is not ("list" or "export"))
{
    Console.WriteLine("""
        Utilisation :
          ffextract list   <dossier files du jeu | bundle>   liste les tenues trouvées
          ffextract export <bundle> [--outfit NOM] [--hd] [--out DOSSIER] [--apk JEU.xapk]
                                                             exporte les tenues en .glb
                                                             (--apk : textures de peau du corps de base)
        """);
    return 1;
}

bool hd = args.Contains("--hd");
string? outfitFilter = Opt("--outfit");
string outDir = Opt("--out") ?? Directory.GetCurrentDirectory();
SkinTextures? skin = null;
if (Opt("--apk") is { } apk)
{
    Console.WriteLine("Lecture des textures de peau dans l'APK…");
    skin = SkinTextures.Load(apk);
    if (skin.Female == null) Console.WriteLine("  texture de peau femme introuvable, couleur unie à la place");
    if (skin.Male == null) Console.WriteLine("  texture de peau homme introuvable, couleur unie à la place");
}

var bundles = Directory.Exists(args[1])
    ? Directory.EnumerateFiles(args[1], "*", SearchOption.AllDirectories)
        .Where(f => Path.GetFileName(f).StartsWith("optionalab_avatar") || Path.GetFileName(f).StartsWith("optionalab_legendcloth")
                    || Path.GetFileName(f).StartsWith("optionalab_tailor"))
    : [args[1]];

int errors = 0;
foreach (var file in bundles.OrderBy(f => f))
{
    string label = Path.GetFileName(file).Split('.')[0];
    if (!GameBundle.IsDownloaded(file)) { Console.WriteLine($"{label} : pas encore téléchargé par le jeu"); continue; }
    try
    {
        using var bundle = new GameBundle(file);
        foreach (var outfit in OutfitReader.Read(bundle, hd, skin))
        {
            if (outfitFilter != null && !outfit.Name.Contains(outfitFilter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"{label} : {outfit.Gender} {outfit.Name} ({string.Join(", ", outfit.Parts.Select(p => p.SlotName))})");
            if (args[0] == "export")
            {
                Directory.CreateDirectory(outDir);
                string path = Path.Combine(outDir, $"{outfit.Gender}_{outfit.Name}{(hd ? "_HD" : "")}.glb");
                GlbExporter.Export(outfit, path);
                Console.WriteLine($"  → {path}");
            }
        }
    }
    catch (Exception e)
    {
        errors++;
        Console.Error.WriteLine($"{label} : erreur : {e.Message}");
    }
}
return errors == 0 ? 0 : 2;

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
