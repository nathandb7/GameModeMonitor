namespace GameModeMonitor.Windows;

public static class AppCatalog
{
    public static Dictionary<string, string> Resolve()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app:vscode"] = Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
            ["docker"] = Path.Combine(pf, "Docker", "Docker", "Docker Desktop.exe")
        };
        if (!File.Exists(catalog["app:vscode"])) catalog["app:vscode"] = Path.Combine(pf, "Microsoft VS Code", "Code.exe");
        var jetbrains = Path.Combine(pf, "JetBrains");
        if (Directory.Exists(jetbrains))
            foreach (var folder in Directory.EnumerateDirectories(jetbrains))
                foreach (var exe in new[] { "idea64.exe", "pycharm64.exe", "webstorm64.exe", "rider64.exe", "clion64.exe", "goland64.exe" })
                {
                    var path = Path.Combine(folder, "bin", exe);
                    if (File.Exists(path)) catalog["app:jetbrains:" + Path.GetFileName(folder) + ":" + exe] = path;
                }
        foreach (var edition in new[] { "Community", "Professional", "Enterprise" })
        {
            var path = Path.Combine(pf, "Microsoft Visual Studio", "2022", edition, "Common7", "IDE", "devenv.exe");
            if (File.Exists(path)) catalog["app:visualstudio:" + edition] = path;
        }
        return catalog.Where(p => File.Exists(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
}
