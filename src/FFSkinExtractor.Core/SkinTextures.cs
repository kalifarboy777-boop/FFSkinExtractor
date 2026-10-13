using System.Buffers.Binary;
using System.IO.Compression;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FFSkinExtractor.Core;

/// <summary>
/// Textures de peau du corps de base (homme et femme). Les tenues ne contiennent que les vêtements :
/// les morceaux de peau visibles (cou, mains, taille…) utilisent ces textures, rangées dans l'APK du jeu.
/// </summary>
public sealed class SkinTextures
{
    public DecodedTexture? Male { get; private set; }
    public DecodedTexture? Female { get; private set; }

    public DecodedTexture? For(string gender) =>
        gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? Female : Male;

    static readonly (string Name, bool Female)[] Wanted = [("female_Newbody_d", true), ("male_body_D", false)];

    /// <summary>Lit les textures de peau depuis un .xapk, un .apk, ou un dossier d'APK décompressé.</summary>
    public static SkinTextures Load(string path)
    {
        var result = new SkinTextures();
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).Length > 300_000) continue;
                result.TryAdd(File.ReadAllBytes(file), Path.GetFileName(file));
                if (result.Complete) break;
            }
            return result;
        }
        using var zip = ZipFile.OpenRead(path);
        result.Scan(zip);
        // Un .xapk contient d'autres .apk : les données du jeu sont dans asset_pack_install_time.apk
        foreach (var inner in zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            if (result.Complete) break;
            var mem = new MemoryStream();
            using (var s = inner.Open()) s.CopyTo(mem);
            mem.Position = 0;
            using var innerZip = new ZipArchive(mem, ZipArchiveMode.Read);
            result.Scan(innerZip);
        }
        return result;
    }

    bool Complete => Male != null && Female != null;

    void Scan(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            if (Complete) return;
            if (e.Length > 300_000 || !e.FullName.StartsWith("assets/bin/Data/")) continue;
            var data = new byte[e.Length];
            using (var s = e.Open()) s.ReadExactly(data);
            TryAdd(data, e.Name);
        }
    }

    void TryAdd(byte[] data, string name)
    {
        foreach (var (texName, female) in Wanted)
        {
            if ((female ? Female : Male) != null) continue;
            if (data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(texName)) < 0) continue;
            var tex = ReadTexture(data, name);
            if (tex == null || !tex.Name.Equals(texName, StringComparison.OrdinalIgnoreCase)) continue;
            if (female) Female = tex; else Male = tex;
        }
    }

    // Fichier sérialisé isolé (bin/Data) contenant une Texture2D dont l'image est incluse.
    static DecodedTexture? ReadTexture(byte[] file, string name)
    {
        try
        {
            var am = new AssetsManager();
            var inst = am.LoadAssetsFile(new MemoryStream(file), name, false);
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                inst.file.Reader.Position = info.GetAbsoluteByteOffset(inst.file);
                var raw = inst.file.Reader.ReadBytes((int)info.ByteSize);
                return TextureDecoder.DecodeRaw(raw);
            }
        }
        catch (Exception ex) { if (Environment.GetEnvironmentVariable("FFX_DEBUG") != null) Console.Error.WriteLine($"{name}: {ex}"); }
        return null;
    }
}
