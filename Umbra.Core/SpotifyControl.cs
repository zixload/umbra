using System.Diagnostics;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Umbra.Core;

public class NowPlayingInfo
{
    public bool Playing { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public byte[]? Thumbnail { get; set; }
    // Le réglage de volume passe par le mixeur Windows ciblé sur Spotify.exe
    // (voir WithSpotifyAudioSession) : il n'a aucun effet sur un autre
    // lecteur, donc l'UI doit pouvoir masquer le curseur dans ce cas plutôt
    // que d'afficher un contrôle qui ne fait rien.
    public bool IsSpotify { get; set; }
    // Position de lecture dans le morceau - permet de distinguer une même
    // chanson qui boucle (la position retombe brutalement vers 0) d'une
    // lecture continue, ce que le titre/artiste seuls ne peuvent pas dire
    // (voir NowPlayingBar.RefreshAsync, comptage des écoutes dans les stats).
    public TimeSpan Position { get; set; }
}

// Interroge/pilote les Global System Media Transport Controls de Windows -
// l'API que Spotify (et la plupart des lecteurs media) utilise pour
// exposer "lecture en cours" à l'OS, sans compte/API Spotify ni élévation.
// Contrairement à la version Electron (qui devait shell-out vers
// PowerShell pour accéder à ces APIs WinRT depuis Node.js), une app .NET
// ciblant Windows les appelle directement - plus rapide, pas de process
// PowerShell à démarrer à chaque appel.
public static class SpotifyControl
{
    private static bool IsSpotifySession(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true;

    // Spotify d'abord (c'est le cas d'usage principal, et le seul dont on
    // sache piloter le volume), sinon la session média courante de Windows -
    // navigateur, Apple Music, VLC, foobar... Avant, seul Spotify était
    // reconnu : tout autre lecteur affichait "Aucune lecture en cours" en
    // permanence et ne comptait jamais rien dans les statistiques d'écoute,
    // alors que les GSMTC exposent tous les lecteurs de la même façon.
    private static async Task<GlobalSystemMediaTransportControlsSession?> FindSessionAsync()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return mgr.GetSessions().FirstOrDefault(IsSpotifySession) ?? mgr.GetCurrentSession();
    }

    public static async Task<NowPlayingInfo> GetNowPlayingAsync()
    {
        try
        {
            var session = await FindSessionAsync();
            if (session == null) return new NowPlayingInfo { Playing = false };

            var props = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();
            var isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            byte[]? thumbnail = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var streamRef = await props.Thumbnail.OpenReadAsync();
                    using var netStream = streamRef.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms);
                    thumbnail = ms.ToArray();
                }
                catch
                {
                    thumbnail = null;
                }
            }

            var timeline = session.GetTimelineProperties();

            return new NowPlayingInfo
            {
                Playing = isPlaying,
                Title = props.Title ?? "",
                Artist = props.Artist ?? "",
                Thumbnail = thumbnail,
                Position = timeline.Position,
                IsSpotify = IsSpotifySession(session),
            };
        }
        catch
        {
            return new NowPlayingInfo { Playing = false };
        }
    }

    public static async Task<bool> ControlPlaybackAsync(string action)
    {
        try
        {
            var session = await FindSessionAsync();
            if (session == null) return false;

            return action switch
            {
                "previous" => await session.TrySkipPreviousAsync(),
                "toggle" => await session.TryTogglePlayPauseAsync(),
                "next" => await session.TrySkipNextAsync(),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    // Le volume par application n'existe pas dans les GSMTC - seul le
    // mixeur audio Windows (Core Audio / WASAPI) l'expose, par session liée
    // au PID du process qui joue le son. On retrouve la session de
    // Spotify.exe à chaque appel plutôt que de garder une référence : les
    // sessions WASAPI peuvent disparaître/réapparaître (lecture arrêtée,
    // app relancée), les retrouver à chaque fois est plus robuste.
    private static void WithSpotifyAudioSession(Action<AudioSessionControl> action)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    using var proc = Process.GetProcessById((int)session.GetProcessID);
                    if (proc.ProcessName.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                    {
                        action(session);
                        return;
                    }
                }
                catch
                {
                    // process disparu entre l'énumération et l'accès, ou accès refusé
                }
            }
        }
        catch
        {
            // pas de périphérique de rendu par défaut, ou API Core Audio indisponible
        }
    }

    public static float GetVolume()
    {
        var volume = 1f;
        WithSpotifyAudioSession(s => volume = s.SimpleAudioVolume.Volume);
        return volume;
    }

    public static void SetVolume(float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        WithSpotifyAudioSession(s => s.SimpleAudioVolume.Volume = clamped);
    }
}
