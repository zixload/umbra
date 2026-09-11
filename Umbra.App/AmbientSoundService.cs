using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Umbra.Core;

namespace Umbra.App;

public sealed record AmbientSound(string Id, string Name, string AudioFile, string ImageFile);
public sealed record ActiveAmbientSound(AmbientSound Sound, double Volume);

public static class AmbientSoundService
{
    private sealed class PlayerState(AmbientSound sound, MediaPlayer player)
    {
        public AmbientSound Sound { get; } = sound;
        public MediaPlayer Player { get; } = player;
    }

    public const int MaxActive = 3;
    private const double DefaultVolume = 0.55;
    private static readonly Dictionary<string, PlayerState> Players = new(StringComparer.OrdinalIgnoreCase);
    public static event Action? Changed;

    // Le slider de volume (NowPlayingBar) déclenche SetVolume en continu
    // pendant le glissement : on regroupe les écritures plutôt que de
    // réécrire settings.json des dizaines de fois par glissement.
    private static readonly DispatcherTimer SaveVolumesTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    static AmbientSoundService()
    {
        SaveVolumesTimer.Tick += (_, _) => { SaveVolumesTimer.Stop(); PersistVolumes(); };
    }

    private static void PersistVolumes()
    {
        try
        {
            var settings = Settings.Load();
            foreach (var state in Players.Values) settings.AmbientVolumes[state.Sound.Id] = state.Player.Volume;
            Settings.Save(settings);
        }
        catch
        {
            // Best-effort : perdre un réglage de volume ne doit jamais
            // interrompre la lecture en cours.
        }
    }

    private static double SavedVolume(string id)
    {
        try
        {
            return Settings.Load().AmbientVolumes.TryGetValue(id, out var volume) ? Math.Clamp(volume, 0, 1) : DefaultVolume;
        }
        catch
        {
            return DefaultVolume;
        }
    }

    public static IReadOnlyList<AmbientSound> Catalog { get; } =
    [
        new("birds", "Birds", "birds.wav", "birds.png"),
        new("waterfall", "Waterfall", "waterfall.wav", "waterfall.png"),
        new("coffeeshop", "Coffee Shop", "coffeeshop.mp3", "coffeeshop.png"),
        new("wind", "Wind", "wind.wav", "wind.png"),
        new("creek", "Creek", "creek.wav", "creek.png"),
        new("beach", "Beach", "beach.wav", "beach.png"),
        new("underwater", "Underwater", "underwater.wav", "underwater.png"),
        new("citystreet", "City Street", "citystreet.wav", "citystreet.png"),
        new("rain", "Rain", "rain.wav", "rain.png"),
        new("rainforest", "Rainforest", "Rainforest.mp3", "rainforest.png"),
        new("whitenoise", "White Noise", "whitenoise.wav", "whitenoise.png"),
        new("thunder", "Thunder", "thunder.mp3", "thunder.png"),
        new("fireplace", "Fireplace", "fireplace.wav", "fireplace.png"),
        new("gangnamrain", "Gangnam Rain", "gangnamrain.mp3", "gangnamrain.png"),
        new("hongdaenight", "Hongdae Night", "hongdaenight.mp3", "hongdaenight.png"),
        new("tokyorain", "Tokyo Rain", "tokyorain.mp3", "tokyorain.png"),
        new("shibuyanight", "Shibuya Night", "shibuyanight.mp3", "shibuyanight.png"),
        new("neoncity", "Neon City Rain", "neoncity.mp3", "neoncity.png"),
        new("neonstreets", "Cyberpunk City", "neonstreets.mp3", "neonstreets.png"),
        new("forestbirds", "Forest Birds", "forestbirds.mp3", "forestbirds.png"),
        new("windgusts", "Wind Gusts", "windgusts.mp3", "windgusts.png"),
        new("mtfuji", "Sakura Bloom", "mtfuji.mp3", "mtfuji.png")
    ];

    public static IReadOnlyList<ActiveAmbientSound> Active => Players.Values
        .Select(x => new ActiveAmbientSound(x.Sound, x.Player.Volume)).ToList();

    public static string ImagePath(AmbientSound sound) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Ambient", "Images", sound.ImageFile);

    public static bool Toggle(AmbientSound sound)
    {
        if (Players.ContainsKey(sound.Id))
        {
            // Enregistrer AVANT de retirer le lecteur : PersistVolumes ne
            // parcourt que les sons actifs, donc un volume changé dans les
            // 700 ms précédant l'arrêt serait perdu (le timer n'a pas encore
            // tiré) et le son repartirait au volume par défaut au rallumage.
            PersistVolumes();
            SaveVolumesTimer.Stop();
            Players.Remove(sound.Id, out var existing);
            existing!.Player.Close();
            Changed?.Invoke();
            return true;
        }
        if (Players.Count >= MaxActive) return false;

        var player = new MediaPlayer { Volume = SavedVolume(sound.Id) };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Ambient", "Sounds", sound.AudioFile);
        player.Open(new Uri(path, UriKind.Absolute));
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.Zero; player.Play(); };
        Players[sound.Id] = new PlayerState(sound, player);
        player.Play();
        Changed?.Invoke();
        return true;
    }

    public static bool IsActive(string id) => Players.ContainsKey(id);

    public static void SetVolume(string id, double volume)
    {
        if (!Players.TryGetValue(id, out var state)) return;
        state.Player.Volume = Math.Clamp(volume, 0, 1);
        SaveVolumesTimer.Stop();
        SaveVolumesTimer.Start();
    }

    public static void Remove(string id)
    {
        if (!Players.ContainsKey(id)) return;
        PersistVolumes(); // même raison que dans Toggle : garder le volume du son qu'on retire
        SaveVolumesTimer.Stop();
        Players.Remove(id, out var state);
        state!.Player.Close();
        Changed?.Invoke();
    }
}
