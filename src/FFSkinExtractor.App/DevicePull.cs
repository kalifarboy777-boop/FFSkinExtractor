using System.Diagnostics;
using System.Text;
using FFSkinExtractor.Core;

namespace FFSkinExtractor.App;

/// <summary>Téléphone ou émulateur vu par ADB, avec les paquets Free Fire qui y sont installés.</summary>
public sealed record AndroidDevice(string Serial, string Model, bool Emulator, List<string> Games);

/// <summary>
/// Récupère par ADB les bundles que Free Fire a téléchargés sur un téléphone (débogage USB) ou un émulateur
/// (BlueStacks, LDPlayer, MuMu, Nox), et les copie dans le dossier des données. Seuls les fichiers
/// absents du dossier sont copiés : les relances suivantes ne prennent que les nouveaux skins.
/// </summary>
public sealed class DevicePull
{
    public static readonly string[] Packages = ["com.dts.freefireth", "com.dts.freefiremax"];

    // Ports ADB habituels des émulateurs (BlueStacks, LDPlayer, MuMu, Nox)
    static readonly int[] EmulatorPorts = [5555, 5565, 5575, 5585, 7555, 16384, 16416, 62001, 62025];

    readonly object _sync = new();
    CancellationTokenSource? _cts;
    public bool Running { get; private set; }
    public int Total { get; private set; }
    public int Done { get; private set; }
    public int Copied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public bool Watching { get; private set; }
    public string? WatchDevice { get; private set; }

