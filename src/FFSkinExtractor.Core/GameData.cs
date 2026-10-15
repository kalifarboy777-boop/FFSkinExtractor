using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace FFSkinExtractor.Core;

/// <summary>Objet Unity rangé dans les données de base du jeu (fichiers « bin/Data » de l'APK).</summary>
public readonly record struct DataObject(string File, long PathId, AssetClassID Type, string Name);

/// <summary>
/// Accès aux données de base du jeu, installées avec l'APK (asset_pack_install_time.apk → assets/bin/Data) :
/// corps de base, visage, cheveux par défaut, squelettes, textures de peau. Chaque fichier y contient
/// un ou quelques objets ; les références entre fichiers passent par leur liste de dépendances.
/// </summary>
public sealed class GameData : IDisposable
{
    const long MaxIndexedFile = 4_000_000;

    readonly Dictionary<string, Func<byte[]>> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AssetsFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IDisposable> _owned = [];
    Dictionary<string, List<DataObject>>? _byName;

    public int FileCount => _files.Count;

    /// <summary>
    /// Fichier où garder l'index des noms d'objets entre deux lancements (null : pas de cache). Il est refait
    /// quand le paquet change (chemin, taille ou date).
    /// </summary>
    public string? IndexCachePath { get; init; }
    string? _source;

