using Microsoft.Win32;

namespace FFSkinExtractor.App;

/// <summary>
/// Démarrage avec Windows : une copie de l'exécutable dans %LOCALAPPDATA%\FFSkinStudio, lancée en arrière-plan
/// à l'ouverture de session, et un raccourci « FF Skin Studio » sur le bureau qui ouvre l'adresse locale.
/// Le logiciel s'utilise alors comme un site, sans relancer l'exe.
/// </summary>
static class Install
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FFSkinStudio";

    public static bool Supported => OperatingSystem.IsWindows();

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFSkinStudio");
    public static string InstalledExe => Path.Combine(Folder, "FFSkinStudio.exe");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FF Skin Studio.url");

    public static bool AutoStart
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static bool IsInstalledCopy => SamePath(Environment.ProcessPath, InstalledExe);

    /// <summary>Active ou coupe le démarrage avec Windows. Renvoie un message d'erreur, ou null.</summary>
    public static string? SetAutoStart(bool enabled, int port)
    {
        if (!OperatingSystem.IsWindows()) return "Disponible seulement sous Windows.";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                return null;
            }
            CopySelf();
            key.SetValue(ValueName, $"\"{InstalledExe}\" --background");
            File.WriteAllText(Shortcut, $"[InternetShortcut]\r\nURL=http://127.0.0.1:{port}/\r\nIconFile={InstalledExe}\r\nIconIndex=0\r\n");
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>
    /// Lancée depuis une nouvelle version alors que le démarrage avec Windows est actif : la copie installée
    /// est remplacée, pour que la prochaine ouverture de session lance la nouvelle version.
    /// </summary>
    public static void RefreshInstalledCopy()
    {
        if (!AutoStart || IsInstalledCopy) return;
        try { CopySelf(); }
        catch (Exception) { /* copie en cours d'utilisation : elle sera remplacée au prochain lancement */ }
    }

    static void CopySelf()
    {
        if (Environment.ProcessPath is not { } self || SamePath(self, InstalledExe)) return;
        Directory.CreateDirectory(Folder);
        File.Copy(self, InstalledExe, true);
    }

    static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
