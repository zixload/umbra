using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Umbra.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Umbra.App.Pages;

public partial class PeriodsPage : UserControl
{
    // Period.Days utilise DayOfWeek natif (0=dimanche..6=samedi) - Loc.DayLetters
    // est affiché lundi->dimanche, donc cette table convertit index affiché -> DayOfWeek.
    private static readonly int[] DayIndexToDow = [1, 2, 3, 4, 5, 6, 0];

    private PeriodsData _data = Periods.Load();
    private int? _suggestedHour;
    // Non-null pendant que le formulaire "Nouvelle plage" modifie une plage
    // EXISTANTE (voir StartEditing) au lieu d'en créer une nouvelle - c'est
    // une référence directe vers l'objet déjà présent dans _data.Periods,
    // donc le muter puis appeler Periods.Save(_data) suffit à persister.
    private Period? _editingPeriod;
    private readonly HashSet<int> _selectedDays = new();
    private readonly List<ToggleButton> _dayButtons = new();
    private List<SavedBlocklist> _savedBlocklists = new();
    private readonly DispatcherTimer _refreshTimer;

    public PeriodsPage()
    {
        InitializeComponent();

        NewPeriodExpander.Header = Loc.T("periods.new");
        NameBox.Text = Loc.T("periods.name.default");
        StartLabel.Text = Loc.T("periods.start");
        EndLabel.Text = Loc.T("periods.end");
        DaysLabel.Text = Loc.T("periods.days");
        BlocklistLabel.Text = Loc.T("periods.blocklist.label");
        HardModeLabel.Text = Loc.T("periods.hardmode");
        HardModeDescText.Text = Loc.T("periods.hardmode.desc");
        PomodoroModeLabel.Text = Loc.T("periods.pomodoro");
        PomodoroModeDescText.Text = Loc.T("periods.pomodoro.desc");
        AddPeriodButton.Content = Loc.T("periods.add");
        SuggestionCreateButton.Content = Loc.T("periods.suggestion.create");
        SuggestionDismissButton.Content = Loc.T("periods.suggestion.dismiss");

        BuildDayButtons();
        BuildBlocklistCombo();
        Render();
        RenderSuggestion();

        // Une plage peut entrer/sortir de sa fenêtre active pendant que cet
        // onglet reste ouvert (le TabControl parent ne recrée pas cette page
        // en changeant d'onglet) - sans ça, le verrou hard mode resterait
        // figé à l'état observé au chargement de la page.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Render();
        _refreshTimer.Start();
        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    // "Aucune" (index 0) laisse la plage sans apps/sites propres (comme
    // avant) - choisir une liste enregistrée copie ses apps/sites dans la
    // plage à la création, exactement comme Period.Apps/Sites le permet déjà
    // côté watchdog (WatchdogLoop additionne les listes des plages actives).
    private void BuildBlocklistCombo()
    {
        _savedBlocklists = SavedBlocklists.Load();
        BlocklistCombo.Items.Clear();
        BlocklistCombo.Items.Add(Loc.T("periods.blocklist.none"));
        foreach (var list in _savedBlocklists) BlocklistCombo.Items.Add(list.Name);
        BlocklistCombo.SelectedIndex = 0;
    }

    private void BuildDayButtons()
    {
        DaysPanel.Children.Clear();
        _dayButtons.Clear();
        var dayLabels = Loc.DayLetters;
        for (var i = 0; i < dayLabels.Length; i++)
        {
            var dow = DayIndexToDow[i];
            var btn = new ToggleButton
            {
                Content = dayLabels[i],
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
            };
            btn.Checked += (_, _) => _selectedDays.Add(dow);
            btn.Unchecked += (_, _) => _selectedDays.Remove(dow);
            _dayButtons.Add(btn);
            DaysPanel.Children.Add(btn);
        }
    }

    private void Render()
    {
        PeriodsList.Items.Clear();
        foreach (var p in _data.Periods) PeriodsList.Items.Add(BuildRow(p));
    }

    // Nudge doux basé sur l'heure de décrochage la plus fréquente
    // (History.GetSuggestedStartHour, 30 derniers jours) - jamais insistant :
    // une fois ignorée pour une heure donnée, elle ne revient que si le pic
    // d'usage se déplace vers une autre heure.
    private void RenderSuggestion()
    {
        var settings = Settings.Load();
        var suggestedHour = History.GetSuggestedStartHour();
        var alreadyCovered = suggestedHour is { } hour &&
            _data.Periods.Any(p => Periods.TryParseTime(p.StartTime, out var start) && (int)start.TotalHours == hour);

        if (suggestedHour is not { } h || h == settings.DismissedSuggestedHour || alreadyCovered)
        {
            _suggestedHour = null;
            SuggestionCard.Visibility = Visibility.Collapsed;
            return;
        }

        _suggestedHour = h;
        SuggestionText.Text = string.Format(Loc.T("periods.suggestion.text"), $"{h:D2}:00");
        SuggestionCard.Visibility = Visibility.Visible;
    }

    private void SuggestionCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestedHour is not { } hour) return;
        NewPeriodExpander.IsExpanded = true;
        StartBox.Text = $"{hour:D2}:00";
        EndBox.Text = $"{(hour + 1) % 24:D2}:00";
        SuggestionCard.Visibility = Visibility.Collapsed;
    }

    private void SuggestionDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestedHour is not { } hour) return;
        var settings = Settings.Load();
        settings.DismissedSuggestedHour = hour;
        Settings.Save(settings);
        SuggestionCard.Visibility = Visibility.Collapsed;
    }

    private CardControl BuildRow(Period p)
    {
        // Le mode hard d'une session manuelle serait contournable en
        // désactivant/supprimant simplement la plage qui bloque à la place -
        // le verrou ne s'applique donc que pendant que CETTE plage bloque
        // activement (PeriodCoversNow), jamais en dehors de sa fenêtre.
        var locked = p.HardMode && Periods.PeriodCoversNow(p, DateTime.Now);

        var enabledBox = new CheckBox
        {
            Content = p.Name,
            IsChecked = p.Enabled,
            IsEnabled = !locked,
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabledBox.Checked += (_, _) => { p.Enabled = true; Periods.Save(_data); };
        enabledBox.Unchecked += (_, _) => { p.Enabled = false; Periods.Save(_data); };

        var dayLabels = Loc.DayLetters;
        var days = string.Concat(Enumerable.Range(0, 7).Select(i => p.Days.Contains(DayIndexToDow[i]) ? dayLabels[i] : "·"));
        var blockCount = p.Apps.Count + p.Sites.Count;
        var blockSuffix = blockCount > 0 ? $"  ·  {blockCount}" : "";
        var detail = new TextBlock
        {
            Text = $"{p.StartTime}–{p.EndTime}  {days}{blockSuffix}",
            Opacity = 0.6,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        // Éditer une plage verrouillée permettrait de contourner le hard mode
        // aussi sûrement que la désactiver/supprimer (reculer EndTime, décocher
        // HardMode...) - même garde que pour enabledBox/removeBtn.
        var editBtn = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24 },
            Appearance = ControlAppearance.Secondary,
            ToolTip = Loc.T("periods.edit"),
            IsEnabled = !locked,
            Margin = new Thickness(0, 0, 6, 0),
        };
        editBtn.Click += (_, _) => StartEditing(p);

        var removeBtn = new Wpf.Ui.Controls.Button
        {
            Content = locked ? Loc.T("focus.locked") : Loc.T("periods.remove"),
            Appearance = ControlAppearance.Danger,
            IsEnabled = !locked,
        };
        removeBtn.Click += (_, _) =>
        {
            _data.Periods.Remove(p);
            Periods.Save(_data);
            if (_editingPeriod == p) ResetForm();
            Render();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(editBtn);
        actions.Children.Add(removeBtn);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (p.HardMode) left.Children.Add(new SymbolIcon { Symbol = SymbolRegular.LockClosed24, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        if (p.PomodoroMode) left.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Timer24, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(enabledBox);
        left.Children.Add(detail);

        return new CardControl { Header = left, Content = actions, Margin = new Thickness(0, 0, 0, 6) };
    }

    // Remplit le formulaire "Nouvelle plage" avec les valeurs de p et bascule
    // AddPeriodButton en mode "enregistrer les modifications" (voir
    // AddPeriod_Click) - pas de lien fiable vers un éventuel profil de
    // blocage d'origine (Apps/Sites sont copiés puis indépendants une fois
    // la plage créée), donc BlocklistCombo reste sur "Aucune" : modifier les
    // apps/sites d'une plage existante se fait via un nouveau choix explicite.
    private void StartEditing(Period p)
    {
        _editingPeriod = p;
        NewPeriodExpander.IsExpanded = true;
        NewPeriodExpander.Header = Loc.T("periods.editing");
        NameBox.Text = p.Name;
        StartBox.Text = p.StartTime;
        EndBox.Text = p.EndTime;
        foreach (var b in _dayButtons) b.IsChecked = false;
        _selectedDays.Clear();
        for (var i = 0; i < DayIndexToDow.Length; i++)
        {
            if (p.Days.Contains(DayIndexToDow[i])) _dayButtons[i].IsChecked = true;
        }
        BlocklistCombo.SelectedIndex = 0;
        HardModeToggle.IsChecked = p.HardMode;
        PomodoroModeToggle.IsChecked = p.PomodoroMode;
        AddPeriodButton.Content = Loc.T("periods.save.changes");
        CancelEditButton.Content = Loc.T("periods.edit.cancel");
        CancelEditButton.Visibility = Visibility.Visible;
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ResetForm();

    private void ResetForm()
    {
        foreach (var b in _dayButtons) b.IsChecked = false;
        _selectedDays.Clear();
        NameBox.Text = Loc.T("periods.name.default");
        StartBox.Text = "22:00";
        EndBox.Text = "07:00";
        BlocklistCombo.SelectedIndex = 0;
        HardModeToggle.IsChecked = false;
        PomodoroModeToggle.IsChecked = false;
        _editingPeriod = null;
        NewPeriodExpander.Header = Loc.T("periods.new");
        AddPeriodButton.Content = Loc.T("periods.add");
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    // "13.00" au lieu de "13:00" ne lève aucune erreur au clic : PeriodCoversNow
    // (parseur maison, Split(':')) et GetTiming/GetPomodoroTiming (TimeSpan.TryParse)
    // gèrent l'entrée invalide chacun à sa façon (silencieusement minuit pour l'un,
    // fenêtre figée à 0 pour l'autre) - la plage se retrouve active sur une fenêtre
    // totalement différente de celle tapée, sans jamais prévenir personne. D'où cette
    // validation au moment de la création, avant que la valeur invalide n'atteigne
    // periods.json.
    private static bool IsValidTime(string text) => Periods.TryParseTime(text, out _);

    private void AddPeriod_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? Loc.T("periods.name.default") : NameBox.Text.Trim();
        if (_selectedDays.Count == 0) return;

        var startTime = StartBox.Text.Trim();
        var endTime = EndBox.Text.Trim();
        if (!IsValidTime(startTime) || !IsValidTime(endTime))
        {
            AppNotifications.Show(Loc.T("notify.period.invalidtime.title"), Loc.T("notify.period.invalidtime.body"));
            return;
        }

        if (_editingPeriod is { } editing)
        {
            editing.Name = name;
            editing.Days = _selectedDays.ToList();
            editing.StartTime = startTime;
            editing.EndTime = endTime;
            editing.HardMode = HardModeToggle.IsChecked == true;
            editing.PomodoroMode = PomodoroModeToggle.IsChecked == true;
            if (BlocklistCombo.SelectedIndex > 0)
            {
                var chosen = _savedBlocklists[BlocklistCombo.SelectedIndex - 1];
                editing.Apps = new List<string>(chosen.Apps);
                editing.Sites = new List<string>(chosen.Sites);
            }
            Periods.Save(_data);
            ResetForm();
            Render();
            RenderSuggestion();
            return;
        }

        var period = new Period
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Enabled = true,
            Recurring = true,
            Days = _selectedDays.ToList(),
            StartTime = startTime,
            EndTime = endTime,
            HardMode = HardModeToggle.IsChecked == true,
            PomodoroMode = PomodoroModeToggle.IsChecked == true,
        };
        if (BlocklistCombo.SelectedIndex > 0)
        {
            var chosen = _savedBlocklists[BlocklistCombo.SelectedIndex - 1];
            period.Apps = new List<string>(chosen.Apps);
            period.Sites = new List<string>(chosen.Sites);
        }
        _data.Periods.Add(period);
        Periods.Save(_data);
        ResetForm();
        Render();
        RenderSuggestion();
    }
}
