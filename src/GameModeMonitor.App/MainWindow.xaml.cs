using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using GameModeMonitor.Core;
using GameModeMonitor.Windows;
using Forms = System.Windows.Forms;

namespace GameModeMonitor.App;

public sealed class TargetRow(Target target, bool initialSelection) : INotifyPropertyChanged
{
    public Target Target { get; } = target;
    public string Key => Target.Key;
    public string Label => Target.Label;
    public string Impact => ProfileSettings.Impact(Key);
    public string Memory => Target.MemoryBytes == 0 ? "servicio / sistema" : $"{Target.MemoryBytes / 1048576.0:N0} MB";
    private bool selected = initialSelection;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly CommandRunner commands = new();
    private readonly WindowsBackend backend;
    private readonly ResourceMonitor resources = new();
    private readonly SessionEngine engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Forms.NotifyIcon tray;
    private readonly string[] arguments;
    private readonly ObservableCollection<TargetRow> rows = [];
    private readonly WidgetLayout layout = new();
    private readonly ProfileSettingsStore settingsStore;
    private readonly bool smoke;
    private readonly string smokeOutput;
    private ProfileSettings settings = new();
    private bool loadingProfiles = true;
    private string? settingsError;
    private bool refreshing, acting, closing;
    private int samples;
    private readonly string stateDirectory;

