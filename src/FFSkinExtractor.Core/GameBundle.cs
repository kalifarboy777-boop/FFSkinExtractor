using System.IO.Compression;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FFSkinExtractor.Core;

/// <summary>Référence vers un objet Unity (PPtr).</summary>
public readonly record struct PPtr(int FileId, long PathId)
{
    public bool IsNull => PathId == 0;
}

/// <summary>Un MonoBehaviour UMA lu sans typetree : nom, classe du script et octets bruts.</summary>
public sealed record UmaObject(long PathId, string Name, string ScriptClass, byte[] Raw);

/// <summary>
/// Ouvre un bundle Free Fire (UnityFS, éventuellement compressé en gzip comme dans l'APK)
/// et donne accès aux objets UMA et aux textures.
/// </summary>
public sealed partial class GameBundle : IDisposable
{
    readonly AssetsManager _am = new();
    readonly BundleFileInstance _bundle;
    readonly AssetsFileInstance _file;
    readonly Dictionary<long, string> _scriptClassCache = new();

    public string Path { get; }

    public GameBundle(string path) : this(File.OpenRead(path), path) { }

    /// <summary>Ouvre un bundle déjà en mémoire (par exemple rangé dans un APK).</summary>
    public GameBundle(Stream stream, string path)
    {
        Path = path;
        if (IsGzip(stream))
        {
            var mem = new MemoryStream();
            using (var gz = new GZipStream(stream, CompressionMode.Decompress)) gz.CopyTo(mem);
            mem.Position = 0;
            stream = mem;
        }
        _bundle = _am.LoadBundleFile(stream, path, true);
        int index = Enumerable.Range(0, _bundle.file.BlockAndDirInfo.DirectoryInfos.Count)
            .First(i => _bundle.file.IsAssetsFile(i));
        _file = _am.LoadAssetsFileFromBundle(_bundle, index, false);
    }

    static bool IsGzip(Stream s)
    {
        int a = s.ReadByte(), b = s.ReadByte();
        s.Position = 0;
        return a == 0x1f && b == 0x8b;
    }