    public object Snapshot()
    {
        lock (_sync) return new { running = Running, watching = Watching, device = WatchDevice, total = Total, done = Done, copied = Copied, message = Message, error = Error };
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>adb.exe : réglage, PATH, Android SDK, puis l'adb fourni avec les émulateurs.</summary>
    public static string? FindAdb(string? configured)
    {
        if (configured is { Length: > 0 } && File.Exists(configured)) return configured;
        bool windows = OperatingSystem.IsWindows();
        string exe = windows ? "adb.exe" : "adb";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe); }
            catch (ArgumentException) { /* entrée du PATH invalide */ }
        }
        if (!windows) return null;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new List<string>
        {
            Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"),
            Path.Combine(pf, "BlueStacks_nxt", "HD-Adb.exe"),
            Path.Combine(pf, "BlueStacks", "HD-Adb.exe"),
            Path.Combine(pf, "Netease", "MuMuPlayer-12.0", "shell", "adb.exe"),
            Path.Combine(pf, "Netease", "MuMu Player 12", "shell", "adb.exe"),
            Path.Combine(pf86, "Nox", "bin", "nox_adb.exe"),
            Path.Combine(pf, "Nox", "bin", "nox_adb.exe"),
        };
        foreach (var drive in new[] { "C:", "D:", "E:" })
            foreach (var ld in new[] { "LDPlayer9", "LDPlayer4.0", "LDPlayer" })
            {
                candidates.Add(Path.Combine(drive + "\\", "LDPlayer", ld, "adb.exe"));
                candidates.Add(Path.Combine(drive + "\\", ld, "adb.exe"));
            }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Appareils connectés (les émulateurs locaux sont d'abord rattachés à ADB).</summary>
    public static async Task<List<AndroidDevice>> ListDevicesAsync(string adb, CancellationToken ct = default)
    {
        await Adb(adb, ["start-server"], TimeSpan.FromSeconds(20), ct);
        // Les émulateurs n'apparaissent souvent qu'après un « adb connect » sur leur port
        await Task.WhenAll(EmulatorPorts.Select(port => Adb(adb, ["connect", $"127.0.0.1:{port}"], TimeSpan.FromSeconds(4), ct)));
        var (_, output) = await Adb(adb, ["devices", "-l"], TimeSpan.FromSeconds(10), ct);
        var serials = new List<(string Serial, string Model)>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "device") continue;
            string model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', ' ') ?? parts[0];
            serials.Add((parts[0], model));
        }
        var devices = new List<AndroidDevice>();
        var seenIds = new HashSet<string>();
        foreach (var (serial, model) in serials)
        {
            // Un même émulateur peut répondre sous deux noms (emulator-5554 et 127.0.0.1:5555)
            var (_, id) = await Adb(adb, ["-s", serial, "shell", "getprop", "ro.serialno;", "getprop", "ro.boot.serialno"], TimeSpan.FromSeconds(10), ct);
            string key = id.Trim() + "|" + model;
            if (id.Trim().Length > 0 && !seenIds.Add(key)) continue;
            var games = new List<string>();
            foreach (var pkg in Packages)
            {
                var (_, ls) = await Adb(adb, ["-s", serial, "shell", "ls", "-d", $"/sdcard/Android/data/{pkg}/files"], TimeSpan.FromSeconds(10), ct);
                if (ls.Contains($"{pkg}/files") && !ls.Contains("No such file")) games.Add(pkg);
            }
            bool emulator = serial.StartsWith("emulator-") || serial.StartsWith("127.0.0.1:") || serial.StartsWith("localhost:");
            devices.Add(new AndroidDevice(serial, model, emulator, games));
        }
        return devices;
    }

    /// <summary>Lance la copie en arrière-plan ; <paramref name="done"/> est appelé si des fichiers ont été copiés.</summary>
    public bool TryStart(string adb, AndroidDevice device, string dataFolder, Action<List<string>> done)
    {
        lock (_sync)
        {
            if (Running) return false;
            Running = true; Total = 0; Done = 0; Copied = 0; Error = null; Message = "Liste des fichiers du jeu…";
            _cts = new CancellationTokenSource();
        }
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var copied = await PullAsync(adb, device, dataFolder, LocalFiles(dataFolder), null, ct);
                if (copied.Count > 0) done(copied);
            }
            catch (OperationCanceledException) { Message = $"Annulé : {Copied} fichier(s) copié(s)."; }
            catch (Exception e) { Error = e.Message; }
            finally { lock (_sync) Running = false; }
        });
        return true;
    }

    /// <summary>
    /// Suivi en direct : toutes les quelques secondes, regarde si le jeu a téléchargé de nouveaux bundles
    /// (un skin affiché pour la première fois) et les copie aussitôt.
    /// </summary>
    public bool TryWatch(string adb, AndroidDevice device, string dataFolder, Action<List<string>> arrived)
    {
        lock (_sync)
        {
            if (Running) return false;
            Running = true; Watching = true; WatchDevice = device.Model; Total = 0; Done = 0; Copied = 0; Error = null;
            Message = $"Suivi de {device.Model} : équipe ou essaie un skin dans le jeu…";
            _cts = new CancellationTokenSource();
        }
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var local = LocalFiles(dataFolder);
                // Taille vue au tour précédent : un fichier n'est copié qu'une fois sa taille stable (téléchargement fini)
                var seen = new Dictionary<string, long>();
                int failures = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var copied = await PullAsync(adb, device, dataFolder, local, seen, ct);
                        failures = 0;
                        if (copied.Count > 0)
                        {
                            Message = $"Suivi de {device.Model} : {copied.Count} nouveau(x) fichier(s) reçu(s), chargement…";
                            arrived(copied);
                        }
                        else if (Total == 0) Message = $"Suivi de {device.Model} : équipe ou essaie un skin dans le jeu…";
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Appareil débranché ou en veille : on réessaie, puis on abandonne
                        if (++failures >= 5) throw;
                        Message = $"Suivi de {device.Model} : appareil injoignable, nouvel essai…";
                    }
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                }
            }
            catch (OperationCanceledException) { Message = $"Suivi arrêté : {Copied} fichier(s) reçu(s)."; }
            catch (Exception e) { Error = e.Message; }
            finally { lock (_sync) { Running = false; Watching = false; } }
        });
        return true;
    }

    static HashSet<(string, long)> LocalFiles(string dataFolder) =>
        Directory.EnumerateFiles(dataFolder, "optionalab_*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(f => (Path.GetFileName(f).ToLowerInvariant(), new FileInfo(f).Length)).ToHashSet();

    /// <summary>
    /// Copie les bundles du jeu absents de <paramref name="local"/> (nom avec son suffixe de version + taille)
    /// et renvoie les chemins des fichiers copiés. Avec <paramref name="seen"/>, ne prend que les fichiers
    /// dont la taille n'a pas bougé depuis le tour précédent.
    /// </summary>
    async Task<List<string>> PullAsync(string adb, AndroidDevice device, string dataFolder, HashSet<(string, long)> local,
        Dictionary<string, long>? seen, CancellationToken ct)
    {
        var wanted = new List<(string Remote, string Relative, long Size)>();
        foreach (var pkg in device.Games)
        {
            string root = $"/sdcard/Android/data/{pkg}/files/contentcache";
            var (code, listing) = await Adb(adb, ["-s", device.Serial, "shell",
                $"find {root} -type f -name 'optionalab_*' -size +0 -exec stat -c '%s|%n' {{}} +"], TimeSpan.FromMinutes(2), ct);
            if (code != 0 && listing.Contains("device") && (listing.Contains("not found") || listing.Contains("offline")))
                throw new IOException($"{device.Model} n'est plus connecté.");
            if (listing.Contains("Permission denied") && !listing.Contains('|'))
                throw new UnauthorizedAccessException("Android refuse l'accès au dossier du jeu. Autorise le débogage USB sur l'appareil, puis réessaie.");
            if (!listing.Contains('|'))
            {
                // Anciennes versions d'Android sans « stat -c » : noms seuls, taille inconnue (-1)
                (code, listing) = await Adb(adb, ["-s", device.Serial, "shell", $"find {root} -type f -name 'optionalab_*'"], TimeSpan.FromMinutes(2), ct);
                listing = string.Join('\n', listing.Split('\n').Select(l => "-1|" + l));
            }
            foreach (var line in listing.Split('\n'))
            {
                int bar = line.IndexOf('|');
                if (bar <= 0 || !long.TryParse(line[..bar], out long size)) continue;
                string remote = line[(bar + 1)..].Trim();
                if (!remote.StartsWith(root + "/")) continue;
                string relative = remote[(root.Length + 1)..];
                // Téléchargements en cours (« Temp ») : incomplets
                if (relative.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!GameBundle.IsOutfitBundle(relative)) continue;
                string fileName = Path.GetFileName(relative).ToLowerInvariant();
                if (size >= 0 ? local.Contains((fileName, size)) : local.Any(l => l.Item1 == fileName)) continue;
                if (seen != null)
                {
                    bool stable = seen.TryGetValue(remote, out long before) && before == size;
                    seen[remote] = size;
                    if (!stable) continue;
                }
                wanted.Add((remote, Path.Combine(pkg, "files", "contentcache", relative.Replace('/', Path.DirectorySeparatorChar)), size));
            }
        }
        var copied = new List<string>();
        Total = wanted.Count;
        if (wanted.Count == 0)
        {
            if (seen == null) Message = "Aucun nouveau skin : tout est déjà dans le dossier des données.";
            return copied;
        }
        Done = 0;

        string target = Path.Combine(dataFolder, "Appareil - " + SafeName(device.Model));
        Message = $"Copie de {wanted.Count} fichier(s) depuis {device.Model}…";
        int failed = 0;
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (f, token) =>
        {
            string dest = Path.Combine(target, f.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Nom provisoire qui ne commence pas par « optionalab_ » : jamais pris pour un bundle
            string part = Path.Combine(Path.GetDirectoryName(dest)!, "~" + Path.GetFileName(dest));
            // -a garde la date du fichier : la version la plus récente d'un bundle l'emporte à l'analyse
            var (code, _) = await Adb(adb, ["-s", device.Serial, "pull", "-a", f.Remote, part], TimeSpan.FromMinutes(5), token);
            if (code == 0 && File.Exists(part) && (f.Size < 0 ? new FileInfo(part).Length > 0 : new FileInfo(part).Length == f.Size))
            {
                File.Move(part, dest, true);
                lock (_sync)
                {
                    Copied++;
                    copied.Add(dest);
                    local.Add((Path.GetFileName(dest).ToLowerInvariant(), new FileInfo(dest).Length));
                }
            }
            else
            {
                try { File.Delete(part); } catch (IOException) { }
                Interlocked.Increment(ref failed);
            }
            lock (_sync) Done++;
        });
        Message = $"{copied.Count} fichier(s) copié(s) dans « {target} »" + (failed > 0 ? $", {failed} échec(s)." : ".");
        Total = 0;
        return copied;
    }

    static async Task<(int Code, string Output)> Adb(string adb, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, e.Message); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var stdout = proc.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = proc.StandardError.ReadToEndAsync(limit.Token);
        try { await proc.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "délai dépassé");
        }
        string output = (await stdout) + (await stderr);
        return (proc.ExitCode, output.Replace("\r", ""));
    }

    static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
