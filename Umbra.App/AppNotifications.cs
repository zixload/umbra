using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Umbra.App;

// TrayIcon.ShowNotification utilisait Shell_NotifyIcon (P/Invoke direct,
// voir TrayIcon.cs) : ces "balloons" n'ont pas d'identité d'app Windows
// (AUMID), donc Umbra n'apparaît jamais dans Réglages > Notifications, et
// Windows peut les faire disparaître silencieusement selon la config.
// ToastNotificationManagerCompat (API recommandée du paquet, contrairement
// à DesktopNotificationManagerCompat) gère l'identité AUMID pour une app
// Win32 non empaquetée (pas de MSIX) sans avoir besoin de retoucher le
// raccourci du menu Démarrer ni d'enregistrer de classe COM manuellement.
internal static class AppNotifications
{
    private static bool _initialized;

    // ms-appx:/// (custom toast audio "normal") ne marche que pour une app
    // empaquetée (MSIX). file:/// vers le chemin absolu semblait une
    // alternative raisonnable pour une app non empaquetée, mais vérifié en
    // conditions réelles : le son ne joue pas du tout (silence, pas de
    // fallback sur le son par défaut). Ce n'est donc pas un mécanisme
    // documenté/fiable ici - le son custom est joué directement par l'app
    // via MediaPlayer (même mécanisme déjà fiable pour les sons d'ambiance,
    // voir AmbientSoundService) plutôt que de dépendre du système de toast
    // pour ça ; le toast lui-même est rendu silencieux (silent:true avec un
    // src ms-winsoundevent: valide en placeholder) pour ne pas superposer un
    // second son par-dessus.
    private static readonly string NotificationSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "notification.mp3");

    // Garde une racine GC pour chaque lecture en cours - un MediaPlayer local
    // sans référence externe (même abonné à son propre évènement MediaEnded,
    // une référence circulaire non rattachée à une racine) peut être
    // ramassé par le GC en plein milieu de la lecture. Une liste plutôt
    // qu'un seul champ : deux notifications rapprochées ne doivent pas se
    // couper l'une l'autre.
    private static readonly List<MediaPlayer> ActivePlayers = new();

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            ToastNotificationManagerCompat.OnActivated += _ => { };
        }
        catch
        {
            // Environnement sans shell/registre accessible (rare) : les
            // notifications resteront simplement silencieuses plutôt que de
            // planter le démarrage de l'app pour ça.
        }
    }

    public static void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .AddAudio(new Uri("ms-winsoundevent:Notification.Default"), silent: true)
                .Show();
        }
        catch
        {
            // Best-effort : une notification manquée ne doit jamais faire
            // planter le flux appelant (fin de session, mise à jour, etc.).
        }
        PlayNotificationSound();
    }

    private static void PlayNotificationSound()
    {
        try
        {
            if (!File.Exists(NotificationSoundPath)) return;
            var player = new MediaPlayer { Volume = 0.8 };
            ActivePlayers.Add(player);
            player.MediaEnded += (_, _) => { player.Close(); ActivePlayers.Remove(player); };
            player.Open(new Uri(NotificationSoundPath, UriKind.Absolute));
            player.Play();
        }
        catch
        {
            // Best-effort, comme le toast lui-même.
        }
    }
}
