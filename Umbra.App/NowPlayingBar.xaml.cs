using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Umbra.Core;
using Wpf.Ui.Controls;

namespace Umbra.App;

public partial class NowPlayingBar : UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _isAdjustingVolume;
    private string? _lastPlayingTrack;
    private TimeSpan? _lastPosition;

    public NowPlayingBar()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        _ = RefreshAsync();
        AmbientSoundService.Changed += RenderAmbientSounds;
        RenderAmbientSounds();

        Loc.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguageChanged;
            AmbientSoundService.Changed -= RenderAmbientSounds;
        };

        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window is null) return;
            UpdateMaximizeIcon(window.WindowState);
            window.StateChanged += (_, _) => UpdateMaximizeIcon(window.WindowState);
        };
    }

    private void OnLanguageChanged() => _ = RefreshAsync();

    private void RenderAmbientSounds()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RenderAmbientSounds); return; }
        AmbientSoundsPanel.Children.Clear();
        foreach (var active in AmbientSoundService.Active)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MinWidth = 178 };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = active.Sound.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 68, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) });
            var slider = new Slider { Width = 82, Minimum = 0, Maximum = 1, Value = active.Volume, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, e) => AmbientSoundService.SetVolume(active.Sound.Id, e.NewValue);
            panel.Children.Add(slider);
            var close = new System.Windows.Controls.Button { Content = "×", Style = (Style)FindResource("IconOnlyButtonStyle"), FontSize = 16, Padding = new Thickness(7, 0, 9, 0) };
            close.Click += (_, _) => AmbientSoundService.Remove(active.Sound.Id);
            panel.Children.Add(close);
            AmbientSoundsPanel.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("ControlFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(6), Height = 42, Margin = new Thickness(4, 0, 4, 0), Child = panel
            });
        }
    }

    private void UpdateMaximizeIcon(WindowState state) =>
        MaximizeIcon.Symbol = state == WindowState.Maximized ? SymbolRegular.SquareMultiple24 : SymbolRegular.Square24;

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null) window.WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            // Comportement natif Windows : glisser depuis une fenêtre
            // maximisée la restaure et la fait suivre le curseur (au lieu de
            // rester bloquée en place) - on calcule où repositionner la
            // fenêtre restaurée pour que le curseur reste au même endroit
            // relatif du bandeau.
            var pointInWindow = e.GetPosition(window);
            var ratioX = pointInWindow.X / window.ActualWidth;
            var screenPoint = PointToScreen(e.GetPosition(this));

            window.WindowState = WindowState.Normal;
            window.Left = screenPoint.X - window.RestoreBounds.Width * ratioX;
            window.Top = screenPoint.Y - e.GetPosition(this).Y;
        }

        window.DragMove();
    }

    private void MediaArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null)
        {
            if (element is System.Windows.Controls.Button or Slider or Thumb) return;
            element = VisualTreeHelper.GetParent(element);
        }
        DragArea_MouseLeftButtonDown(sender, e);
        e.Handled = true;
    }

    private async Task RefreshAsync()
    {
        // "Spotify : afficher la tuile dans l'expérience de session" (Réglages) -
        // la barre reste (drag/min/agrandir/fermer en dépendent), seuls le
        // transport et les infos du morceau se masquent.
        var showTile = Settings.Load().ShowSpotifyTile;
        TransportPanel.Visibility = showTile ? Visibility.Visible : Visibility.Collapsed;
        TrackInfoPanel.Visibility = showTile ? Visibility.Visible : Visibility.Collapsed;
        if (!showTile) return;

        var info = await SpotifyControl.GetNowPlayingAsync();

        if (string.IsNullOrEmpty(info.Title))
        {
            TrackTitleText.Text = Loc.T("nowplaying.none");
            TrackArtistText.Text = "";
        }
        else
        {
            TrackTitleText.Text = info.Title;
            TrackArtistText.Text = string.IsNullOrEmpty(info.Artist) ? "" : $"— {info.Artist}";
        }

        PlayPauseIcon.Symbol = info.Playing ? SymbolRegular.Pause24 : SymbolRegular.Play24;

        // Historique musical "pendant les sessions" pour les statistiques -
        // sondage périodique (3s, le rythme de ce timer) plutôt qu'un vrai
        // suivi début/fin de lecture, largement suffisant pour un classement.
        if (info.Playing && !string.IsNullOrEmpty(info.Title))
        {
            var trackKey = $"{info.Title}\n{info.Artist}";
            var trackChanged = !string.Equals(_lastPlayingTrack, trackKey, StringComparison.Ordinal);
            // Une boucle sur le même morceau garde le même titre/artiste,
            // donc trackChanged seul ne la détecte jamais - un retour brutal
            // de la position de lecture (plus qu'un simple jitter d'horloge
            // sur l'intervalle de 3s de ce sondage) trahit un redémarrage,
            // que ce soit une vraie boucle ou l'utilisateur qui relance le
            // morceau depuis le début.
            var looped = !trackChanged && _lastPosition is { } last && info.Position < last - TimeSpan.FromSeconds(2);
            var isNewPlay = trackChanged || looped;
            await Task.Run(() => MusicHistory.RecordPlayback(info.Title, info.Artist, 3, info.Thumbnail, isNewPlay));
            _lastPlayingTrack = trackKey;
            _lastPosition = info.Position;
        }
        else
        {
            _lastPlayingTrack = null;
            _lastPosition = null;
        }

        // Le volume passe par le mixeur Windows ciblé sur Spotify.exe : sur
        // un autre lecteur (navigateur, VLC...) le curseur ne ferait
        // strictement rien tout en restant manipulable, et GetVolume()
        // renverrait 1 - il vaut mieux le masquer que d'afficher un contrôle
        // mort.
        VolumeSlider.Visibility = info.IsSpotify ? Visibility.Visible : Visibility.Collapsed;

        // Si le bouton est relâché en dehors du curseur (glissement au-delà
        // de son extrémité), PreviewMouseLeftButtonUp ne se déclenche jamais
        // dessus et le drapeau restait bloqué à true : le curseur cessait
        // alors définitivement de se resynchroniser avec le volume réel.
        if (Mouse.LeftButton == MouseButtonState.Released) _isAdjustingVolume = false;

        // On ne réécrit pas la position du slider pendant que l'utilisateur
        // le fait glisser, sinon le refresh périodique (toutes les 3s) lutte
        // avec le geste de la souris.
        if (info.IsSpotify && !_isAdjustingVolume) VolumeSlider.Value = SpotifyControl.GetVolume();

        if (info.Thumbnail is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(info.Thumbnail))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                ThumbnailBrush.ImageSource = bitmap;
                ThumbnailBorder.Visibility = Visibility.Visible;
                FallbackIcon.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                // image corrompue/format inattendu : on retombe sur l'icône par défaut
            }
        }

        ThumbnailBorder.Visibility = Visibility.Collapsed;
        FallbackIcon.Visibility = Visibility.Visible;
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        await SpotifyControl.ControlPlaybackAsync("toggle");
        await RefreshAsync();
    }

    private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isAdjustingVolume = true;
    private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _isAdjustingVolume = false;

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        SpotifyControl.SetVolume((float)e.NewValue);
}
