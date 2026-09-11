using System.Diagnostics;

namespace Umbra.Core;

public static class Blocker
{
    private static string ReadHosts() => File.ReadAllText(Config.HostsPath);

    // Retire TOUS les blocs Umbra, y compris un bloc tronqué dont le
    // marqueur de fin manque. C'est le cas qui comptait : si le WriteAllText
    // du fichier hosts est interrompu (process tué, disque plein, coupure),
    // le fichier garde un bloc sans marqueur de fin. L'ancienne version
    // sortait alors sans rien retirer, donc RemoveSiteBlock devenait un
    // no-op et les domaines restaient bloqués indéfiniment, sans aucune
    // session active pour l'expliquer - impossible à débloquer autrement
    // qu'en éditant le fichier hosts à la main.
    private static string StripBlock(string content)
    {
        while (true)
        {
            var startIdx = content.IndexOf(Config.MarkStart, StringComparison.Ordinal);
            if (startIdx == -1) return content;

            // Recherche à partir de startIdx : un marqueur de fin orphelin
            // situé AVANT le début produisait des indices croisés et
            // recopiait le bloc au lieu de l'enlever.
            var endIdx = content.IndexOf(Config.MarkEnd, startIdx, StringComparison.Ordinal);
            var before = content[..startIdx];
            // Le bloc est toujours ajouté en fin de fichier (voir
            // ApplySiteBlock) : sans marqueur de fin, tout ce qui suit le
            // marqueur de début nous appartient.
            var after = endIdx == -1 ? "" : content[(endIdx + Config.MarkEnd.Length)..];
            content = before.TrimEnd() + "\n" + after.TrimStart();
        }
    }

    public static void ApplySiteBlock(IEnumerable<string> sites)
    {
        var content = StripBlock(ReadHosts());
        if (!File.Exists(Config.HostsBackup))
        {
            // Sauvegarde du contenu NETTOYÉ : si un watchdog précédent a été
            // tué en laissant un bloc orphelin, sauvegarder le fichier tel
            // quel figeait ce blocage dans la copie de secours censée servir
            // à s'en sortir.
            File.WriteAllText(Config.HostsBackup, content);
        }
        var lines = new List<string> { Config.MarkStart };
        foreach (var site in sites)
        {
            var domain = (site ?? "").Trim().ToLowerInvariant();
            if (domain.Length == 0) continue;
            lines.Add($"127.0.0.1 {domain}");
            lines.Add($"127.0.0.1 www.{domain}");
        }
        lines.Add(Config.MarkEnd);
        var newContent = content.TrimEnd() + "\n\n" + string.Join("\n", lines) + "\n";
        File.WriteAllText(Config.HostsPath, newContent);
    }

    // Appelée à chaque tick où rien ne doit bloquer (pas seulement quand on
    // pense avoir soi-même posé un blocage - le watchdog s'auto-corrige à
    // chaque cycle plutôt que de se fier à un état mémoire qui ne survit pas
    // à un redémarrage du process), donc no-op explicite ici s'il n'y a déjà
    // rien à retirer pour éviter une écriture disque inutile en continu.
    public static void RemoveSiteBlock()
    {
        var original = ReadHosts();
        var content = StripBlock(original);
        if (content == original) return;
        File.WriteAllText(Config.HostsPath, content);
    }

    private static async Task RunAsync(string exe, params string[] args)
    {
        using var proc = new Process();
        proc.StartInfo.FileName = exe;
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;
        proc.Start();
        await proc.WaitForExitAsync();
    }

    // Bloque les résolveurs DNS chiffrés les plus courants au niveau du
    // pare-feu. Sans ça, un navigateur Chromium avec le DNS sécurisé activé
    // ignore complètement le fichier hosts.
    public static async Task ApplyDohBlockAsync()
    {
        var ips = string.Join(",", Config.DohBlockIps);
        try
        {
            await RunAsync("netsh", "advfirewall", "firewall", "add", "rule",
                $"name={Config.FirewallRuleName}", "dir=out", "action=block",
                $"remoteip={ips}", "enable=yes");
        }
        catch
        {
            // best-effort, comme côté JS/Python (pas de droits ou déjà là : on ignore)
        }
    }

    public static async Task RemoveDohBlockAsync()
    {
        try
        {
            await RunAsync("netsh", "advfirewall", "firewall", "delete", "rule", $"name={Config.FirewallRuleName}");
        }
        catch
        {
            // rien à supprimer ou droits insuffisants : on ignore, best-effort
        }
    }

    // Accès natif à la liste des process (Process.GetProcesses) au lieu de
    // parser la sortie CSV de tasklist.exe comme côté Electron - plus direct
    // et plus robuste (pas de dépendance à un format de sortie externe).
    public static List<string> ListRunningApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var name = proc.ProcessName + ".exe";
                if (!Config.ProtectedProcesses.Contains(name)) names.Add(name);
            }
            catch
            {
                // process disparu entre l'énumération et la lecture du nom : on ignore
            }
            finally
            {
                proc.Dispose();
            }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> EnforceAppBlock(IEnumerable<string> blockedApps)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in blockedApps)
        {
            var name = (a ?? "").Trim().ToLowerInvariant();
            if (name.Length > 0 && !Config.ProtectedProcesses.Contains(name)) targets.Add(name);
        }

        var killed = new List<string>();
        foreach (var name in targets)
        {
            var bareName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            var matches = Process.GetProcessesByName(bareName);
            if (matches.Length == 0) continue;
            var anyKilled = false;
            foreach (var proc in matches)
            {
                try
                {
                    proc.Kill();
                    anyKilled = true;
                }
                catch
                {
                    // déjà en train de se fermer, ou droits insuffisants : on ignore
                }
                finally
                {
                    proc.Dispose();
                }
            }
            if (anyKilled) killed.Add(name);
        }
        return killed;
    }
}
