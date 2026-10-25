using System.Text.Json;

namespace GameModeMonitor.Core;

public enum OptimizationProfile { Safe, Recommended, Experimental }

public sealed class ProfileSettings
{
    public int Version { get; set; } = 1;
    public OptimizationProfile Profile { get; set; } = OptimizationProfile.Recommended;
    public Dictionary<string, bool> Overrides { get; set; } = new(StringComparer.Ordinal);
    public bool IsSelected(string key)
    {
        if (Overrides.TryGetValue(Profile + ":" + key, out var selected)) return selected;
        if (!key.StartsWith("service:", StringComparison.Ordinal)) return false;
        var name = key[8..];
        if (ServicePolicy.IsWindowsName(name))
            return name is "WSearch" or "Spooler" ? Profile != OptimizationProfile.Safe : Profile == OptimizationProfile.Experimental;
        return ServicePolicy.IsKnownName(name);
    }
    public void SetSelected(string key, bool selected) => Overrides[Profile + ":" + key] = selected;
    public void ResetCurrentProfile()
    {
        foreach (var key in Overrides.Keys.Where(k => k.StartsWith(Profile + ":", StringComparison.Ordinal)).ToArray()) Overrides.Remove(key);
    }
    public static string Impact(string key) => key switch
    {
        "service:WSearch" => "Pausa la indexación y puede limitar las búsquedas.",
        "service:Spooler" => "Pausa la impresión, incluida la impresión a PDF.",
        "service:DiagTrack" => "Pausa diagnósticos y telemetría de Windows; beneficio no garantizado.",
        "service:SysMain" => "Pausa la precarga de aplicaciones; podría empeorar sus tiempos de carga.",
        "docker" => "Detiene contenedores locales y Docker. Requiere confirmación adicional.",
        "wsl" => "Finaliza todas las sesiones Linux. Guarde su trabajo antes.",
        _ => key.StartsWith("service:", StringComparison.Ordinal) ? "Pausa un servicio de desarrollo con inicio automático. Interrumpe conexiones activas." : "Solicita un cierre normal. No se recupera trabajo sin guardar."
    };
}

public sealed class ProfileSettingsStore(string directory)
{
    public string FilePath => Path.Combine(directory, "settings.json");
    public ProfileSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 100_000) throw new IOException("La configuración es demasiado grande.");
        try
        {
            var settings = JsonSerializer.Deserialize<ProfileSettings>(File.ReadAllText(FilePath));
            if (settings is null || settings.Version != 1 || !Enum.IsDefined(settings.Profile) || settings.Overrides is null || settings.Overrides.Count > 500)
                throw new IOException("Configuración incompatible. Se conserva el archivo original.");
            return settings;
        }
        catch (JsonException ex) { throw new IOException("Configuración dañada. Se conserva el archivo original.", ex); }
    }
    public void Save(ProfileSettings settings) => AtomicJson.Write(FilePath, settings);
}

public static class AtomicJson
{
    public static void Write<T>(string filePath, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporary = filePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temporary, filePath, true);
    }
}
