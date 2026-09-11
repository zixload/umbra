using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Umbra.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Umbra.App.Pages;

public partial class StatsPage : UserControl
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _musicRefreshGate = new(1, 1);
    private bool _activityFiltersReady;
    private bool _musicExpanded;
    private long _lastMusicRevision = -1;
    private string? _renderedMusicSignature;
    // Clés de ressource réelles de WPF-UI (thème Fluent2/WinUI3), relevées en
    // énumérant les ResourceDictionary fusionnés au runtime plutôt que
    // devinées - évite de retomber dans le piège des clés inventées de la
    // première passe (StaticResource introuvable = XamlParseException).
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // Ambre de la page Stats (chiffre de streak, fill des jours actifs) -
    // référence unique pour que tout élément "positif/actif" de cette page
    // reste visuellement de la même famille de couleur.
    private static readonly Color StreakColor = Color.FromRgb(0xF7, 0xA2, 0x1B);

    public StatsPage()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) =>
        {
            await RefreshMusicAsync(force: false);
            RenderBlockedAttempts();
        };
        Loaded += async (_, _) =>
        {
            await RefreshMusicAsync(force: true);
            if (IsLoaded) _refreshTimer.Start();
        };
        Unloaded += (_, _) => _refreshTimer.Stop();

        StreakLabelText.Text = Loc.T("stats.streak.label");
        StreakDescText.Text = Loc.T("stats.streak.desc");
        MusicHeaderText.Text = Loc.T("stats.music");
        MusicEmptyText.Text = Loc.T("stats.music.empty");
        ActivityLegendText.Text = Loc.T("stats.activity.legend");
        BlockedHeaderText.Text = Loc.T("stats.blocked");
        InitializeActivityFilters();

        RenderStreak();
        RenderSummary();
        RenderActivityHeatmap();
        RenderBlockedAttempts();
    }

    private void InitializeActivityFilters()
    {
        _activityFiltersReady = false;
        var currentYear = DateTime.Now.Year;
        var years = History.Load()
            .Select(entry => DateTimeOffset.FromUnixTimeMilliseconds(entry.EndedAt).ToLocalTime().Year)
            .Append(currentYear)
            .Distinct()
            .OrderByDescending(year => year)
            .ToList();
        foreach (var year in years) ActivityYearBox.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });

        ActivityMonthBox.Items.Add(new ComboBoxItem { Content = Loc.T("stats.activity.year"), Tag = 0 });
        var culture = CultureInfo.GetCultureInfo(Loc.Language == "fr" ? "fr-FR" : "en-US");
        for (var month = 1; month <= 12; month++)
        {
            var name = culture.DateTimeFormat.GetMonthName(month);
            ActivityMonthBox.Items.Add(new ComboBoxItem { Content = culture.TextInfo.ToTitleCase(name), Tag = month });
        }
        ActivityYearBox.SelectedIndex = 0;
        ActivityMonthBox.SelectedIndex = 0;
        _activityFiltersReady = true;
    }

    private void ActivityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activityFiltersReady) RenderActivityHeatmap();
    }

    private void RenderActivityHeatmap()
    {
        if (ActivityYearBox.SelectedItem is not ComboBoxItem { Tag: int year }) return;
        var selectedMonth = ActivityMonthBox.SelectedItem is ComboBoxItem { Tag: int month } ? month : 0;
        var minutesByDate = History.Load()
            .GroupBy(entry => DateTimeOffset.FromUnixTimeMilliseconds(entry.EndedAt).ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => (int)Math.Round(group.Sum(entry => entry.FocusedMinutes)));
        var selectedValues = minutesByDate
            .Where(pair => pair.Key.Year == year && (selectedMonth == 0 || pair.Key.Month == selectedMonth))
            .Select(pair => pair.Value)
            .ToList();
        var max = Math.Max(1, selectedValues.DefaultIfEmpty(0).Max());

        var yearStart = new DateTime(year, 1, 1);
        var mondayOffset = ((int)yearStart.DayOfWeek + 6) % 7;
        var gridStart = yearStart.AddDays(-mondayOffset);
        var yearEnd = new DateTime(year, 12, 31);
        var sundayOffset = (7 - ((int)yearEnd.DayOfWeek + 6) % 7 - 1) % 7;
        var gridEnd = yearEnd.AddDays(sundayOffset);
        var weeks = (int)((gridEnd - gridStart).TotalDays + 1) / 7;

        ActivityHeatmapGrid.Children.Clear();
        ActivityHeatmapGrid.ColumnDefinitions.Clear();
        ActivityHeatmapGrid.RowDefinitions.Clear();
        for (var column = 0; column < weeks; column++) ActivityHeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < 7; row++) ActivityHeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

        for (var index = 0; index < weeks * 7; index++)
        {
            var date = gridStart.AddDays(index);
            var minutes = minutesByDate.GetValueOrDefault(date);
            var inYear = date.Year == year;
            var highlighted = inYear && (selectedMonth == 0 || date.Month == selectedMonth);
            var intensity = minutes == 0 ? 0 : Math.Clamp((double)minutes / max, 0.18, 1);
            var color = intensity == 0
                ? ((SolidColorBrush)Res("ControlFillColorSecondaryBrush")).Color
                : Color.FromRgb((byte)(45 - 15 * intensity), (byte)(115 + 90 * intensity), (byte)(150 + 75 * intensity));
            var cell = new Border
            {
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(color),
                Opacity = highlighted ? 1 : 0.12,
                ToolTip = inYear ? $"{date:dd/MM/yyyy} · {FormatDuration(minutes)}" : null,
            };
            Grid.SetColumn(cell, index / 7);
            Grid.SetRow(cell, index % 7);
            ActivityHeatmapGrid.Children.Add(cell);
        }
    }

    private void RenderBlockedAttempts()
    {
        var attempts = BlockAttemptHistory.GetTop(5);
        var total = BlockAttemptHistory.GetTotal();
        BlockedSummaryText.Text = string.Format(Loc.T("stats.blocked.total"), total);
        BlockedAttemptsList.Items.Clear();
        if (attempts.Count == 0)
        {
            BlockedAttemptsList.Items.Add(new TextBlock { Text = Loc.T("stats.blocked.empty"), Opacity = 0.55 });
            return;
        }

        foreach (var attempt in attempts)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = attempt.Target, TextTrimming = TextTrimming.CharacterEllipsis });
            var count = new TextBlock { Text = $"×{attempt.Count}", FontWeight = FontWeights.SemiBold, Opacity = 0.7 };
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            BlockedAttemptsList.Items.Add(row);
        }
    }

    private async Task RefreshMusicAsync(bool force)
    {
        // The expanded list is a snapshot. Rebuilding up to 100 decoded
        // artworks every ten seconds would create the same periodic stutter
        // this method is designed to avoid; toggling the list refreshes it.
        if (_musicExpanded && !force) return;

        await _musicRefreshGate.WaitAsync();
        try
        {
            var revisionBeforeLoad = MusicHistory.Revision;
            if (!force && revisionBeforeLoad == _lastMusicRevision) return;

            var limit = _musicExpanded ? 100 : 10;
            var tracks = await Task.Run(() => MusicHistory.GetTopTracks(limit));
            var revisionAfterLoad = MusicHistory.Revision;
            if (!IsLoaded) return;

            var signature = BuildMusicSignature(tracks, _musicExpanded);
            _lastMusicRevision = revisionAfterLoad;
            if (!force && string.Equals(signature, _renderedMusicSignature, StringComparison.Ordinal)) return;

            _renderedMusicSignature = signature;
            RenderMusic(tracks);
        }
        finally
        {
            _musicRefreshGate.Release();
        }
    }

    private static string BuildMusicSignature(IEnumerable<TrackPlayTime> tracks, bool expanded) =>
        $"{expanded}|" + string.Join('|', tracks.Select(track =>
            $"{track.Title}\u001f{track.Artist}\u001f{track.PlayCount}\u001f{Math.Round(track.Seconds / 60)}\u001f{track.Thumbnail?.Length ?? 0}"));

    private void RenderMusic(IReadOnlyList<TrackPlayTime> tracks)
    {
        // La vue dépliée reste volontairement plafonnée : créer des centaines
        // de BitmapImage et de tuiles après plusieurs années finirait par
        // rendre l'ouverture de Stats coûteuse. L'historique sur disque, lui,
        // n'est pas tronqué.
        var allTracks = tracks;
        MusicList.Items.Clear();
        AllMusicList.Items.Clear();

        if (tracks.Count == 0)
        {
            MusicEmptyPanel.Visibility = Visibility.Visible;
            MusicList.Visibility = Visibility.Collapsed;
            return;
        }

        MusicEmptyPanel.Visibility = Visibility.Collapsed;
        MusicList.Visibility = _musicExpanded ? Visibility.Collapsed : Visibility.Visible;
        AllMusicList.Visibility = _musicExpanded ? Visibility.Visible : Visibility.Collapsed;
        MusicTotalText.Visibility = _musicExpanded ? Visibility.Visible : Visibility.Collapsed;
        var totalPlays = allTracks.Sum(track => track.PlayCount);
        var totalMinutes = (int)Math.Round(allTracks.Sum(track => track.Seconds) / 60);
        MusicTotalText.Text = Loc.Language == "fr"
            ? $"{allTracks.Count} titre{(allTracks.Count == 1 ? "" : "s")} · {totalPlays} écoute{(totalPlays == 1 ? "" : "s")} · {FormatDuration(totalMinutes)} au total"
            : $"{allTracks.Count} track{(allTracks.Count == 1 ? "" : "s")} · {totalPlays} play{(totalPlays == 1 ? "" : "s")} · {FormatDuration(totalMinutes)} total";
        if (!_musicExpanded)
        {
            for (var i = 0; i < tracks.Count; i++)
                MusicList.Items.Add(BuildTrackTile(tracks[i], i + 1, fillWidth: true));
        }
        else
        {
            for (var i = 0; i < tracks.Count; i++)
                AllMusicList.Items.Add(BuildTrackTile(tracks[i], i + 1));
        }
    }

    private async void MusicHeader_Click(object sender, RoutedEventArgs e)
    {
        _musicExpanded = !_musicExpanded;
        if (MusicExpandGlyph.RenderTransform is RotateTransform rotate)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_musicExpanded ? 180 : 0, TimeSpan.FromMilliseconds(160)));
        await RefreshMusicAsync(force: true);
        if (_musicExpanded)
            AllMusicList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private static Border BuildTrackTile(TrackPlayTime track, int rank, bool fillWidth = false)
    {
        var scale = new ScaleTransform(1, 1);
        var tile = new Border
        {
            Width = fillWidth ? double.NaN : 148,
            Height = 148,
            CornerRadius = new CornerRadius(10),
            Margin = fillWidth ? new Thickness(5, 0, 5, 10) : new Thickness(0, 0, 12, 8),
            HorizontalAlignment = fillWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            ClipToBounds = true,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Background = Res("ControlFillColorSecondaryBrush"),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var content = new Grid();
        content.Children.Add(CreateArtwork(track.Thumbnail));

        var details = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 12, 12, 16)),
            Opacity = 0,
            Padding = new Thickness(12),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        text.Children.Add(new TextBlock
        {
            Text = track.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 42,
        });
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            text.Children.Add(new TextBlock
            {
                Text = track.Artist,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 198)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        var minutes = Math.Max(1, (int)Math.Round(track.Seconds / 60));
        var plays = Loc.Language == "fr"
            ? $"{track.PlayCount} écoute{(track.PlayCount == 1 ? "" : "s")}"
            : $"{track.PlayCount} play{(track.PlayCount == 1 ? "" : "s")}";
        text.Children.Add(new TextBlock
        {
            Text = $"{plays}  ·  {FormatDuration(minutes)}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 195, 225)),
            Margin = new Thickness(0, 7, 0, 0),
        });
        details.Child = text;
        content.Children.Add(details);

        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 18, 18, 22)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Child = new TextBlock { Text = $"#{rank}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
        });
        tile.Child = content;

        tile.ToolTip = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Title} — {track.Artist}";
        tile.MouseEnter += (_, _) => AnimateTrackTile(details, scale, 0.94, 1.035, 160);
        tile.MouseLeave += (_, _) => AnimateTrackTile(details, scale, 0, 1, 140);
        return tile;
    }

    private static void AnimateTrackTile(Border details, ScaleTransform scale, double opacity, double size, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        details.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, duration));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(size, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(size, duration));
    }

    private static FrameworkElement CreateArtwork(byte[]? bytes)
    {
        if (bytes is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.UniformToFill };
            }
            catch
            {
                // Old or corrupted artwork falls back to the music icon.
            }
        }

        return new SymbolIcon
        {
            Symbol = SymbolRegular.MusicNote224,
            FontSize = 42,
            Opacity = 0.35,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void RenderStreak()
    {
        var s = History.GetStats();
        StreakNumberText.Text = s.StreakDays.ToString();
        StreakRecordText.Text = s.BestStreakDays > 0
            ? string.Format(Loc.T("stats.streak.record"), Loc.Language == "fr" ? $"{s.BestStreakDays} jour{(s.BestStreakDays == 1 ? "" : "s")}" : $"{s.BestStreakDays} day{(s.BestStreakDays == 1 ? "" : "s")}")
            : "";
        StreakRecordText.Visibility = s.BestStreakDays > 0 ? Visibility.Visible : Visibility.Collapsed;

        var dayLetters = Loc.DayLetters;
        var weekdays = History.GetCurrentWeekBreakdown();
        var today = (int)DateTime.Now.DayOfWeek;
        WeekdayRow.Children.Clear();
        for (var i = 0; i < weekdays.Count; i++)
        {
            var row = weekdays[i];
            var active = row.Minutes > 0;
            var isToday = row.Dow == today;
            var circle = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(2, 0, 2, 0),
                Background = active ? new SolidColorBrush(StreakColor) : Res("ControlFillColorSecondaryBrush"),
                BorderBrush = isToday ? new SolidColorBrush(StreakColor) : null,
                BorderThickness = isToday ? new Thickness(2) : new Thickness(0),
                Child = new TextBlock
                {
                    Text = dayLetters[i],
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = active ? Brushes.Black : (Brush)Res("TextFillColorSecondaryBrush"),
                },
            };
            WeekdayRow.Children.Add(circle);
        }
    }

    private void RenderSummary()
    {
        var s = History.GetStats();
        SummaryGrid.Children.Clear();

        // Comparaison à la semaine calendaire précédente - seulement si elle
        // a au moins une minute enregistrée, sinon un pourcentage n'a pas de
        // sens (division par zéro / "infini" pour une première semaine).
        string? weekTrendText = null;
        Brush? weekTrendBrush = null;
        if (s.PreviousWeekMinutes > 0)
        {
            var pct = (int)Math.Round((s.WeekMinutes - s.PreviousWeekMinutes) / (double)s.PreviousWeekMinutes * 100);
            weekTrendText = $"{(pct >= 0 ? "+" : "")}{pct}%";
            // Même ambre que le chiffre de streak et le fill des jours actifs
            // (StreakColor) - pas de rouge/vert façon feu tricolore, ça
            // détonnerait avec le reste de la page. La baisse reste lisible
            // via l'opacité réduite plutôt qu'une couleur différente.
            weekTrendBrush = new SolidColorBrush(StreakColor) { Opacity = pct >= 0 ? 1 : 0.5 };
        }

        foreach (var (icon, label, value, trendText, trendBrush) in new (SymbolRegular, string, string, string?, Brush?)[]
        {
            (SymbolRegular.Clock24, Loc.T("stats.today"), FormatDuration(s.TodayMinutes), null, null),
            (SymbolRegular.CalendarLtr24, Loc.T("stats.week"), FormatDuration(s.WeekMinutes), weekTrendText, weekTrendBrush),
            (SymbolRegular.CalendarMonth24, Loc.T("stats.month"), FormatDuration(s.MonthMinutes), null, null),
            (SymbolRegular.Timer24, Loc.T("stats.alltime"), FormatDuration(s.TotalMinutes), null, null),
            (SymbolRegular.Trophy24, Loc.T("stats.bestday"), FormatDuration(s.BestDayMinutes), null, null),
            (SymbolRegular.ArrowTrending24, Loc.T("stats.longest"), FormatDuration(s.LongestSessionMinutes), null, null),
        })
        {
            SummaryGrid.Children.Add(BuildStatRow(icon, label, value, trendText, trendBrush));
        }
    }

    // Au-delà de 60 minutes, "90 min"/"1500 min" est moins lisible qu'un
    // format à unité adaptée - bascule en heures puis en jours, en gardant
    // au plus deux unités (jamais "1d 2h 30min", juste "1d 2h").
    private static string FormatDuration(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;
        // "d" est l'abréviation anglaise - en français c'est "j" (jour),
        // contrairement à "min"/"h" qui sont déjà identiques dans les deux
        // langues et n'avaient donc jamais révélé cet oubli.
        var dayUnit = Loc.Language == "fr" ? "j" : "d";
        if (days > 0) return hours > 0 ? $"{days}{dayUnit} {hours}h" : $"{days}{dayUnit}";
        return mins > 0 ? $"{hours}h {mins}min" : $"{hours}h";
    }

    private static Grid BuildStatRow(SymbolRegular icon, string label, string value, string? trendText = null, Brush? trendBrush = null)
    {
        var grid = new Grid { Margin = new Thickness(8, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconEl = new SymbolIcon { Symbol = icon, FontSize = 18, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        Grid.SetColumn(iconEl, 0);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.6 });
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
        valueRow.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold });
        if (trendText is not null)
        {
            valueRow.Children.Add(new TextBlock
            {
                Text = trendText,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = trendBrush,
                Margin = new Thickness(6, 0, 0, 1),
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
        stack.Children.Add(valueRow);
        Grid.SetColumn(stack, 1);

        grid.Children.Add(iconEl);
        grid.Children.Add(stack);
        return grid;
    }

}
