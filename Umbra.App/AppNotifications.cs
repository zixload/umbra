using System;
using System.IO;
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

    // Un URI ms-appx:/// (le chemin habituel pour un son de toast custom) ne
    // marche que pour une app empaquetée (MSIX) - Umbra ne l'est pas, donc
    // il faut un file:/// vers le chemin absolu du fichier une fois installé
    // (AppContext.BaseDirectory, comme les autres assets embarqués).
    private static readonly Uri NotificationSound = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "notification.mp3"));

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
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);
            // Un seul son pour toutes les notifications (fin de session, fin
            // de pause, échec de mise à jour...) puisqu'elles passent toutes
            // par cette méthode - si le fichier a disparu, AddAudio met
            // simplement le son par défaut de Windows, jamais d'exception.
            if (File.Exists(NotificationSound.LocalPath)) builder.AddAudio(NotificationSound);
            builder.Show();
        }
        catch
        {
            // Best-effort : une notification manquée ne doit jamais faire
            // planter le flux appelant (fin de session, mise à jour, etc.).
        }
    }
}