    public MainWindow(string[] arguments)
    {
        this.arguments = arguments;
        smoke = arguments.Contains("--smoke");
        var outputIndex = Array.IndexOf(arguments, "--output");
        smokeOutput = outputIndex >= 0 && outputIndex + 1 < arguments.Length
            ? Path.GetFullPath(arguments[outputIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "artifacts");
        // A unique, initially empty sandbox prevents a smoke run from consuming any real journal/settings.
        stateDirectory = smoke ? Path.Combine(smokeOutput, "sandbox-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameModeMonitor");
        settingsStore = new(stateDirectory);
        try { settings = settingsStore.Load(); }
        catch (Exception ex) { settingsError = ex.Message; }
        InitializeComponent();
        ProfilePicker.SelectedIndex = (int)settings.Profile;
        loadingProfiles = false;
        UpdateProfileDescription();
        commands.SlowOperation += NoticeSlowOperation;
        backend = new(commands, NoticeSlowOperation);
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        engine = new(backend, new JsonStateStore(stateDirectory), boot);
        TargetsList.ItemsSource = rows;
        var iconResource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GameModeMonitor.ico"));
        using var iconStream = iconResource.Stream;
        var appIcon = new System.Drawing.Icon(iconStream, 32, 32);
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/GameModeMonitor.ico"));
        tray = new Forms.NotifyIcon { Text = "Modo juego · Monitor", Icon = appIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir monitor", null, (_, _) => Dispatcher.Invoke(ShowMonitor));
        menu.Items.Add("Desplegar panel", null, (_, _) => Dispatcher.Invoke(() => { layout.Expand(); ApplyLayout(); ShowMonitor(); }));
        menu.Items.Add("Salir", null, (_, _) => Dispatcher.Invoke(() => Close()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMonitor);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { Hide(); timer.Stop(); } else if (IsVisible) timer.Start(); };
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            UpdateState();
            if (!smoke && engine.IsPreviousBoot)
                await RunActionAsync("Verificando recuperación de Windows", () => Task.Run(() => engine.ReconcileAfterBootAsync()));
            if (settingsError is not null && !smoke)
                ThemedDialog.Notice(this, "Configuración no disponible", settingsError + "\n\nEl archivo original se conserva. Optimizar y la edición de perfiles están bloqueados; revise settings.json manualmente.");
            await RefreshAsync(); timer.Start();
            if (smoke) await SmokeAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) => { timer.Stop(); tray.Dispose(); appIcon.Dispose(); };
    }
    private async Task RefreshAsync()
    {
        if (refreshing || acting || !IsVisible || WindowState == WindowState.Minimized) return;
        refreshing = true;
        try
        {
            var expanded = layout.Expanded;
            var result = await Task.Run(() =>
            {
                var processes = expanded ? ProcessInventory.Read() : [];
                return (Resource: resources.Read(), Targets: expanded ? backend.Discover(processes) : null, Processes: processes);
            });
            samples++;
            CpuText.Text = $"{result.Resource.CpuPercent:N0}%"; CpuGauge.Value = result.Resource.CpuPercent;
            CompactCpu.Text = $"CPU: {result.Resource.CpuPercent:N0}%";
            var used = result.Resource.TotalMemory - result.Resource.AvailableMemory;
            RamText.Text = $"{used / 1073741824.0:N1} GB";
            RamDetail.Text = $"{result.Resource.AvailableMemory / 1073741824.0:N1} GB libres de {result.Resource.TotalMemory / 1073741824.0:N1} GB";
            var ramPercent = result.Resource.TotalMemory == 0 ? 0 : 100.0 * used / result.Resource.TotalMemory;
            RamGauge.Value = ramPercent; RamPercentText.Text = $"{ramPercent:N0}%"; CompactRam.Text = $"RAM: {ramPercent:N0}%";
            SampleText.Text = DateTime.Now.ToString("HH:mm:ss") + " · en vivo";
            if (result.Targets is not null)
            {
                PopulateTargets(result.Targets);
            }
            TargetCount.Text = rows.Count + " disponibles";
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "No hay objetivos activos del catálogo seguro.";
            if (expanded) ProcessesList.ItemsSource = result.Processes.Where(ProcessInventory.IsDevelopment).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = $"{g.Key}  ×{g.Count()}", Memory = $"{g.Sum(p => p.MemoryBytes) / 1048576.0:N0} MB", Detail = string.Join("\n", g.Select(p => $"PID {p.Pid} · padre {p.ParentPid} · {p.Path}")) })
                .OrderByDescending(g => double.Parse(g.Memory.Split(' ')[0], System.Globalization.CultureInfo.CurrentCulture)).Take(10).ToArray();
            UpdateState();
        }
        catch (Exception ex) { HintText.Text = "No se pudo actualizar el inventario: " + ex.Message; }
        finally { refreshing = false; }
    }
    private void UpdateState()
    {
        StatusText.Text = engine.GetStatus();
        OptimizeButton.IsEnabled = !smoke && !acting && settingsError is null && engine.State is null;
        var interrupted = engine.State?.Entries.Any(e => e.Outcome is Outcome.Pending or Outcome.NeedsReview) == true;
        RestoreButton.IsEnabled = !smoke && !acting && engine.State is not null && (engine.IsPreviousBoot || !interrupted);
        RestoreButton.Content = engine.IsPreviousBoot ? "Verificar recuperación" : "Revertir";
        MiniOptimizeButton.IsEnabled = OptimizeButton.IsEnabled;
        MiniRestoreButton.IsEnabled = RestoreButton.IsEnabled;
        CompactPanel.ToolTip = "Modo juego · " + engine.GetStatus() + " · Arrastre para mover; flecha para desplegar";
        ArchiveButton.IsEnabled = !smoke && !acting && engine.CanArchive;
        CleanButton.IsEnabled = MiniCleanButton.IsEnabled = !smoke && !acting;
        TargetsList.IsEnabled = ProfilePanel.IsEnabled = !smoke && !acting && settingsError is null && engine.State is null;
        if (engine.State is null) { HintText.Text = settingsError is null ? "Optimizar utiliza su perfil guardado; siempre pedirá confirmación. No se garantiza una mejora de FPS." : "Configuración dañada o no disponible: Optimizar está bloqueado. Se conserva el archivo original."; ActionLog.Text = "Todavía no se ejecutaron acciones en esta sesión."; }
        else
        {
            HintText.Text = engine.IsPreviousBoot ? "Windows aún no confirmó todos los servicios; el inicio automático puede demorarse. Revertir solo vuelve a verificar, sin ejecutar órdenes antiguas. Revise Servicios antes de archivar."
                : interrupted ? "Operación interrumpida: revise servicios y herramientas manualmente. Revertir está bloqueado para no solapar una orden que podría seguir activa."
                : "Revertir recupera servicios y abre apps. No reconstruye trabajo, terminales ni servidores. Archive la sesión antes de optimizar otra vez.";
            ActionLog.Text = string.Join("\n\n", engine.State.Entries.Select(e => $"{e.Target.Target.Label}: {OutcomeLabel(e.Outcome)}\n{e.Message}"));
        }
    }
    private static string OutcomeLabel(Outcome outcome) => outcome switch
    {
        Outcome.Pending => "En curso",
        Outcome.Stopped => "Detenido",
        Outcome.PartialStopped => "Cierre parcial",
        Outcome.Failed => "No completado",
        Outcome.Restored => "Recuperado",
        Outcome.RestoreFailed => "Error al recuperar",
        _ => "Revisión manual"
    };
    private void PopulateTargets(IReadOnlyList<Target> targets)
    {
        rows.Clear();
        foreach (var target in targets)
        {
            // Initial values must not invoke a settings write during inventory refresh.
            var row = new TargetRow(target, settings.IsSelected(target.Key));
            row.PropertyChanged += TargetSelectionChanged;
            rows.Add(row);
        }
    }
    private void TargetSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (loadingProfiles || acting || smoke || settingsError is not null || sender is not TargetRow row) return;
        settings.SetSelected(row.Key, row.Selected);
        SaveSettings();
    }
    private void ProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loadingProfiles || acting || smoke || settingsError is not null || ProfilePicker.SelectedIndex < 0) return;
        settings.Profile = (OptimizationProfile)ProfilePicker.SelectedIndex;
        UpdateProfileDescription();
        PopulateTargets(rows.Select(r => r.Target).ToArray());
        SaveSettings();
    }
    private void UpdateProfileDescription()
    {
        ProfileDescription.Text = settings.Profile switch
        {
            OptimizationProfile.Safe => "Seguro · Solo servicios de desarrollo reconocidos con inicio automático.",
            OptimizationProfile.Recommended => "Recomendado · Desarrollo, búsqueda e impresión. Puede limitar búsquedas e impresión temporalmente.",
            _ => "Experimental · Añade diagnósticos y SysMain. La precarga puede empeorar y el beneficio no está garantizado."
        };
        MiniOptimizeButton.ToolTip = "Optimizar · " + (settings.Profile switch { OptimizationProfile.Safe => "Seguro", OptimizationProfile.Recommended => "Recomendado", _ => "Experimental" });
    }
    private bool SaveSettings()
    {
        if (smoke || settingsError is not null) return false;
        try { settingsStore.Save(settings); SettingsStatus.Text = "Perfil guardado. Optimizar aplicará esta selección."; return true; }
        catch (Exception ex)
        {
            settingsError = ex.Message;
            SettingsStatus.Text = "No se pudo guardar. Optimizar está bloqueado; revise la configuración.";
            UpdateState();
            ThemedDialog.Notice(this, "No se pudo guardar el perfil", ex.Message + "\n\nNo se ejecutó ninguna optimización.");
            return false;
        }
    }
    private void SaveProfileClicked(object sender, RoutedEventArgs e) => SaveSettings();
    private void ResetProfileClicked(object sender, RoutedEventArgs e)
    {
        if (acting || smoke || settingsError is not null) return;
        if (!ThemedDialog.Confirm(this, "Restablecer perfil", "Se eliminarán solo las preferencias del perfil actual. No se detendrán ni iniciarán servicios o aplicaciones.", "Restablecer")) return;
        settings.ResetCurrentProfile();
        PopulateTargets(rows.Select(r => r.Target).ToArray());
        SaveSettings();
    }
    private async void OptimizeClicked(object sender, RoutedEventArgs e)
    {
        if (smoke || acting || settingsError is not null || engine.State is not null) return;
        await RunActionAsync("Preparando optimización", async () =>
        {
            // Compact mode inventories on demand rather than relying on expanded rows.
            var active = await Task.Run(() => backend.Discover(ProcessInventory.Read()));
            var selection = active.Where(t => settings.IsSelected(t.Key)).ToArray();
            PopulateTargets(active);
            if (selection.Length == 0)
            {
                ThemedDialog.Notice(this, "Sin objetivos seleccionados", "No hay objetivos activos seleccionados por este perfil. Puede revisar las casillas en Configuración del perfil. No se ejecutó ninguna acción.");
                return;
            }
            if (!ThemedDialog.Confirm(this, "Preparar para jugar", "Guarde su trabajo y termine las operaciones de base de datos. Se intentará cerrar:\n\n" + string.Join("\n", selection.Select(t => "• " + t.Label + "\n  " + ProfileSettings.Impact(t.Key))) + "\n\nNo se forzará el cierre de aplicaciones. Los tipos de inicio no cambian. ¿Continuar?", "Optimizar")) return;
            if (settings.Profile == OptimizationProfile.Experimental && !ThemedDialog.Confirm(this, "Perfil experimental", "Este perfil pausa diagnósticos y precarga si están seleccionados. Puede empeorar los tiempos de carga y no garantiza más FPS.\n\nRevise los impactos indicados. ¿Aplicar el perfil experimental esta vez?", "Aplicar esta vez")) return;
            var virtualized = selection.Any(t => t.Kind is TargetKind.Docker or TargetKind.Wsl);
            if (virtualized && !ThemedDialog.Confirm(this, "Confirmar Docker y WSL", "Al seleccionar Docker se detienen sus contenedores; al seleccionar WSL se apagan todas sus distribuciones. Se perderán las sesiones y procesos Linux activos.\n\nGuarde primero su trabajo. Si Docker falla, no se apagará WSL. Al revertir no se reconstruyen sesiones ni se inician contenedores explícitamente. Docker podría iniciarlos según sus propias políticas.\n\n¿Autoriza estas operaciones?", "Autorizar")) return;
            StatusText.Text = "Optimizando";
            await Task.Run(() => engine.OptimizeAsync(selection, virtualized));
        });
    }
    private async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (smoke || acting || !RestoreButton.IsEnabled) return;
        await RunActionAsync(engine.IsPreviousBoot ? "Verificando recuperación de Windows" : "Restaurando", async () =>
        {
            if (engine.IsPreviousBoot) await Task.Run(() => engine.ReconcileAfterBootAsync());
            else
            {
                if (!ThemedDialog.Confirm(this, "Revertir modo juego", "Se recuperarán los servicios detenidos y se abrirán las aplicaciones cerradas correctamente, sin duplicarlas.\n\nNo se reconstruyen proyectos, trabajo sin guardar, terminales, sesiones WSL ni servidores. Docker puede iniciar contenedores por sus propias políticas.\n\n¿Continuar?", "Revertir")) return;
                await Task.Run(() => engine.RestoreAsync());
            }
            if (engine.NeedsRestart) OfferRestart();
        }, offerRestartOnError: true);
    }
    private void OfferRestart()
    {
        if (smoke) return;
        if (!ThemedDialog.Confirm(this, "Recuperación incompleta", "No se confirmó la recuperación de todos los servicios. Los servicios automáticos pueden tardar en arrancar. Puede revisar Servicios y volver a verificar.\n\nTambién puede reiniciar Windows para recuperar el inicio habitual. El reinicio no recupera trabajo sin guardar, terminales ni sesiones anteriores. ¿Desea preparar un reinicio?", "Preparar reinicio")) return;
        if (!ThemedDialog.Confirm(this, "Confirmar reinicio de Windows", "Guarde todo su trabajo. Esta acción reiniciará Windows inmediatamente.\n\nNo se ejecutará ninguna optimización al iniciar sesión. ¿Reiniciar ahora?", "Reiniciar ahora")) return;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/r", "/t", "0" } });
    }
    private async void CleanClicked(object sender, RoutedEventArgs e)
    {
        if (smoke || acting) return;
        await RunActionAsync("Limpiando memoria del monitor", async () =>
        {
            if (!ThemedDialog.Confirm(this, "Limpiar memoria del monitor", "Solo se intentará reducir la memoria sin uso de este monitor. No se vacía la memoria de juegos, servicios ni otras aplicaciones.\n\nWindows puede volver a cargar esas páginas. La RAM disponible puede subir o bajar por otras aplicaciones y no se garantiza una mejora de FPS.", "Limpiar monitor")) return;
            var result = await Task.Run(() => MemoryCleaner.CleanAsync());
            var delta = result.AvailableDeltaMiB.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.CurrentCulture);
            ThemedDialog.Notice(this, "Medición de memoria", (result.TrimSucceeded ? "Windows aceptó el intento de reducción." : "Windows no realizó la reducción del conjunto de trabajo; puede no ser necesaria.")
                + $"\n\nCambio observado en RAM disponible del sistema: {delta} MiB.\nReducción observada del conjunto de trabajo del monitor: {result.OwnReductionMiB:N1} MiB.\n\nLa variación incluye actividad de todo el sistema; no es memoria liberada garantizada ni un aumento de FPS.");
        });
    }
    private async Task RunActionAsync(string status, Func<Task> action, bool offerRestartOnError = false)
    {
        if (acting) return;
        acting = true; timer.Stop(); UpdateState(); StatusText.Text = status;
        try
        {
            while (refreshing) await Task.Delay(25);
            await action();
        }
        catch (Exception ex)
        {
            ThemedDialog.Notice(this, "Operación no completada", "Se conserva el registro. Revise el estado antes de continuar.\n\n" + ex.Message);
            if (offerRestartOnError && engine.NeedsRestart) OfferRestart();
        }
        finally
        {
            acting = false; UpdateState();
            if (IsVisible && WindowState != WindowState.Minimized) { await RefreshAsync(); timer.Start(); }
        }
    }
    private void ArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (smoke || acting) return;
        if (!ThemedDialog.Confirm(this, "Archivar sesión", "Archivar conserva el registro, pero NO recupera servicios ni aplicaciones pendientes. Después no podrá revertir esta sesión desde el botón.\n\nCompruebe manualmente que no quedan órdenes en curso y recupere los servicios o herramientas pendientes. Las operaciones interrumpidas son inciertas.\n\n¿Archivar y comenzar una sesión nueva?", "Archivar")) return;
        try { engine.Archive(); UpdateState(); } catch (Exception ex) { ThemedDialog.Notice(this, "No se pudo archivar", ex.Message); }
    }
    private void PinChanged(object sender, RoutedEventArgs e) { Topmost = ((System.Windows.Controls.CheckBox)sender).IsChecked == true; }
    private void NoticeSlowOperation(string message) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = "Operación en curso";
        HintText.Text = message;
        CompactPanel.ToolTip = message;
    });
    private void ToggleLayoutClicked(object sender, RoutedEventArgs e) { layout.Toggle(); ApplyLayout(); _ = RefreshAsync(); }
    private void ApplyLayout()
    {
        CompactPanel.Visibility = layout.Expanded ? Visibility.Collapsed : Visibility.Visible;
        FullPanel.Visibility = layout.Expanded ? Visibility.Visible : Visibility.Collapsed;
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(screen.Left, screen.Top));
        var bottomRight = transform.Transform(new Point(screen.Right, screen.Bottom));
        Width = Math.Min(layout.Width, bottomRight.X - topLeft.X);
        Height = Math.Min(layout.Height, bottomRight.Y - topLeft.Y);
        Left = Math.Clamp(Left, topLeft.X, Math.Max(topLeft.X, bottomRight.X - Width));
        Top = Math.Clamp(Top, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - Height));
    }
    private void DragSurface(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var item = e.OriginalSource as DependencyObject; item is not null; item = VisualTreeHelper.GetParent(item))
            if (item is System.Windows.Controls.Primitives.ButtonBase) return;
        DragMove();
    }
    private void TrayClicked(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
    private void ShowMonitor() { Show(); WindowState = WindowState.Normal; Activate(); timer.Start(); _ = RefreshAsync(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (acting) { e.Cancel = true; ThemedDialog.Notice(this, "Operación en curso", "Espere a que termine la operación antes de salir."); return; }
        if (!smoke && engine.State is not null && !ThemedDialog.Confirm(this, "Salir del monitor", "Salir no revierte las acciones. El registro se conservará para recuperar los servicios y aplicaciones después.\n\n¿Salir?", "Salir")) { e.Cancel = true; return; }
        closing = true;
    }
    private async Task SmokeAsync()
    {
        try
        {
            var output = smokeOutput;
            Directory.CreateDirectory(output);
            using var visibleProcess = Process.GetCurrentProcess();
            var visibleCpuBefore = visibleProcess.TotalProcessorTime;
            await Task.Delay(6500);
            visibleProcess.Refresh();
            var visibleCpuMs = (visibleProcess.TotalProcessorTime - visibleCpuBefore).TotalMilliseconds;
            var defaultCompact = !layout.Expanded && ActualHeight == 54 && ActualWidth == 380;
            SavePreview(Path.Combine(output, "bar.png"));
            layout.Expand(); ApplyLayout(); await RefreshAsync(); await Task.Delay(1000);
            SavePreview(Path.Combine(output, "monitor.png"));
            ProfileExpander.IsExpanded = true;
            await Task.Delay(250);
            SavePreview(Path.Combine(output, "settings.png"));
            var preview = new ThemedDialog(this, "Preparar para jugar", "Guarde su trabajo y termine las operaciones de base de datos.\n\n• Servicio de desarrollo\n  Interrumpe conexiones activas.\n• Búsqueda de Windows\n  Pausa la indexación y puede limitar las búsquedas.\n• Cola de impresión\n  Pausa la impresión, incluida la impresión a PDF.\n\nNo se forzará el cierre de aplicaciones. Los tipos de inicio no cambian. No se garantiza una mejora de FPS.\n\nVista de prueba: no se ejecutará ninguna acción.", "Optimizar");
            preview.Loaded += async (_, _) => { await Task.Delay(250); SavePreview(Path.Combine(output, "dialog.png"), preview); preview.Close(); };
            preview.ShowDialog();
            ProfileExpander.IsExpanded = false;
            layout.Toggle(); ApplyLayout(); await Task.Delay(200);
            var collapsedAgain = !layout.Expanded && ActualHeight == 54;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var visibleMemory = process.WorkingSet64;
            var cpuBefore = process.TotalProcessorTime;
            var before = samples;
            WindowState = WindowState.Minimized;
            await Task.Delay(6500);
            process.Refresh();
            var pausedSamples = samples - before;
            var minimizedCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var minimizedMemory = process.WorkingSet64;
            ShowMonitor(); await Task.Delay(2500);
            var report = new { DefaultCompact = defaultCompact, CollapsedAgain = collapsedAgain, VisibleSamples = before, PausedSamples = pausedSamples, ResumedSamples = samples - before, VisibleWorkingSetBytes = visibleMemory, MinimizedWorkingSetBytes = minimizedMemory, VisibleCpuMilliseconds = visibleCpuMs, MinimizedCpuMilliseconds = minimizedCpuMs, IntervalMilliseconds = 6500, ReadOnly = true, IsolatedState = stateDirectory, SettingsFileCreated = File.Exists(settingsStore.FilePath), SessionFileCreated = File.Exists(Path.Combine(stateDirectory, "session.json")) };
            File.WriteAllText(Path.Combine(output, "smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Debug.WriteLine(ex); Environment.ExitCode = 1; }
        finally { Close(); }
    }
    private void SavePreview(string path, Window? window = null)
    {
        window ??= this;
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); png.Save(stream);
    }
}
