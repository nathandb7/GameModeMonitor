using System.Diagnostics;

namespace GameModeMonitor.Windows;

public record CommandResult(int ExitCode, string Output, string Error);
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, bool unicode = false);
}
public sealed class CommandRunner : ICommandRunner
{
    public event Action<string>? SlowOperation;
    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, bool unicode = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (unicode) info.StandardOutputEncoding = System.Text.Encoding.Unicode;
        // Never inherit a remote Docker context/host or ambient Docker config.
        foreach (var key in new[] { "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "DOCKER_CONFIG" }) info.Environment.Remove(key);
        using var process = Process.Start(info) ?? throw new IOException("No se pudo iniciar la herramienta local.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            // Cancellation only cancelled the wait, not the command. Retain the
            // caller's operation gate until exit is known; never overlap Restore.
            SlowOperation?.Invoke("La herramienta superó el tiempo previsto. Se espera su salida sin forzar el cierre; Optimizar y Revertir siguen bloqueados.");
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw new TimeoutException("La herramienta terminó fuera del tiempo previsto. Compruebe el resultado manualmente; no se forzó ningún cierre.");
        }
        return new(process.ExitCode, await output, await error);
    }
}
