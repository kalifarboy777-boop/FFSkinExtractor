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

    public GameBundle(string path)
    {
        Path = path;
        Stream stream = File.OpenRead(path);
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

    public IEnumerable<AssetFileInfo> Textures() => _file.file.GetAssetsOfType(AssetClassID.Texture2D);

    public AssetFileInfo? GetInfo(long pathId) => _file.file.GetAssetInfo(pathId);

    public AssetTypeValueField BaseField(AssetFileInfo info) => _am.GetBaseField(_file, info);

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
