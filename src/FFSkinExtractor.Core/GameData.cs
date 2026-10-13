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
    readonly Dictionary<string, AssetsFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IDisposable> _owned = [];
    Dictionary<string, List<DataObject>>? _byName;

    public int FileCount => _files.Count;

    /// <summary>Ouvre un .xapk, un .apk, ou un dossier (APK décompressé ou dossier bin/Data).</summary>
    public static GameData Open(string path, Action<string>? progress = null)
    {
        var data = new GameData();
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var norm = file.Replace('\\', '/');
                if (norm.Contains("/bin/Data/") || Path.GetDirectoryName(file) == path)
                    data._files.TryAdd(Path.GetFileName(file), () => File.ReadAllBytes(file));
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
        var index = new Dictionary<string, List<DataObject>>(StringComparer.Ordinal);
        foreach (var file in _files.Keys.ToList())
        {
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
                        if (t is not (AssetClassID.MonoBehaviour or AssetClassID.Texture2D or AssetClassID.Avatar)) continue;
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
        if (t is AssetClassID.MonoBehaviour or AssetClassID.Texture2D or AssetClassID.Avatar or AssetClassID.Material)
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
