namespace Umbra.Core;

// File.WriteAllText seul est un truncate-puis-write : un crash, une coupure
// disque plein, ou un kill forcé du watchdog pendant l'écriture (arrive
// réellement - voir WatchdogStopRequestFile) laisse un JSON tronqué. Chaque
// Load() de ce projet avale déjà l'exception de parsing et retombe sur un
// état par défaut vide, donc un fichier corrompu se traduit par une perte
// silencieuse et définitive (historique, blocklists, plages...) au prochain
// Save(). Écrire dans un .tmp puis renommer (déjà le pattern utilisé par
// MusicHistory.PersistCached) rend l'écriture atomique au niveau du
// système de fichiers : soit l'ancien contenu reste intact, soit le nouveau
// y est intégralement - jamais un état intermédiaire.
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        // Nom temporaire propre au process : le tableau de bord, le watchdog
        // élevé et l'hôte navigateur écrivent les MÊMES fichiers
        // (session.json, history.json, block_attempts.json...). Avec un
        // ".tmp" partagé, deux écritures simultanées se disputaient
        // littéralement le même fichier intermédiaire et l'une des deux
        // échouait sur un partage refusé. Suffixé par PID plutôt qu'aléatoire
        // pour qu'une écriture interrompue laisse au plus un orphelin par
        // process, jamais une collection qui grossit.
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, content);
        MoveWithRetry(tmp, path);
    }

    // Le renommage lui-même peut échouer un court instant : un autre process
    // remplaçant la même destination au même moment, ou un antivirus qui
    // tient le fichier ouvert le temps de l'analyser.
    private static void MoveWithRetry(string tmp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }
}
