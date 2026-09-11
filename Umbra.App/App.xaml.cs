using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

public partial class App : Application
{
    private CancellationTokenSource? _watchdogCts;
    private TrayIcon? _trayIcon;
    private MainWindow? _dashboard;
    private bool _reallyQuitting;
    private DispatcherTimer? _reminderTimer;
    private DateTime? _lastReminderDate;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private SingleInstanceCoordinator? _singleInstance;

    public string InstalledVersion { get; } = GetInstalledVersion();
    public UpdateUiStatus UpdateStatus { get; private set; } = new(UpdatePhase.Idle, GetInstalledVersion());
    public event Action? UpdateStatusChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterCrashHandlers();

        if (e.Args.Contains("--watchdog"))
        {
            // Processus détaché, headless, aucune fenêtre : c'est lui, et
            // uniquement lui, qui applique les blocages (voir WatchdogLoop
            // dans Umbra.Core) - il survit à la fermeture du tableau de
            // bord. ShutdownMode=OnExplicitShutdown (voir App.xaml) garde
            // le process en vie sans fenêtre.
            _watchdogCts = new CancellationTokenSource();
            _ = WatchdogLoop.RunAsync(NotifyPlaceholder, _watchdogCts.Token);
            return;
        }

        _singleInstance = new SingleInstanceCoordinator("UmbraNative.Desktop");
        if (!_singleInstance.IsPrimary)
        {
            // The pinned taskbar shortcut starts the executable again after
            // the dashboard was hidden to the tray. Wake the existing
            // process, then end this short-lived secondary process.
            _singleInstance.SignalPrimary();
            Shutdown();
            return;
        }

        AppNotifications.Initialize();

        // Applique le thème enregistré (Dark par défaut) - voir AppTheme.cs
        // pour pourquoi ça ne peut pas se limiter à ApplicationThemeManager.
        AppTheme.Apply(Settings.Load().Theme);

        // The executable path can change after an update. Refreshing the
        // per-user native messaging registration on every normal launch keeps
        // Chrome/Vivaldi/Edge/Brave connected without manual repair steps.
        BrowserIntegration.RegisterNativeHost();

        _dashboard = new MainWindow();
        // Fermer la fenêtre (croix) masque vers le systray plutôt que de
        // quitter - seul "Quitter" dans le menu de l'icône termine
        // vraiment le process, pour que le watchdog élevé reste supervisé
        // même quand le tableau de bord n'est pas affiché à l'écran.
        _dashboard.Closing += (_, args) =>
        {
            if (_reallyQuitting) return;
            args.Cancel = true;
            _dashboard.Hide();
        };
        _dashboard.Show();

        _singleInstance.Listen(RequestDashboardActivation);

        _trayIcon = new TrayIcon(_dashboard, "Umbra");
        _trayIcon.Activated += ShowDashboard;
        _trayIcon.MenuOpening += RefreshTrayMenu;
        RefreshTrayMenu();

        VerifyPendingUpdate();

        // Un installateur encore présent au démarrage a déjà fait son travail
        // (c'est lui qui vient de relancer l'app) ou a été abandonné : dans
        // les deux cas c'est 156 Mo de poids mort. En tâche de fond pour ne
        // pas retarder l'affichage du tableau de bord.
        _ = Task.Run(() => Updater.CleanupDownloadedInstallers());

        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _reminderTimer.Tick += (_, _) => CheckSmartReminder();
        _reminderTimer.Start();

