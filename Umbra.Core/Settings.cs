using System.Text.Json;

namespace Umbra.Core;

// Plus simple que côté Electron à dessein : pas de système de palettes CSS
// ni de particules à reconstruire, WinUI 3 fournit un vrai thème natif
// (clair/sombre/système) directement, et Mica remplace l'image/vidéo de
// fond csS-approximée.
public class AppSettings
{
    public string Language { get; set; } = "fr"; // "fr" | "en"
    public string Theme { get; set; } = "Dark"; // "Dark" | "Light"
    public List<int> DurationPresets { get; set; } = new() { 25, 60, 180 }; // minutes
    public string FocusClockStyle { get; set; } = "halo"; // halo | orbit | arc | digital
    public bool PlayEndOfSessionSound { get; set; } = true;
    public bool PlayEndOfBreakSound { get; set; } = true;
    public bool ShowSpotifyTile { get; set; } = true;
    public string SmartReminderMode { get; set; } = "off"; // off | manual | automatic
    public string SmartReminderTime { get; set; } = "09:00";
    public string? BackgroundImagePath { get; set; } // null = pas d'image de fond (défaut)
    public List<string> RecentBackgroundImages { get; set; } = new(); // les plus récentes en premier
    public double BackgroundOverlayOpacity { get; set; } = 0.88; // 0 = fond très visible, 1 = comme sans image
    public string BackgroundAppearanceMode { get; set; } = "full"; // full | content | navigation
    public double BackgroundBlur { get; set; } = 80;
    public string? FloatingFocusBackgroundPath { get; set; }
    public List<string> RecentFloatingFocusBackgrounds { get; set; } = new();
    public double FloatingFocusBlur { get; set; } = 12;
    public bool ShowSessionTasks { get; set; } // désactivé par défaut, opt-in depuis Réglages
    // Volume par son d'ambiance (id -> 0..1), pour retrouver son mix après
    // un redémarrage - avant, chaque son repartait à 0.55 à chaque relance
    // et même à chaque arrêt/relance du son, alors que le réglage par son
    // est une fonctionnalité mise en avant.
    public Dictionary<string, double> AmbientVolumes { get; set; } = new();
    // Heure suggérée (History.GetSuggestedStartHour) que l'utilisateur a
    // explicitement ignorée dans l'onglet Schedules - ne pas la reproposer
    // tant que le pic d'usage reste sur cette même heure (voir PeriodsPage).
    public int? DismissedSuggestedHour { get; set; }
}

public static class Settings
{
    // Durée la plus longue qu'un slider de la page Focus puisse atteindre
    // (FocusPage.xaml : 120 min en pomodoro, 240 en session libre). Au-delà,
    // une durée prédéfinie était silencieusement ramenée au maximum du
    // slider : le réglage avait simplement l'air cassé.
    public const int MaxPresetMinutes = 240;

    private static AppSettings DefaultSettings() => new();

    public static AppSettings Load()
    {
        if (!File.Exists(Config.SettingsFile)) return DefaultSettings();
        try
        {
            var json = File.ReadAllText(Config.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Json.Options) ?? DefaultSettings();
            if (settings.FocusClockStyle is not ("halo" or "orbit" or "arc" or "digital"))
                settings.FocusClockStyle = "halo";
            return settings;
        }
        catch
        {
            return DefaultSettings();
        }
    }

    public static void Save(AppSettings data)
    {
        AtomicFile.WriteAllText(Config.SettingsFile, JsonSerializer.Serialize(data, Json.Options));
    }
}
