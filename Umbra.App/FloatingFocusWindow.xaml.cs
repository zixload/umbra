using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

public partial class FloatingFocusWindow : Wpf.Ui.Controls.FluentWindow
{
    private static FloatingFocusWindow? _instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _clockStyle;
    private string? _lastArtKey;

    public static void ShowOrActivate()
    {
        if (_instance is { IsLoaded: true }) { _instance.Activate(); return; }
        _instance = new FloatingFocusWindow();
        _instance.Show();
    }

    private FloatingFocusWindow()
    {
        InitializeComponent();
        _clockStyle = Settings.Load().FocusClockStyle;
        ApplyBackground();
        _timer.Tick += async (_, _) => { RefreshSession(); await RefreshSpotify(); };
        _timer.Start();
        // Close() en plus de Stop() : Stop met juste la lecture en pause au
        // début, c'est Close() qui relâche réellement la ressource média.
        // Sans ça, ouvrir/fermer la fenêtre flottante en boucle avec un MP4
        // de fond accumule des ressources natives jusqu'au passage du GC.
        Closed += (_, _) => { _timer.Stop(); BackgroundVideo.Stop(); BackgroundVideo.Close(); _instance = null; };
        RefreshSession();
        _ = RefreshSpotify();
    }

    private void ApplyBackground()
    {
        var settings = Settings.Load();
        var path = settings.FloatingFocusBackgroundPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var effect = new BlurEffect { Radius = settings.FloatingFocusBlur };
        if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            BackgroundVideo.Source = new Uri(path, UriKind.Absolute); BackgroundVideo.Effect = effect; BackgroundVideo.Play();
        }
        else
        {
            BackgroundImage.Source = new BitmapImage(new Uri(path, UriKind.Absolute)); BackgroundImage.Effect = effect;
        }
    }

    private void RefreshSession()
    {
        if (RingHost is null) return;
        var s = Session.Load();
        var seconds = s.Active ? Session.RemainingSeconds(s) : 0d;
        var total = s.Active ? Math.Max(1, (s.EndTs - s.StartTs) / 1000d) : 1d;
        var title = Loc.T("focus.status.none");
        var detail = "";

        if (s.Active && s.Kind == "pomodoro" && s.Pomodoro is { } pomodoro)
        {
            var cycle = Math.Min(pomodoro.CycleIndex + 1, pomodoro.CyclesTotal);
            title = pomodoro.Phase == "break"
                ? string.Format(Loc.T("floating.pomodoro.break"), cycle, pomodoro.CyclesTotal)
                : string.Format(Loc.T("floating.pomodoro.focus"), cycle, pomodoro.CyclesTotal);
            detail = pomodoro.Phase == "break"
                ? Loc.T("floating.pomodoro.next.focus")
                : cycle < pomodoro.CyclesTotal ? string.Format(Loc.T("floating.pomodoro.next.break"), pomodoro.BreakMinutes) : Loc.T("floating.pomodoro.last");
        }
        else if (s.Active)
        {
            title = string.IsNullOrWhiteSpace(s.QuestName) ? Loc.T("floating.free") : s.QuestName;
            detail = $"{Loc.T("floating.free")} · {string.Format(Loc.T("floating.ends"), DateTimeOffset.FromUnixTimeMilliseconds(s.EndTs).ToLocalTime().ToString("HH:mm"))}";
        }
        else
        {
            var period = Periods.GetActivePeriods(Periods.Load(), DateTime.Now).FirstOrDefault();
            if (period is not null)
            {
                if (period.PomodoroMode)
                {
                    bool isBreak;
                    (isBreak, seconds, total, _) = Periods.GetPomodoroTiming(period, DateTime.Now);
                    title = string.Format(Loc.T(isBreak ? "floating.schedule.break" : "floating.schedule.focus"), period.Name);
                }
                else
                {
                    (seconds, total) = Periods.GetTiming(period, DateTime.Now);
                    title = string.Format(Loc.T("floating.schedule"), period.Name);
                }
                detail = string.Format(Loc.T("floating.ends"), period.EndTime);
            }
        }

        var ringSize = Math.Clamp(Math.Min(ActualWidth - 90, ActualHeight - 160), 155, 285);
        RingHost.Children.Clear();
        RingHost.Children.Add(RingVisual.BuildFocusTimer(ringSize, seconds > 0 ? seconds / total : 0,
            new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), new SolidColorBrush(Color.FromRgb(45, 165, 230)),
            seconds > 0 ? $"{(int)(seconds / 60):D2}:{(int)(seconds % 60):D2}" : "--:--", Brushes.White, _clockStyle));
        PhaseText.Text = title;
        ModeDetailText.Text = detail;
        RenderFloatingTasks();
    }

    private void RenderFloatingTasks()
    {
        if (!Settings.Load().ShowSessionTasks)
        {
            FloatingTasksPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var tasks = SessionTasks.Load().Tasks.Where(t => !t.Done).ToList();
        if (tasks.Count == 0)
        {
            FloatingTasksPanel.Visibility = Visibility.Collapsed;
            return;
        }
        FloatingTasksPanel.Visibility = Visibility.Visible;
        FloatingTasksPanel.Children.Clear();
        foreach (var task in tasks.Take(6))
        {
            var dot = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4),
                Opacity = 0.85, Margin = new Thickness(0, 0, 8, 0),
            };
            var text = new TextBlock
            {
                Text = task.Text, FontSize = 11, Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand,
            };
            row.Children.Add(dot);
            row.Children.Add(text);
            var id = task.Id;
            row.MouseLeftButtonUp += (_, _) => CompleteFloatingTask(id);
            FloatingTasksPanel.Children.Add(row);
        }
    }

    private void CompleteFloatingTask(string id)
    {
        var data = SessionTasks.Load();
        var task = data.Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;
        task.Done = true;
        SessionTasks.Save(data);
        RenderFloatingTasks();
    }

    private async Task RefreshSpotify()
    {
        var info = await SpotifyControl.GetNowPlayingAsync();
        SpotifyTitle.Text = string.IsNullOrWhiteSpace(info.Title) ? Loc.T("nowplaying.none") : info.Title;
        SpotifyArtist.Text = info.Artist ?? "";
        if (info.Thumbnail is not { Length: > 0 })
        {
            SpotifyThumbnail.Source = null;
            _lastArtKey = null;
            return;
        }
        // Ce refresh tourne chaque seconde : sans ce garde, la pochette était
        // décodée en BitmapImage 60 fois par minute alors qu'elle ne change
        // qu'au changement de titre (même principe que _lastPlayingTrack dans
        // NowPlayingBar).
        var artKey = $"{info.Title}{info.Artist}{info.Thumbnail.Length}";
        if (artKey == _lastArtKey) return;
        _lastArtKey = artKey;

        var image = new BitmapImage();
        using var stream = new MemoryStream(info.Thumbnail);
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        SpotifyThumbnail.Source = image;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        var point = e.GetPosition(this);
        if (point.X <= 9 || point.Y <= 9 || point.X >= ActualWidth - 9 || point.Y >= ActualHeight - 9) return;
        DragMove();
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshSession();
    private void BackgroundVideo_MediaEnded(object sender, RoutedEventArgs e) { BackgroundVideo.Position = TimeSpan.Zero; BackgroundVideo.Play(); }
}
