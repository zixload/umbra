using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Umbra.Core;

namespace Umbra.App;

internal static class BrowserIntegration
{
    public const string HostName = "com.umbra.browser_blocker";
    public const string StoreExtensionId = "kihnnccjkhgjagaoljepcpghmfdpicoc";
    public const string ManualExtensionId = "ijgalicomdmmcjecigefpchbdeiadnld";
    public const string StoreUrl = "https://chromewebstore.google.com/detail/" + StoreExtensionId;

    private static readonly string[] VendorKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Vivaldi\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    ];

    public static bool RegisterNativeHost()
    {
        BrowserHostLifecycle.ClearStopRequest();
        var hostExe = FindHostExecutable();
        if (hostExe is null) return false;

        try
        {
            var manifestPath = Path.Combine(Config.DataDir, "browser-native-host.json");
            var manifest = new
            {
                name = HostName,
                description = "Umbra browser blocking host",
                path = hostExe,
                type = "stdio",
                allowed_origins = new[]
                {
                    $"chrome-extension://{StoreExtensionId}/",
                    $"chrome-extension://{ManualExtensionId}/",
                },
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Json.Options));

            foreach (var vendorKey in VendorKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{vendorKey}\{HostName}");
                key?.SetValue(null, manifestPath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ShellExecute échoue s'il n'y a aucun navigateur par défaut enregistré,
    // ou si l'association http est cassée : sans ce garde, un simple clic sur
    // "Installer l'extension" levait une exception non gérée.
    public static bool OpenStorePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = StoreUrl, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> StopNativeHostsForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!BrowserHostLifecycle.RequestStop()) return false;
        try
        {
            var gracefulDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (DateTime.UtcNow < gracefulDeadline)
            {
                using var hosts = FindOwnedHostProcesses();
                if (hosts.Count == 0) return true;
                await Task.Delay(200, cancellationToken);
            }

            // Older hosts (notably the one shipped with 1.0.3) do not understand
            // the cooperative stop marker. Force only processes whose executable
            // path matches this Umbra installation, then let the installer proceed.
            using (var hosts = FindOwnedHostProcesses())
            {
                foreach (var host in hosts)
                {
                    try
                    {
                        if (!host.HasExited) host.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                }
            }

            var forcedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < forcedDeadline)
            {
                using var hosts = FindOwnedHostProcesses();
                if (hosts.Count == 0) return true;
                await Task.Delay(150, cancellationToken);
            }

            BrowserHostLifecycle.ClearStopRequest();
            return false;
        }
        catch
        {
            BrowserHostLifecycle.ClearStopRequest();
            throw;
        }
    }

    public static void ResumeNativeHostsAfterFailedUpdate()
    {
        BrowserHostLifecycle.ClearStopRequest();
        RegisterNativeHost();
    }

    private static string? FindHostExecutable()
    {
        return HostExecutableCandidates().FirstOrDefault(File.Exists);
    }

    private static string[] HostExecutableCandidates() =>
    [
        Path.Combine(AppContext.BaseDirectory, "browser-host", "Umbra.BrowserHost.exe"),
        Path.Combine(AppContext.BaseDirectory, "Umbra.BrowserHost.exe"),
    ];

    private static ProcessCollection FindOwnedHostProcesses()
    {
        var expectedPaths = HostExecutableCandidates()
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owned = new List<Process>();
        foreach (var process in Process.GetProcessesByName("Umbra.BrowserHost"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && expectedPaths.Contains(Path.GetFullPath(path)))
                    owned.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                process.Dispose();
            }
        }
        return new ProcessCollection(owned);
    }

    private sealed class ProcessCollection(List<Process> processes) : IDisposable
    {
        public int Count => processes.Count;
        public IEnumerator<Process> GetEnumerator() => processes.GetEnumerator();
        public void Dispose()
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