    /// <summary>
    /// Bundle chiffré par le jeu (UnityCN, drapeau 0x200 de l'en-tête UnityFS en Unity 2018) : il n'est pas lu,
    /// ses objets seraient illisibles.
    /// </summary>
    public static bool IsEncrypted(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "UnityFS\0") return false;
            r.ReadBytes(4);
            ReadCString(r); ReadCString(r);
            r.ReadBytes(8 + 4 + 4);
            var f = r.ReadBytes(4);
            uint flags = (uint)(f[0] << 24 | f[1] << 16 | f[2] << 8 | f[3]);
            return (flags & 0x200) != 0;
        }
        catch (Exception) { return false; }
    }

    static void ReadCString(BinaryReader r) { while (r.ReadByte() != 0) { } }

    /// <summary>Un fichier sans contenu (0 octet) veut dire que le jeu ne l'a pas encore téléchargé.</summary>
    public static bool IsDownloaded(string path) => new FileInfo(path).Length > 0;

    static readonly string[] OutfitPrefixes = ["optionalab_avatar", "optionalab_legendcloth", "optionalab_tailor"];

    /// <summary>
    /// Bundle susceptible de contenir des tenues : « optionalab_avatar… », « optionalab_legendcloth… »,
    /// « optionalab_tailor… » et les bundles numérotés (« optionalab_672 », « optionalab_1_1 ») où le jeu
    /// range la plupart des tenues de la boutique une fois téléchargées.
    /// </summary>
    public static bool IsOutfitBundle(string path)
    {
        string name = System.IO.Path.GetFileName(path).Split('.')[0];
        if (name.EndsWith("_animctrl", StringComparison.OrdinalIgnoreCase)) return false;
        return OutfitPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
               || NumberedRegex().IsMatch(name);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^optionalab_\d+(_\d+)*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NumberedRegex();

    public IEnumerable<UmaObject> UmaObjects()
    {
        foreach (var info in _file.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            var raw = ReadRaw(info);
            var r = new AssetsFileReader(new MemoryStream(raw));
            r.Position = 12;            // m_GameObject
            r.ReadInt32();              // m_Enabled + alignement
            var script = new PPtr(r.ReadInt32(), r.ReadInt64());
            string name = r.ReadCountStringInt32();
            yield return new UmaObject(info.PathId, name, ScriptClass(script), raw);
        }
    }

    string ScriptClass(PPtr script)
    {
        if (script.FileId != 0 || script.IsNull) return "";
        if (_scriptClassCache.TryGetValue(script.PathId, out var cached)) return cached;
        var info = _file.file.GetAssetInfo(script.PathId);
        string cls = info == null ? "" : _am.GetBaseField(_file, info)["m_ClassName"].AsString;
        _scriptClassCache[script.PathId] = cls;
        return cls;
    }

    public byte[] ReadRaw(AssetFileInfo info)
    {
        var reader = _file.file.Reader;
        lock (reader)
        {
            reader.Position = info.GetAbsoluteByteOffset(_file.file);
            return reader.ReadBytes((int)info.ByteSize);
        }
    }

    /// <summary>Nom interne du fichier d'assets (« CAB-… »), par lequel les autres bundles le désignent.</summary>
    public string CabName => _file.name;

    /// <summary>Nom CAB du fichier visé par une référence externe (fileId ≥ 1).</summary>
    public string? ExternalName(int fileId)
    {
        var ext = _file.file.Metadata.Externals;
        if (fileId < 1 || fileId > ext.Count) return null;
        var path = ext[fileId - 1].PathName;
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>TextAssets du bundle : nom et contenu (fichiers de configuration du jeu).</summary>
    public IEnumerable<(string Name, byte[] Data)> TextAssets()
    {
        foreach (var info in _file.file.GetAssetsOfType(AssetClassID.TextAsset))
        {
            var r = new AssetsFileReader(new MemoryStream(ReadRaw(info)));
            string name = r.ReadCountStringInt32();
            r.Align();
            yield return (name, r.ReadBytes(r.ReadInt32()));
        }
    }

    public IEnumerable<AssetFileInfo> Textures() => _file.file.GetAssetsOfType(AssetClassID.Texture2D);

    public IEnumerable<AssetFileInfo> OfType(AssetClassID type) => _file.file.GetAssetsOfType(type);

    public AssetFileInfo? GetInfo(long pathId) => _file.file.GetAssetInfo(pathId);

    public AssetTypeValueField BaseField(AssetFileInfo info) => _am.GetBaseField(_file, info);

    /// <summary>Nom d'un objet (premier champ, « m_Name »), lu sans décoder le reste.</summary>
    public string ReadName(AssetFileInfo info)
    {
        lock (_file.file.Reader)
        {
            var r = _file.file.Reader;
            r.Position = info.GetAbsoluteByteOffset(_file.file);
            int length = r.ReadInt32();
            return length is > 0 and < 512 ? Encoding.UTF8.GetString(r.ReadBytes(length)) : "";
        }
    }

    /// <summary>Lit un objet en s'arrêtant avant les champs ajoutés par le jeu (« m_16BitCurves » des clips).</summary>
    public AssetTypeValueField BaseFieldWithout(AssetFileInfo info, params string[] fields)
    {
        var template = _am.GetTemplateBaseField(_file, info);
        template.Children.RemoveAll(c => fields.Contains(c.Name));
        return template.MakeValue(_file.file.Reader, info.GetAbsoluteByteOffset(_file.file));
    }

    /// <summary>Lit des données stockées à part dans le bundle (fichier .resS).</summary>
    public byte[] ReadStreamData(string path, long offset, int size)
    {
        string fileName = path[(path.LastIndexOf('/') + 1)..];
        int index = _bundle.file.GetFileIndex(fileName);
        if (index < 0) throw new FileNotFoundException($"{fileName} absent du bundle {System.IO.Path.GetFileName(Path)}");
        _bundle.file.GetFileRange(index, out long start, out _);
        var reader = _bundle.file.DataReader;
        lock (reader)
        {
            reader.Position = start + offset;
            return reader.ReadBytes(size);
        }
    }

    public void Dispose() => _am.UnloadAll(true);
}
