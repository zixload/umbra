using System.Text.Json;

namespace Umbra.Core;

public class TrackPlayTime
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double Seconds { get; set; }
    public byte[]? Thumbnail { get; set; }
    public int PlayCount { get; set; }
}

// Tracks media playback during focus sessions. The in-memory cache matters:
// artwork makes the JSON file several megabytes large, while playback is
// sampled every three seconds. Reloading and rewriting that entire file on
// every sample caused regular UI stalls as the history grew.
public static class MusicHistory
{
    private static readonly Mutex HistoryMutex = new(false, "Local\\UmbraNative.MusicHistory");
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);
    // Les statistiques n'affichent au maximum que les 100 morceaux les plus
    // écoutés (StatsPage.RefreshMusicAsync) : garder une pochette pour
    // chacun des milliers de titres croisés au fil des années ferait gonfler
    // music_history.json jusqu'à des dizaines de Mo - relus et réécrits en
    // entier toutes les 30 s pendant la lecture. On conserve donc l'art des
    // mieux classés seulement ; le reste garde son temps d'écoute.
    private const int ThumbnailBudget = 150;
    private static List<TrackPlayTime>? _cachedData;
    private static string? _cachedPath;
    private static DateTime _cachedFileWriteUtc;
    private static DateTime _lastPersistedUtc;
    private static bool _dirty;
    private static long _revision;

    public static long Revision => Interlocked.Read(ref _revision);

    public static void RecordPlayback(string title, string artist, double seconds, byte[]? thumbnail = null, bool countAsPlay = false)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (!EnterHistoryLock()) return;
        try
        {
            var data = LoadCached();
            var entry = data.FirstOrDefault(t => t.Title == title && t.Artist == artist);
            if (entry is null)
            {
                entry = new TrackPlayTime { Title = title, Artist = artist };
                data.Add(entry);
            }

            entry.Seconds += seconds;
            if (countAsPlay) entry.PlayCount += 1;
            if (thumbnail is { Length: > 0 }) entry.Thumbnail = thumbnail;

            _dirty = true;
            Interlocked.Increment(ref _revision);

            // Persist track changes immediately. Ordinary time samples are
            // batched and flushed on exit, reducing disk work by roughly 10x.
            if (countAsPlay || DateTime.UtcNow - _lastPersistedUtc >= PersistInterval)
                PersistCached();
        }
        finally
        {
            ExitHistoryLock();
        }
    }

    public static List<TrackPlayTime> GetTopTracks(int count) => WithHistoryLock(
        () => LoadCached().OrderByDescending(t => t.Seconds).Take(count).Select(Clone).ToList(),
        new List<TrackPlayTime>());

    public static List<TrackPlayTime> GetAllTracks() => WithHistoryLock(
        () => LoadCached().OrderByDescending(t => t.Seconds).Select(Clone).ToList(),
        new List<TrackPlayTime>());

    public static void Flush() => WithHistoryLock(() =>
    {
        PersistCached();
        return true;
    }, false);

    private static T WithHistoryLock<T>(Func<T> action, T fallback)
    {
        if (!EnterHistoryLock()) return fallback;
        try
        {
            return action();
        }
        finally
        {
            ExitHistoryLock();
        }
    }

    // Renvoie true seulement si le mutex est bien détenu : sans ça, un échec
    // d'attente (rare mais possible : sécurité refusée sur le mutex nommé,
    // arrêt du process) menait quand même au ReleaseMutex du finally, qui
    // lève ApplicationException quand le thread ne détient pas le mutex -
    // une exception de plus sur un chemin qui doit rester silencieux.
    private static bool EnterHistoryLock()
    {
        try
        {
            HistoryMutex.WaitOne();
            return true;
        }
        catch (AbandonedMutexException)
        {
            // Le mutex EST acquis dans ce cas : un autre process l'a laissé
            // sans le relâcher, à nous de le libérer normalement ensuite.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ExitHistoryLock()
    {
        try
        {
            HistoryMutex.ReleaseMutex();
        }
        catch
        {
            // rien à faire de plus : perdre l'historique musical ne doit
            // jamais faire tomber l'application
        }
    }

    private static List<TrackPlayTime> LoadCached()
    {
        var path = Config.MusicHistoryFile;
        if (_cachedData is not null && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (_dirty || LastWriteUtc(path) <= _cachedFileWriteUtc) return _cachedData;
        }

        _cachedPath = path;
        _cachedData = LoadFromDisk(path);
        _cachedFileWriteUtc = LastWriteUtc(path);
        _lastPersistedUtc = DateTime.UtcNow;
        _dirty = false;
        Interlocked.Increment(ref _revision);
        return _cachedData;
    }

    private static List<TrackPlayTime> LoadFromDisk(string path)
    {
        if (!File.Exists(path)) return new List<TrackPlayTime>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<TrackPlayTime>>(json, Json.Options) ?? new List<TrackPlayTime>();
        }
        catch
        {
            return new List<TrackPlayTime>();
        }
    }

    private static DateTime LastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void TrimThumbnails(List<TrackPlayTime> data)
    {
        if (data.Count <= ThumbnailBudget) return;
        foreach (var track in data.OrderByDescending(t => t.Seconds).Skip(ThumbnailBudget))
            track.Thumbnail = null;
    }

    private static void PersistCached()
    {
        if (!_dirty || _cachedData is null || string.IsNullOrWhiteSpace(_cachedPath)) return;

        TrimThumbnails(_cachedData);
        try
        {
            var temporaryPath = _cachedPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_cachedData, Json.Options));
            File.Move(temporaryPath, _cachedPath, overwrite: true);
            _cachedFileWriteUtc = LastWriteUtc(_cachedPath);
            _dirty = false;
        }
        catch
        {
            // Disque plein, dossier verrouillé par un antivirus ou par la
            // synchronisation OneDrive... RecordPlayback est appelé depuis un
            // Task.Run awaité dans un gestionnaire de timer async void
            // (NowPlayingBar) : une exception ici remontait en exception non
            // gérée et fermait purement et simplement l'application en pleine
            // session. On garde _dirty pour réessayer au prochain cycle.
        }
        // Dans les deux cas : ne pas retenter à chaque échantillon de 3 s.
        _lastPersistedUtc = DateTime.UtcNow;
    }

    private static TrackPlayTime Clone(TrackPlayTime track) => new()
    {
        Title = track.Title,
        Artist = track.Artist,
        Seconds = track.Seconds,
        Thumbnail = track.Thumbnail,
        PlayCount = track.PlayCount,
    };
}
