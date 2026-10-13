using FFSkinExtractor.Core;

// ffextract list   <dossier-ou-bundle>
// ffextract export <bundle> [--outfit NOM] [--hd] [--out dossier] [--apk fichier.xapk]
if (args.Length >= 3 && args[0] == "dump")
{
    // ffextract dump <bundle> <dossier> : écrit les objets UMA bruts (diagnostic du format)
    using var b = new GameBundle(args[1]);
    Directory.CreateDirectory(args[2]);
    foreach (var o in b.UmaObjects())
        File.WriteAllBytes(Path.Combine(args[2], $"{o.ScriptClass}__{o.Name}__{o.PathId}.bin"), o.Raw);
    return 0;
}
if (args.Length >= 4 && args[0] == "skeleton")
{
    // ffextract skeleton <fichier Avatar> <Female|Male> <sortie.json> : régénère un squelette intégré
    var am = new AssetsTools.NET.Extra.AssetsManager();
    var inst = am.LoadAssetsFile(args[1], false);
    var info = inst.file.GetAssetsOfType(AssetsTools.NET.Extra.AssetClassID.Avatar).First();
    inst.file.Reader.Position = info.GetAbsoluteByteOffset(inst.file);
    var skel = FFSkinExtractor.Core.Rig.Skeleton.FromAvatar(args[2], inst.file.Reader.ReadBytes((int)info.ByteSize));
    File.WriteAllText(args[3], skel.ToJson());
    Console.WriteLine($"{skel.Bones.Count} os : {string.Join(", ", skel.Bones.Select(b => b.Name))}");
    return 0;
}
if (args.Length >= 2 && args[0] == "base")
{
    // ffextract base <jeu.xapk> [--out dossier] [--lowpoly] : personnage de base seul (homme et femme)
    using var data = GameData.Open(args[1], Console.WriteLine);
    string dir = Opt("--out") ?? Directory.GetCurrentDirectory();
    Directory.CreateDirectory(dir);
    foreach (var g in new[] { "Female", "Male" })
    {
        var parts = BaseCharacter.Build(data, g, !args.Contains("--lowpoly"));
        string file = Path.Combine(dir, $"{g}_Base.glb");
        GlbExporter.Build($"{g}_Base", g, parts).ToGltf2().SaveGLB(file);
        Console.WriteLine($"{g} : {string.Join(", ", parts.Select(p => $"{p.Piece}={p.SlotName}"))}\n  → {file}");
    }
    return 0;
}
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
GameData? gameData = null;
bool withBase = !args.Contains("--no-base");
if (Opt("--apk") is { } apk)
{
    Console.WriteLine("Lecture des données de base dans l'APK…");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    gameData = GameData.Open(apk, Console.WriteLine);
    skin = SkinTextures.From(gameData);
    Console.WriteLine($"  {gameData.FileCount} fichiers indexés en {sw.Elapsed.TotalSeconds:0.0} s");
    if (skin.Female == null) Console.WriteLine("  texture de peau femme introuvable, couleur unie à la place");
    if (skin.Male == null) Console.WriteLine("  texture de peau homme introuvable, couleur unie à la place");
}

var bundles = Directory.Exists(args[1])
    ? Directory.EnumerateFiles(args[1], "*", SearchOption.AllDirectories)
        .Where(f => Path.GetFileName(f).StartsWith("optionalab_avatar") || Path.GetFileName(f).StartsWith("optionalab_legendcloth")
                    || Path.GetFileName(f).StartsWith("optionalab_tailor"))
    : [args[1]];

int errors = 0;
var baseCache = new Dictionary<string, List<OutfitPart>>();
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
                var parts = outfit.Parts;
                if (gameData != null && withBase)
                {
                    if (!baseCache.TryGetValue(outfit.Gender, out var baseParts))
                        baseCache[outfit.Gender] = baseParts = BaseCharacter.Build(gameData, outfit.Gender, hd);
                    parts = BaseCharacter.Compose(baseParts, outfit.Parts);
                }
                GlbExporter.Build(outfit.Gender + "_" + outfit.Name, outfit.Gender, parts).ToGltf2().SaveGLB(path);
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