    /// <summary>Ouvre un .xapk, un .apk, ou un dossier (APK décompressé ou dossier bin/Data).</summary>
    public static GameData Open(string path, Action<string>? progress = null, string? indexCachePath = null)
    {
        var data = new GameData { IndexCachePath = indexCachePath };
        data._source = Directory.Exists(path) ? null : $"{Path.GetFullPath(path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var norm = file.Replace('\\', '/');
                if (norm.Contains("/bin/Data/") || Path.GetDirectoryName(file) == path)
                    if (data._files.TryAdd(Path.GetFileName(file), () => File.ReadAllBytes(file)))
                        data._sizes[Path.GetFileName(file)] = new FileInfo(file).Length;
            }
            return data;
        }

        var zip = ZipFile.OpenRead(path);
        data._owned.Add(zip);
        if (!data.AddApk(zip))
        {
            // .xapk : l'APK des données est rangé à l'intérieur
            foreach (var inner in zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(e => e.Length))
            {
                progress?.Invoke($"Lecture de {inner.Name}…");
                var mem = new MemoryStream((int)inner.Length);
                using (var s = inner.Open()) s.CopyTo(mem);
                mem.Position = 0;
                var innerZip = new ZipArchive(mem, ZipArchiveMode.Read);
                data._owned.Add(innerZip);
                if (data.AddApk(innerZip)) break;
            }
        }
        if (data._files.Count == 0) throw new InvalidDataException($"{Path.GetFileName(path)} : aucun fichier assets/bin/Data trouvé");
        return data;
    }

    bool AddApk(ZipArchive zip)
    {
        bool any = false;
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("assets/bin/Data/") || e.FullName.IndexOf('/', 16) >= 0 || e.Length == 0) continue;
            var entry = e;
            _sizes.TryAdd(e.Name, e.Length);
            _files.TryAdd(e.Name, () =>
            {
                lock (zip)
                {
                    var buf = new byte[entry.Length];
                    using var s = entry.Open();
                    s.ReadExactly(buf);
                    return buf;
                }
            });
            any = true;
        }
        return any;
    }

    public byte[] Bytes(string file)
    {
        lock (_bytes)
        {
            if (_bytes.TryGetValue(file, out var b)) return b;
            return _bytes[file] = _files[file]();
        }
    }

    AssetsFile? Parse(string file)
    {
        lock (_parsed)
        {
            if (_parsed.TryGetValue(file, out var f)) return f;
            try
            {
                f = new AssetsFile();
                f.Read(new AssetsFileReader(new MemoryStream(Bytes(file))));
                if (f.Header.Version < 9 || f.AssetInfos.Count == 0) f = null;
            }
            catch (Exception) { f = null; }
            return _parsed[file] = f!;
        }
    }

    /// <summary>Octets bruts d'un objet.</summary>
    public byte[] Raw(DataObject o)
    {
        var f = Parse(o.File) ?? throw new InvalidDataException(o.File);
        var info = f.GetAssetInfo(o.PathId);
        lock (f)
        {
            f.Reader.Position = info.GetAbsoluteByteOffset(f);
            return f.Reader.ReadBytes((int)info.ByteSize);
        }
    }

    /// <summary>Suit une référence (PPtr) écrite dans un objet du fichier <paramref name="from"/>.</summary>
    public DataObject? Resolve(string from, int fileId, long pathId)
    {
        if (pathId == 0) return null;
        string target = from;
        if (fileId != 0)
        {
            var f = Parse(from);
            if (f == null || fileId > f.Metadata.Externals.Count) return null;
            target = Path.GetFileName(f.Metadata.Externals[fileId - 1].PathName);
            if (!_files.ContainsKey(target)) return null;
        }
        var tf = Parse(target);
        var info = tf?.GetAssetInfo(pathId);
        return info == null ? null : Describe(target, tf!, info);
    }

    /// <summary>Cherche un objet par son nom (indexation complète au premier appel).</summary>
    public DataObject? Find(string name, AssetClassID? type = null)
    {
        BuildIndex();
        return _byName!.TryGetValue(name, out var list)
            ? list.Where(o => type == null || o.Type == type).Cast<DataObject?>().FirstOrDefault()
            : null;
    }

    public IEnumerable<DataObject> FindAll(Func<string, bool> namePredicate)
    {
        BuildIndex();
        return _byName!.Where(kv => namePredicate(kv.Key)).SelectMany(kv => kv.Value);
    }

    void BuildIndex()
    {
        if (_byName != null) return;
        if (LoadIndex() is { } cached) { _byName = cached; return; }
        var index = new Dictionary<string, List<DataObject>>(StringComparer.Ordinal);
        foreach (var file in _files.Keys.ToList())
        {
            // Les gros fichiers (maillages, sons) ne sont pas indexés : inutile de les décompresser
            if (_sizes.GetValueOrDefault(file) > MaxIndexedFile) continue;
            var bytes = Bytes(file);
            bool keep = false;
            if (bytes.Length <= MaxIndexedFile && LooksSerialized(bytes))
            {
                var f = Parse(file);
                if (f != null)
                {
                    foreach (var info in f.AssetInfos)
                    {
                        var t = (AssetClassID)info.TypeId;
                        if (t is not (AssetClassID.MonoBehaviour or AssetClassID.Texture2D or AssetClassID.Avatar or AssetClassID.TextAsset)) continue;
                        var o = Describe(file, f, info);
                        if (o.Name.Length == 0) continue;
                        if (!index.TryGetValue(o.Name, out var list)) index[o.Name] = list = [];
                        list.Add(o);
                        keep = true;
                    }
                }
            }
            // On ne garde en mémoire que les petits fichiers utiles
            if (!keep || bytes.Length > 512_000)
                lock (_bytes) { _bytes.Remove(file); lock (_parsed) _parsed.Remove(file); }
        }
        _byName = index;
        SaveIndex(index);
    }

    // Index en cache : une ligne d'en-tête (le paquet), puis « nom, fichier, PathId, type » par objet
    const string IndexFormat = "ffss-index-1";

    Dictionary<string, List<DataObject>>? LoadIndex()
    {
        if (IndexCachePath == null || _source == null || !File.Exists(IndexCachePath)) return null;
        try
        {
            using var r = new StreamReader(IndexCachePath, Encoding.UTF8);
            if (r.ReadLine() != IndexFormat || r.ReadLine() != _source) return null;
            var index = new Dictionary<string, List<DataObject>>(StringComparer.Ordinal);
            while (r.ReadLine() is { } line)
            {
                var f = line.Split('\t');
                if (f.Length != 4 || !_files.ContainsKey(f[1])) return null;
                var o = new DataObject(f[1], long.Parse(f[2]), (AssetClassID)int.Parse(f[3]), f[0]);
                if (!index.TryGetValue(o.Name, out var list)) index[o.Name] = list = [];
                list.Add(o);
            }
            return index;
        }
        catch (Exception) { return null; }
    }

    void SaveIndex(Dictionary<string, List<DataObject>> index)
    {
        if (IndexCachePath == null || _source == null) return;
        try
        {
            string tmp = IndexCachePath + ".tmp";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.WriteLine(IndexFormat);
                w.WriteLine(_source);
                foreach (var o in index.Values.SelectMany(l => l))
                    if (!o.Name.Contains('\t') && !o.Name.Contains('\n') && !o.Name.Contains('\r'))
                        w.WriteLine($"{o.Name}\t{o.File}\t{o.PathId}\t{(int)o.Type}");
            }
            File.Move(tmp, IndexCachePath, true);
        }
        catch (Exception) { /* pas de cache : l'index sera refait au prochain lancement */ }
    }

    // En-tête d'un fichier sérialisé Unity : taille des métadonnées, taille du fichier (big-endian), version
    static bool LooksSerialized(byte[] b)
    {
        if (b.Length < 48) return false;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
        // À partir de la version 22, les tailles sont sur 64 bits plus loin dans l'en-tête
        long size = version >= 22 ? BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(0x18)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
        return version is >= 9 and <= 30 && size == b.Length;
    }

    DataObject Describe(string file, AssetsFile f, AssetFileInfo info)
    {
        var t = (AssetClassID)info.TypeId;
        string name = "";
        int at = t == AssetClassID.MonoBehaviour ? 28 : 0;
        if (t is AssetClassID.MonoBehaviour or AssetClassID.Texture2D or AssetClassID.Avatar or AssetClassID.Material or AssetClassID.TextAsset)
        {
            lock (f)
            {
                f.Reader.Position = info.GetAbsoluteByteOffset(f) + at;
                if (info.ByteSize >= at + 4)
                {
                    int len = f.Reader.ReadInt32();
                    if (len > 0 && len < 256 && at + 4 + len <= info.ByteSize)
                        name = Encoding.UTF8.GetString(f.Reader.ReadBytes(len));
                }
            }
        }
        return new DataObject(file, info.PathId, t, name);
    }

    /// <summary>Contenu d'un TextAsset (tables de configuration du jeu, comme « Clothes »).</summary>
    public byte[]? TextAsset(string name)
    {
        if (Find(name, AssetClassID.TextAsset) is not { } o) return null;
        var raw = Raw(o);
        int p = (4 + BinaryPrimitives.ReadInt32LittleEndian(raw) + 3) & ~3;
        int len = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(p));
        return raw.AsSpan(p + 4, len).ToArray();
    }

    readonly Dictionary<DataObject, DecodedTexture?> _textures = [];

    /// <summary>Texture2D d'un fichier de base (données d'image incluses dans le fichier).</summary>
    public DecodedTexture? Texture(DataObject o)
    {
        lock (_textures)
        {
            if (_textures.TryGetValue(o, out var t)) return t;
            try { t = TextureDecoder.DecodeRaw(Raw(o)); }
            catch (Exception) { t = null; }
            return _textures[o] = t;
        }
    }

    /// <summary>Textures d'un overlay UMA des données de base (couleur, normales, spéculaire ou masque PBR).</summary>
    public TextureSet? Overlay(string name)
    {
        if (Find(name, AssetClassID.MonoBehaviour) is not { } ov) return null;
        var refs = Uma.UmaAssets.OverlayTextures(Raw(ov), p => Resolve(ov.File, p.FileId, p.PathId)?.Type == AssetClassID.Texture2D);
        if (refs.Count == 0) return null;
        return TextureSet.FromOverlay(refs.Select(p => Resolve(ov.File, p.FileId, p.PathId) is { } t ? Texture(t) : null).ToList());
    }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _owned.Clear();
    }
}