        _ = CheckForUpdatesAfterStartupAsync();
    }

    private void ShowDashboard()
    {
        if (_dashboard == null) return;
        _dashboard.Show();
        _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
        // Activate() can be denied when another process initiated the request.
        // Briefly toggling Topmost reliably brings the existing WPF window
        // forward without leaving it permanently above other applications.
        _dashboard.Topmost = true;
        _dashboard.Topmost = false;
        _dashboard.Focus();
    }

    private void RequestDashboardActivation()
    {
        if (_reallyQuitting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(ShowDashboard);
    }

    private void QuickStart(double minutes)
    {
        var s = Session.Load();
        if (!s.Active)
        {
            Session.StartCustom(minutes, hardMode: false, History.DefaultQuest);
            WatchdogSupervisor.Ensure();
        }
        ShowDashboard();
    }

    private void RefreshTrayMenu()
    {
        if (_trayIcon is null) return;
        var items = new List<(string, Action?)>
        {
            (Loc.T("tray.open"), ShowDashboard),
            ("-", null),
        };

        var session = Session.Load();
        var activeSchedule = session.Active
            ? null
            : Periods.GetActivePeriods(Periods.Load(), DateTime.Now).FirstOrDefault();
        if (session.Active)
        {
            var title = session.Kind == "pomodoro" && session.Pomodoro is { } pomodoro
                ? Loc.T(pomodoro.Phase == "break" ? "tray.pomodoro.break" : "tray.pomodoro.focus")
                : string.IsNullOrWhiteSpace(session.QuestName) ? Loc.T("tray.free") : session.QuestName;
            items.Add((string.Format(Loc.T("tray.status"), title, FormatTrayDuration(Session.RemainingSeconds(session))), null));
            items.Add((Loc.T("tray.floating"), FloatingFocusWindow.ShowOrActivate));
            items.Add(Session.CanStop(session)
                ? (Loc.T("tray.stop"), StopCurrentSessionFromTray)
                : (Loc.T("tray.stop.locked"), null));
            items.Add(("-", null));
        }
        else if (activeSchedule is not null)
        {
            var remaining = TimeSpan.FromMinutes(Periods.MinutesUntilEnd(activeSchedule, DateTime.Now));
            var title = string.Format(Loc.T("tray.schedule"), activeSchedule.Name);
            items.Add((string.Format(Loc.T("tray.status"), title, FormatTrayDuration(remaining.TotalSeconds)), null));
            items.Add((Loc.T("tray.floating"), FloatingFocusWindow.ShowOrActivate));
            items.Add(("-", null));
        }
        else
        {
            // Les durées prédéfinies (Réglages) ne servaient qu'à positionner
            // les sliders au premier affichage de Focus, alors que le menu
            // systray proposait 25 et 60 en dur : ajouter une durée ne créait
            // donc aucun raccourci nulle part. C'est ici qu'elles prennent
            // leur sens - les trois premières deviennent des démarrages
            // rapides depuis la zone de notification.
            var presets = Settings.Load().DurationPresets.Where(m => m is > 0 and <= Settings.MaxPresetMinutes).Take(3).ToList();
            if (presets.Count == 0) presets.Add(25);
            foreach (var minutes in presets)
                items.Add((string.Format(Loc.T("focus.quick"), minutes), () => QuickStart(minutes)));
            items.Add(("-", null));
        }

        items.Add((Loc.T("tray.quit"), QuitReally));
        _trayIcon.SetMenu(items);
    }

    private void StopCurrentSessionFromTray()
    {
        var session = Session.Load();
        if (!session.Active || !Session.CanStop(session)) return;
        Session.Stop(session);
        RefreshTrayMenu();
    }

    private static string FormatTrayDuration(double seconds)
    {
        var rounded = Math.Max(0, (int)Math.Ceiling(seconds));
        var duration = TimeSpan.FromSeconds(rounded);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void QuitReally()
    {
        _reallyQuitting = true;
        _reminderTimer?.Stop();
        _trayIcon?.Dispose();
        MusicHistory.Flush();
        Shutdown();
    }

    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (!await _updateGate.WaitAsync(0)) return;
        var watchdogStoppedForUpdate = false;
        var browserHostsStoppedForUpdate = false;
        try
        {
            SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Checking, InstalledVersion));
            var update = await Updater.CheckForUpdateAsync(InstalledVersion);
            if (!update.CheckSucceeded)
            {
                SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Failed, InstalledVersion));
                if (userInitiated) ShowUpdateMessage(Loc.T("update.check.failed"), MessageBoxImage.Warning);
                return;
            }

            if (!update.Available)
            {
                SetUpdateStatus(new UpdateUiStatus(UpdatePhase.UpToDate, InstalledVersion, update.LatestVersion));
                if (userInitiated) ShowUpdateMessage(Loc.T("update.current"), MessageBoxImage.Information);
                return;
            }

            SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Available, InstalledVersion, update.LatestVersion));
            if (IsFocusActivityActive())
            {
                if (userInitiated) ShowUpdateMessage(Loc.T("update.activity.active"), MessageBoxImage.Information);
                else AppNotifications.Show(Loc.T("update.available.title"),
                    string.Format(Loc.T("update.available.notification"), update.LatestVersion));
                return;
            }

            if (!userInitiated && (_dashboard is null || !_dashboard.IsVisible))
            {
                AppNotifications.Show(Loc.T("update.available.title"),
                    string.Format(Loc.T("update.available.notification"), update.LatestVersion));
                return;
            }

            if (!update.CanInstall)
            {
                var openRelease = ShowUpdateQuestion(string.Format(Loc.T("update.manual"), update.LatestVersion));
                if (openRelease && !string.IsNullOrWhiteSpace(update.ReleaseUrl))
                    Process.Start(new ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
                return;
            }

            if (!ShowUpdateQuestion(string.Format(Loc.T("update.prompt"), update.LatestVersion))) return;

            var progress = new Progress<double>(value =>
                SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Downloading, InstalledVersion, update.LatestVersion, value)));
            SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Downloading, InstalledVersion, update.LatestVersion));
            var installerPath = await Updater.DownloadInstallerAsync(update, progress);

            if (IsFocusActivityActive())
            {
                SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Available, InstalledVersion, update.LatestVersion));
                ShowUpdateMessage(Loc.T("update.activity.active"), MessageBoxImage.Information);
                return;
            }
            watchdogStoppedForUpdate = await WatchdogSupervisor.StopForUpdateAsync();
            if (!watchdogStoppedForUpdate)
                throw new InvalidOperationException("The Umbra watchdog did not stop for the update.");
            browserHostsStoppedForUpdate = await BrowserIntegration.StopNativeHostsForUpdateAsync();
            if (!browserHostsStoppedForUpdate)
                throw new InvalidOperationException("The Umbra browser host did not stop for the update.");

            SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Installing, InstalledVersion, update.LatestVersion, 1));

            try
            {
                File.WriteAllText(Config.PendingUpdateVersionFile, update.LatestVersion);
            }
            catch
            {
                // Non bloquant : au pire la vérification post-update au prochain
                // démarrage ne se déclenche pas, comme avant ce correctif.
            }

            var installer = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/UPDATE=1 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath),
            });
            if (installer is null) throw new InvalidOperationException("Unable to start the Umbra installer.");
            QuitReally();
        }
        catch (Exception error)
        {
            CrashReporter.Write(error, "update", InstalledVersion);
            if (browserHostsStoppedForUpdate)
                BrowserIntegration.ResumeNativeHostsAfterFailedUpdate();
            if (watchdogStoppedForUpdate && (Session.Load().Active || Periods.HasEnabledPeriod(Periods.Load())))
                WatchdogSupervisor.Ensure();
            SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Failed, InstalledVersion));
            ShowUpdateMessage(Loc.T("update.install.failed"), MessageBoxImage.Error);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (_reallyQuitting) return;
        await CheckForUpdatesAsync(userInitiated: false);
    }

    private void SetUpdateStatus(UpdateUiStatus status)
    {
        UpdateStatus = status;
        UpdateStatusChanged?.Invoke();
    }

    private bool ShowUpdateQuestion(string message)
    {
        var result = _dashboard is { IsVisible: true }
            ? MessageBox.Show(_dashboard, message, Loc.T("update.available.title"), MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(message, Loc.T("update.available.title"), MessageBoxButton.YesNo, MessageBoxImage.Information);
        return result == MessageBoxResult.Yes;
    }

    private void ShowUpdateMessage(string message, MessageBoxImage image)
    {
        if (_dashboard is { IsVisible: true })
            MessageBox.Show(_dashboard, message, Loc.T("update.available.title"), MessageBoxButton.OK, image);
        else
            MessageBox.Show(message, Loc.T("update.available.title"), MessageBoxButton.OK, image);
    }

    // Consomme le marqueur laissé par CheckForUpdatesAsync juste avant de
    // lancer l'installateur silencieux. S'il est toujours là au démarrage
    // suivant et que la version installée n'a pas bougé, l'installateur a
    // "réussi" sans rien remplacer (cas vécu : process Umbra.exe élevé
    // orphelin bloquant Restart Manager, /SUPPRESSMSGBOXES masquant tout).
    private void VerifyPendingUpdate()
    {
        if (!File.Exists(Config.PendingUpdateVersionFile)) return;

        string expected;
        try
        {
            expected = File.ReadAllText(Config.PendingUpdateVersionFile).Trim();
        }
        catch
        {
            return;
        }
        finally
        {
            try { File.Delete(Config.PendingUpdateVersionFile); } catch { }
        }

        if (string.IsNullOrWhiteSpace(expected)) return;
        if (Updater.CompareVersions(InstalledVersion, expected) >= 0) return;

        SetUpdateStatus(new UpdateUiStatus(UpdatePhase.Failed, InstalledVersion, expected));
        AppNotifications.Show(Loc.T("update.available.title"), Loc.T("update.install.incomplete"));
    }

    private static string GetInstalledVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        if (version is null) return "1.0.0";
        return $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
    }

    private static bool IsFocusActivityActive()
    {
        return UpdateReadiness.Evaluate(Session.Load(), Periods.Load(), DateTime.Now) != UpdateBlockReason.None;
    }

    // Une exception non gérée sur le thread UI fermait l'application. Pour un
    // bloqueur, c'est le pire scénario possible : le watchdog élevé est un
    // process séparé, il continue d'appliquer le blocage (hosts, pare-feu,
    // fermeture d'apps) alors que le tableau de bord ET l'icône systray
    // viennent de disparaître - plus aucun moyen d'arrêter la session. On
    // journalise, on prévient, et on continue.
    private int _recoveredErrorCount;
    private DateTime _recoveredErrorWindowUtc;

    private bool TryRecoverFromUiError()
    {
        var now = DateTime.UtcNow;
        if (now - _recoveredErrorWindowUtc > TimeSpan.FromMinutes(1))
        {
            _recoveredErrorWindowUtc = now;
            _recoveredErrorCount = 0;
        }
        _recoveredErrorCount++;
        // Une erreur isolée se rattrape ; une boucle d'erreurs (rendu cassé,
        // état corrompu) ne se rattrape pas en la masquant - au-delà de cinq
        // en une minute on laisse l'application tomber proprement plutôt que
        // de la faire tourner dans un état incohérent.
        if (_recoveredErrorCount > 5) return false;
        if (_recoveredErrorCount == 1)
        {
            try { AppNotifications.Show(Loc.T("crash.recovered.title"), Loc.T("crash.recovered.body")); }
            catch { /* une notification qui échoue ne doit pas relancer un crash */ }
        }
        return true;
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashReporter.Write(args.Exception, "dispatcher", InstalledVersion);
            if (TryRecoverFromUiError()) args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashReporter.Write(
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal error"),
                "app-domain",
                InstalledVersion);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Write(args.Exception, "unobserved-task", InstalledVersion);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        MusicHistory.Flush();
        base.OnExit(e);
    }

    private void CheckSmartReminder()
    {
        if (_trayIcon is null || Session.Load().Active) return;
        var settings = Settings.Load();
        if (settings.SmartReminderMode == "off") return;

        TimeSpan target;
        if (settings.SmartReminderMode == "automatic")
        {
            var hour = History.GetSuggestedStartHour();
            if (hour is null) return;
            target = TimeSpan.FromHours(hour.Value);
        }
        else if (!TimeSpan.TryParse(settings.SmartReminderTime, out target))
        {
            return;
        }

        var now = DateTime.Now;
        var elapsed = now.TimeOfDay - target;
        if (elapsed.TotalMinutes is < 0 or > 5 || _lastReminderDate == now.Date) return;
        _lastReminderDate = now.Date;
        AppNotifications.Show(Loc.T("reminder.notification.title"), Loc.T("reminder.notification.body"));
    }

    // Notifications toast natives à brancher plus tard - pour l'instant on
    // se contente de logger la transition, pas bloquant pour le reste du
    // portage.
    private static void NotifyPlaceholder(string key) => WatchdogLoop.Log($"notify: {key}");
}
