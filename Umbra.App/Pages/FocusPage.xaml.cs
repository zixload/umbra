using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App.Pages;

public partial class FocusPage : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _loaded;
    private List<SavedBlocklist> _savedBlocklists = new();
    private readonly string _clockStyle;
    private SessionTasksData _tasks = SessionTasks.Load();
    private bool _tasksPanelWasVisible;

    // Hors hard mode, StopButton n'arrête plus au premier clic : ce champ
    // retient l'heure à partir de laquelle le clic suivant confirme
    // vraiment l'arrêt (voir RefreshSessionUi/StopButton_Click). null =
    // aucune confirmation en attente.
    private DateTime? _pendingStopUntil;

    // Non-null quand c'est une PLAGE (pas une session manuelle) qui occupe
    // la vue active - avant, StopButton restait masqué dans ce cas ("rien à
    // arrêter depuis ici, c'est le planning qui pilote"), ce qui laissait
    // une plage hors hard mode totalement inaccessible dès qu'elle couvrait
    // l'heure courante : l'onglet Schedules (seul autre endroit pour la
    // désactiver) est lui-même masqué tant qu'une plage est active. Une
    // plage hard mode reste volontairement non désactivable depuis ici
    // (StopButton.IsEnabled=false, voir RefreshSessionUi).
    private Period? _activePeriod;

    // Suivi des transitions pour déclencher les sons de fin de session/pause
    // (Réglages) - même logique de garde que WatchdogLoop.Enforcer côté
    // Core : jamais de son au tout premier tick (une session déjà en cours
    // au chargement de la page ne doit pas sonner immédiatement).
    private bool _soundStateInitialized;
    private bool _lastActive;
    private string? _lastKind;
    private string? _lastPhase;

    public FocusPage()
    {
        InitializeComponent();

        QuestNameBox.PlaceholderText = Loc.T("focus.quest.placeholder");
        BlocklistLabel.Text = Loc.T("focus.blocklist.label");
        FocusSliderLabel.Text = Loc.T("focus.slider.focus");
        RestSliderLabel.Text = Loc.T("focus.slider.rest");
        RepeatsSliderLabel.Text = Loc.T("focus.slider.repeats");
        HardModeLabel.Text = Loc.T("focus.hardmode");
        EndStatLabel.Text = Loc.T("focus.stat.end");
        DurationStatLabel.Text = Loc.T("focus.stat.duration");
        FocusStatLabel.Text = Loc.T("focus.stat.focus");
        RestStatLabel.Text = Loc.T("focus.stat.rest");
        PomodoroTab.Header = Loc.T("focus.mode.pomodoro");
        FreeTab.Header = Loc.T("focus.mode.free");
        SchedulesTab.Header = Loc.T("focus.mode.schedules");
        FreeDurationLabel.Text = Loc.T("focus.free.duration");
        StopButton.Content = Loc.T("focus.stop");
        CancelStopLink.Content = Loc.T("focus.stop.cancel");
        StartButton.Content = Loc.T("focus.start");
        PopoutButton.ToolTip = Loc.T("focus.popout");

        BuildBlocklistCombo();

        var settings = Settings.Load();
        _clockStyle = settings.FocusClockStyle;
        FocusSlider.Value = settings.DurationPresets.Count > 0 ? settings.DurationPresets[0] : 25;
        RestSlider.Value = 5;
        RepeatsSlider.Value = 2;
        FreeDurationSlider.Value = settings.DurationPresets.Count > 0 ? settings.DurationPresets[0] : 25;

        _loaded = true;
        RefreshSessionUi();

        if (Periods.HasEnabledPeriod(Periods.Load()))
        {
            WatchdogSupervisor.Ensure();
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshSessionUi();
        _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    // "Liste actuelle" (index 0) laisse le blocklist.json actif tel quel -
    // choisir une liste enregistrée la copie dedans au démarrage de la
    // session (voir StartButton_Click), sans toucher au watchdog qui lit
    // déjà toujours cette liste "active" pour le blocage lié aux sessions.
    private void BuildBlocklistCombo()
    {
        _savedBlocklists = SavedBlocklists.Load();
        BlocklistCombo.Items.Clear();
        BlocklistCombo.Items.Add(Loc.T("focus.blocklist.current"));
        foreach (var list in _savedBlocklists) BlocklistCombo.Items.Add(list.Name);
        BlocklistCombo.SelectedIndex = 0;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loaded) UpdatePreview();
    }

    private void FreeDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FreeDurationValue is not null) FreeDurationValue.Text = $"{(int)e.NewValue} min";
    }

    private void FocusModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StartButton is null || FocusModeTabs is null) return;
        var isSchedules = FocusModeTabs.SelectedItem == SchedulesTab;
        StartButton.Visibility = isSchedules ? Visibility.Collapsed : Visibility.Visible;
        HardModePanel.Visibility = isSchedules ? Visibility.Collapsed : Visibility.Visible;
        // Pomodoro/Free tiennent dans les 286px d'origine (juste des
        // sliders) - Schedules a le formulaire "nouvelle plage" + la liste
        // existante, nettement plus haut. Fixer cette ligne grandissait le
        // contenu de l'onglet Schedules dans une boîte trop petite, forçant
        // un scroll interne alors que la page elle-même (ScrollViewer
        // englobant tout FocusPage) a largement la place de grandir.
        TabsRow.Height = isSchedules ? GridLength.Auto : new GridLength(286);
    }

    // Aperçu "si je démarre maintenant" affiché tant qu'aucune session n'est
    // active - Répétitions=0 => session brute (Focus seul, pas de repos, pas
    // de cycles) ; Répétitions>=1 => vrai pomodoro. Les totaux reflètent
    // exactement le calendrier réel de Session.StartPomodoro (le dernier
    // cycle de travail ne termine pas sur un repos - voir Session.cs).
    private void UpdatePreview()
    {
        var focusMin = (int)FocusSlider.Value;
        var restMin = (int)RestSlider.Value;
        var repeats = (int)RepeatsSlider.Value;

        FocusSliderValue.Text = focusMin.ToString();
        RestSliderValue.Text = restMin.ToString();
        RepeatsSliderValue.Text = repeats.ToString();
        RestSlider.IsEnabled = repeats > 0;

        int totalFocus, totalRest;
        if (repeats <= 0)
        {
            totalFocus = focusMin;
            totalRest = 0;
        }
        else
        {
            totalFocus = repeats * focusMin;
            totalRest = Math.Max(0, repeats - 1) * restMin;
        }
        var totalDuration = totalFocus + totalRest;

        EndStatValue.Text = DateTime.Now.AddMinutes(totalDuration).ToString("HH:mm");
        DurationStatValue.Text = FormatHm(totalDuration);
        FocusStatValue.Text = FormatHm(totalFocus);
        RestStatValue.Text = FormatHm(totalRest);
    }

    private static string FormatHm(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    private void RefreshSessionUi()
    {
        var s = Session.Load(); // fait aussi avancer les phases pomodoro dues, termine une session custom expirée côté watchdog
        var active = s.Active;
        // Une plage horaire active sans session manuelle en cours doit avoir
        // le même affichage (anneau, statut) que la fenêtre flottante - voir
        // FloatingFocusWindow.RefreshSession, qui gérait déjà ce cas alors
        // que cet onglet retombait sur l'écran de configuration.
        var activePeriod = active ? null : Periods.GetActivePeriods(Periods.Load(), DateTime.Now).FirstOrDefault();

        ActivePanel.Visibility = active || activePeriod is not null ? Visibility.Visible : Visibility.Collapsed;
        ConfigPanel.Visibility = active || activePeriod is not null ? Visibility.Collapsed : Visibility.Visible;

        if (active)
        {
            _activePeriod = null;
            var remaining = TimeSpan.FromSeconds(Session.RemainingSeconds(s));
            SessionStatusText.Text = s.HardMode ? Loc.T("focus.status.active.hard") : Loc.T("focus.status.active");
            SessionTimeText.Text = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
            var canStop = Session.CanStop(s);
            if (!s.HardMode && _pendingStopUntil is { } until)
            {
                var secondsLeft = (int)Math.Ceiling((until - DateTime.Now).TotalSeconds);
                if (secondsLeft > 0)
                {
                    StopButton.Content = string.Format(Loc.T("focus.stop.confirming"), secondsLeft);
                    StopButton.IsEnabled = false;
                }
                else
                {
                    StopButton.Content = Loc.T("focus.stop.confirm");
                    StopButton.IsEnabled = true;
                }
                CancelStopLink.Visibility = Visibility.Visible;
            }
            else
            {
                StopButton.Content = canStop ? Loc.T("focus.stop") : Loc.T("focus.locked");
                StopButton.IsEnabled = canStop;
                CancelStopLink.Visibility = Visibility.Collapsed;
            }
            StopButton.Visibility = Visibility.Visible;
            var totalSeconds = Math.Max(1, (s.EndTs - s.StartTs) / 1000d);
            ActiveRingHost.Children.Clear();
            ActiveRingHost.Children.Add(RingVisual.BuildFocusTimer(245, Session.RemainingSeconds(s) / totalSeconds,
                (System.Windows.Media.Brush)FindResource("ControlStrokeColorDefaultBrush"),
                (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush"), SessionTimeText.Text, Foreground, _clockStyle));
        }
        else if (activePeriod is not null)
        {
            _pendingStopUntil = null;
            _activePeriod = activePeriod;
            CancelStopLink.Visibility = Visibility.Collapsed;
            double remainingSeconds, totalSeconds;
            if (activePeriod.PomodoroMode)
            {
                bool isBreak;
                (isBreak, remainingSeconds, totalSeconds, _) = Periods.GetPomodoroTiming(activePeriod, DateTime.Now);
                SessionStatusText.Text = string.Format(Loc.T(isBreak ? "focus.status.schedule.break" : "focus.status.schedule.focus"), activePeriod.Name);
            }
            else
            {
                (remainingSeconds, totalSeconds) = Periods.GetTiming(activePeriod, DateTime.Now);
                SessionStatusText.Text = string.Format(Loc.T("focus.status.schedule"), activePeriod.Name);
            }
            var remaining = TimeSpan.FromSeconds(remainingSeconds);
            SessionTimeText.Text = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
            if (activePeriod.HardMode)
            {
                // Verrouillée volontairement (voir WatchdogLoop.Enforcer côté
                // Core) - affichée pour que ce soit clair que ce n'est pas un
                // oubli, mais rien à cliquer : la seule échappatoire reste de
                // fermer le watchdog élevé depuis le Gestionnaire des tâches.
                StopButton.Content = Loc.T("focus.locked");
                StopButton.IsEnabled = false;
            }
            else
            {
                StopButton.Content = Loc.T("focus.schedule.disable");
                StopButton.IsEnabled = true;
            }
            StopButton.Visibility = Visibility.Visible;
            ActiveRingHost.Children.Clear();
            ActiveRingHost.Children.Add(RingVisual.BuildFocusTimer(245, remainingSeconds / totalSeconds,
                (System.Windows.Media.Brush)FindResource("ControlStrokeColorDefaultBrush"),
                (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush"), SessionTimeText.Text, Foreground, _clockStyle));
        }
        else
        {
            _pendingStopUntil = null;
            _activePeriod = null;
            CancelStopLink.Visibility = Visibility.Collapsed;
            UpdatePreview();
        }

        var showTasks = (active || activePeriod is not null) && Settings.Load().ShowSessionTasks;
        SessionTasksPanel.Visibility = showTasks ? Visibility.Visible : Visibility.Collapsed;
        // Ne reconstruit la liste que quand le panneau vient d'apparaître,
        // pas à chaque tick (1s) - sinon un clic sur une case à cocher ou le
        // bouton supprimer se ferait potentiellement voler par un rebuild
        // survenant entre le mouse-down et le mouse-up.
        if (showTasks && !_tasksPanelWasVisible) RenderTasks();
        _tasksPanelWasVisible = showTasks;

        WatchdogStatusText.Text = WatchdogSupervisor.IsAlive() ? Loc.T("focus.watchdog.active") : Loc.T("focus.watchdog.inactive");

        var always = AlwaysBlocklist.Load();
        var alwaysCount = always.Apps.Count + always.Sites.Count;
        AlwaysBlockedStatusText.Visibility = alwaysCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (alwaysCount > 0) AlwaysBlockedStatusText.Text = string.Format(Loc.T("focus.always.active"), alwaysCount);


        CheckSoundTransitions(s);
    }

    private void RenderTasks()
    {
        TasksList.Items.Clear();
        foreach (var task in _tasks.Tasks)
        {
            var check = new CheckBox
            {
                Content = task.Text,
                IsChecked = task.Done,
                Margin = new Thickness(0, 0, 0, 6),
            };
            if (task.Done)
            {
                check.Opacity = 0.55;
                var run = new System.Windows.Documents.Run(task.Text) { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                check.Content = new System.Windows.Controls.TextBlock(run);
            }
            check.Checked += (_, _) => ToggleTask(task.Id, true);
            check.Unchecked += (_, _) => ToggleTask(task.Id, false);

            var remove = new Button
            {
                Style = (Style)FindResource("BareIconButton"),
                Margin = new Thickness(6, 0, 0, 6),
                Opacity = 0.5,
                ToolTip = Loc.T("tasks.remove"),
                Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss24, FontSize = 13 },
            };
            remove.Click += (_, _) => RemoveTask(task.Id);

            var row = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(check);
            TasksList.Items.Add(row);
        }
    }

    private void AddTask_Click(object sender, RoutedEventArgs e) => AddTaskFromBox();

    private void NewTaskBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AddTaskFromBox();
    }

    private void AddTaskFromBox()
    {
        var text = NewTaskBox.Text.Trim();
        if (text.Length == 0) return;
        _tasks.Tasks.Add(new SessionTaskItem { Id = Guid.NewGuid().ToString("N"), Text = text });
        SessionTasks.Save(_tasks);
        NewTaskBox.Text = "";
        RenderTasks();
    }

    private void ToggleTask(string id, bool done)
    {
        var task = _tasks.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;
        task.Done = done;
        SessionTasks.Save(_tasks);
        RenderTasks();
    }

    private void RemoveTask(string id)
    {
        _tasks.Tasks.RemoveAll(t => t.Id == id);
        SessionTasks.Save(_tasks);
        RenderTasks();
    }

    // "Fin de période de focus" = une session custom se termine, ou une
    // phase pomodoro "travail" se termine (pause suivante ou fin de
    // session) ; "fin de pause" = la phase "pause" se termine (retour au
    // travail).
    private void CheckSoundTransitions(SessionState s)
    {
        var curPhase = s.Kind == "pomodoro" && s.Pomodoro != null ? s.Pomodoro.Phase : null;

        if (_soundStateInitialized)
        {
            var focusPeriodEnded =
                (_lastActive && !s.Active && _lastKind != "pomodoro") ||
                (_lastActive && _lastKind == "pomodoro" && _lastPhase == "work" && (curPhase == "break" || !s.Active));
            var breakEnded = _lastActive && _lastKind == "pomodoro" && _lastPhase == "break" && curPhase == "work";

            var settings = Settings.Load();
            // Une vraie notification toast (AppNotifications) joue son propre
            // son, propre et unique - remplace l'ancien SystemSounds.Play()
            // brut qui pouvait se chevaucher/grésiller et qui, de toute façon,
            // n'affichait jamais rien de visible pour l'utilisateur.
            if (focusPeriodEnded && settings.PlayEndOfSessionSound)
                AppNotifications.Show(Loc.T("notify.session.ended.title"), Loc.T("notify.session.ended.body"));
            if (breakEnded && settings.PlayEndOfBreakSound)
                AppNotifications.Show(Loc.T("notify.break.ended.title"), Loc.T("notify.break.ended.body"));
        }

        _soundStateInitialized = true;
        _lastActive = s.Active;
        _lastKind = s.Kind;
        _lastPhase = curPhase;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (FocusModeTabs.SelectedItem == FreeTab)
        {
            StartFreeSession((int)FreeDurationSlider.Value);
            return;
        }
        var focusMin = (int)FocusSlider.Value;
        var restMin = (int)RestSlider.Value;
        var repeats = (int)RepeatsSlider.Value;
        var hardMode = HardModeToggle.IsChecked == true;
        var questName = string.IsNullOrWhiteSpace(QuestNameBox.Text) ? Loc.T("focus.quest.default") : QuestNameBox.Text.Trim();

        if (BlocklistCombo.SelectedIndex > 0)
        {
            var chosen = _savedBlocklists[BlocklistCombo.SelectedIndex - 1];
            Blocklist.Save(new BlocklistData { Apps = new List<string>(chosen.Apps), Sites = new List<string>(chosen.Sites) });
        }

        if (repeats <= 0)
        {
            Session.StartCustom(focusMin, hardMode, questName);
        }
        else
        {
            Session.StartPomodoro(focusMin, restMin, repeats, hardMode, questName);
        }
        WarnIfWatchdogFailed(WatchdogSupervisor.Ensure());
        RefreshSessionUi();
    }

    // L'utilisateur vient de démarrer une session en pensant être protégé -
    // si l'élévation UAC du watchdog a échoué (refusée ou autre), il n'y a
    // aucun blocage réel alors que l'UI affiche une session active. Avant,
    // cet échec était totalement silencieux (voir WatchdogSupervisor.Ensure).
    private static void WarnIfWatchdogFailed(bool watchdogOk)
    {
        if (!watchdogOk)
            AppNotifications.Show(Loc.T("notify.watchdog.failed.title"), Loc.T("notify.watchdog.failed.body"));
    }

    private void StartFreeSession(int minutes)
    {
        var hardMode = HardModeToggle.IsChecked == true;
        var questName = string.IsNullOrWhiteSpace(QuestNameBox.Text) ? Loc.T("focus.quest.default") : QuestNameBox.Text.Trim();
        ApplySelectedBlocklist();
        Session.StartCustom(minutes, hardMode, questName);
        WarnIfWatchdogFailed(WatchdogSupervisor.Ensure());
        RefreshSessionUi();
    }

    private void ApplySelectedBlocklist()
    {
        if (BlocklistCombo.SelectedIndex <= 0) return;
        var chosen = _savedBlocklists[BlocklistCombo.SelectedIndex - 1];
        Blocklist.Save(new BlocklistData { Apps = new List<string>(chosen.Apps), Sites = new List<string>(chosen.Sites) });
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePeriod is { } period)
        {
            if (period.HardMode) return; // pas cliquable de toute façon (IsEnabled=false), filet de sécurité
            var data = Periods.Load();
            var target = data.Periods.FirstOrDefault(p => p.Id == period.Id);
            if (target is not null)
            {
                target.Enabled = false;
                Periods.Save(data);
            }
            _activePeriod = null;
            RefreshSessionUi();
            return;
        }

        var s = Session.Load();
        if (!Session.CanStop(s)) return;

        // Hors hard mode : premier clic = démarre le délai de 10s (voir
        // RefreshSessionUi pour l'affichage du décompte), ne stoppe pas
        // encore - dissuade un clic impulsif sans aller jusqu'au hard mode.
        // Le hard mode a déjà son propre verrou via CanStop, pas besoin de
        // ce délai en plus une fois qu'il autorise l'arrêt (session expirée).
        if (!s.HardMode && _pendingStopUntil is null)
        {
            _pendingStopUntil = DateTime.Now.AddSeconds(10);
            RefreshSessionUi();
            return;
        }
        if (!s.HardMode && DateTime.Now < _pendingStopUntil) return; // bouton désactivé pendant le délai, filet de sécurité

        Session.Stop(s);
        _pendingStopUntil = null;
        RefreshSessionUi();
    }

    private void CancelStop_Click(object sender, RoutedEventArgs e)
    {
        _pendingStopUntil = null;
        RefreshSessionUi();
    }

    private void PopoutButton_Click(object sender, RoutedEventArgs e) => FloatingFocusWindow.ShowOrActivate();
}
