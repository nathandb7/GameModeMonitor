using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace GameModeMonitor.App;

public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            ThemedDialog.Notice(null, "Modo juego", "Abra el monitor sin permisos de administrador. Solo el auxiliar de servicios necesita elevación.");
            Shutdown(1); return;
        }
        // Smoke never claims the normal monitor's mutex or opens its persistent state.
        var instanceName = e.Args.Contains("--smoke") ? "GameModeMonitor-Smoke-" : "GameModeMonitor-";
        mutex = new Mutex(true, @"Local\" + instanceName + identity.User?.Value, out var created);
        if (!created) { ThemedDialog.Notice(null, "Modo juego", "El monitor ya está abierto. Busque su icono en la bandeja del sistema."); Shutdown(); return; }
        try
        {
            var window = new MainWindow(e.Args);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ThemedDialog.Notice(null, "No se pudo iniciar el monitor", "No se ejecutó ninguna acción.\n\n" + ex.Message);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
