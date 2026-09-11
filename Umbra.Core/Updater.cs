using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Umbra.Core;

public sealed class UpdateCheckResult
{
    public bool CheckSucceeded { get; init; }
    public bool Available { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? Notes { get; init; }
    public string? InstallerName { get; init; }
    public string? InstallerUrl { get; init; }
    public string? ChecksumsUrl { get; init; }
    public long InstallerSize { get; init; }

    public bool CanInstall => Available
        && !string.IsNullOrWhiteSpace(InstallerName)
        && !string.IsNullOrWhiteSpace(InstallerUrl)
        && !string.IsNullOrWhiteSpace(ChecksumsUrl);
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public static class Updater
{
    private const string Repo = "zixload/umbra";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";
    private static readonly string ApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly Regex VersionPattern = new(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);
    // 10 minutes, c'est le budget d'un téléchargement de 150 Mo sur une
    // mauvaise connexion. Pour la simple interrogation de l'API GitHub c'est
    // absurde : une connexion qui pend bloquait la vérification dix minutes,
    // et comme _updateGate reste pris pendant ce temps (App.xaml.cs), le
    // bouton "Vérifier les mises à jour" ne faisait alors strictement rien,
    // sans le moindre message.
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static Updater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Umbra-App-Updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static int CompareVersions(string a, string b)
    {
        var pa = ParseVersionParts(a);
        var pb = ParseVersionParts(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var na = i < pa.Length ? pa[i] : 0;
            var nb = i < pb.Length ? pb[i] : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CheckTimeout);
        var token = budget.Token;
        try
        {
            using var response = await Http.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult { CurrentVersion = currentVersion };

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: token);
            if (release?.TagName is null)
                return new UpdateCheckResult { CurrentVersion = currentVersion };

            var latestVersion = NormalizeVersion(release.TagName);
            var available = CompareVersions(latestVersion, currentVersion) > 0;
            var expectedInstallerName = $"Umbra-Setup-{latestVersion}-x64.exe";
            var installer = release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, expectedInstallerName, StringComparison.OrdinalIgnoreCase));
            var checksums = release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));

            return new UpdateCheckResult
            {
                CheckSucceeded = true,
                Available = available,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                ReleaseUrl = release.HtmlUrl,
                Notes = release.Body ?? "",
                InstallerName = installer?.Name,
                InstallerUrl = installer?.DownloadUrl,
                ChecksumsUrl = checksums?.DownloadUrl,
                InstallerSize = installer?.Size ?? 0,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult { CurrentVersion = currentVersion };
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult { CurrentVersion = currentVersion };
        }
        catch (JsonException)
        {
            return new UpdateCheckResult { CurrentVersion = currentVersion };
        }
    }

    public static async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.CanInstall || update.InstallerName is null || update.InstallerUrl is null || update.ChecksumsUrl is null)
            throw new InvalidOperationException("The release does not contain a verifiable Umbra installer.");
        if (!IsTrustedGitHubUrl(update.InstallerUrl) || !IsTrustedGitHubUrl(update.ChecksumsUrl))
            throw new InvalidOperationException("The release contains an untrusted download URL.");

        var checksumText = await Http.GetStringAsync(update.ChecksumsUrl, cancellationToken);
        if (!TryReadExpectedSha256(checksumText, update.InstallerName, out var expectedHash))
            throw new InvalidDataException("The installer checksum is missing from SHA256SUMS.txt.");

        var updatesDirectory = UpdatesDirectory;
        Directory.CreateDirectory(updatesDirectory);
        CleanupDownloadedInstallers(update.InstallerName);
        var destinationPath = Path.Combine(updatesDirectory, update.InstallerName);
        var temporaryPath = destinationPath + ".download";

        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            using var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? update.InstallerSize;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var actualHash = await WriteVerifiedDownloadAsync(
                source,
                temporaryPath,
                totalBytes,
                progress,
                cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(expectedHash)))
                throw new CryptographicException("The downloaded installer failed SHA-256 verification.");

            // The helper has disposed its FileShare.None destination before
            // this rename. Keeping that stream alive makes Move fail on
            // Windows with a sharing violation.
            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(1);
            return destinationPath;
        }
        catch
        {
            // Le nettoyage ne doit pas remplacer l'erreur d'origine : si le
            // fichier partiel est encore verrouillé, File.Delete lèverait une
            // IOException qui masquerait la vraie cause de l'échec.
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public static string UpdatesDirectory => Path.Combine(Config.DataDir, "updates");

    // Chaque mise à jour laissait son installateur (~156 Mo) dans
    // %AppData%\UmbraNative\data\updates, et rien ne l'a jamais supprimé :
    // après une quinzaine de versions, plusieurs gigaoctets de fichiers morts
    // s'accumulaient en silence chez l'utilisateur. On ne garde au plus que
    // l'installateur en cours de téléchargement.
    public static void CleanupDownloadedInstallers(string? keepFileName = null)
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(UpdatesDirectory).ToList())
            {
                if (keepFileName is not null
                    && string.Equals(Path.GetFileName(path), keepFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Un installateur encore en cours d'exécution reste verrouillé :
                // il partira au prochain passage.
                try { File.Delete(path); } catch { }
            }
        }
        catch
        {
            // le ménage ne doit jamais faire échouer une mise à jour
        }
    }

    internal static async Task<string> WriteVerifiedDownloadAsync(
        Stream source,
        string temporaryPath,
        long totalBytes,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1024 * 128];
        long downloadedBytes = 0;
        var lastReportedPercent = -1;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            sha256.AppendData(buffer, 0, bytesRead);
            downloadedBytes += bytesRead;

            if (totalBytes > 0)
            {
                var percent = (int)Math.Clamp(downloadedBytes * 100 / totalBytes, 0, 100);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(percent / 100d);
                }
            }
        }

        await destination.FlushAsync(cancellationToken);
        return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
    }

    internal static bool TryReadExpectedSha256(string contents, string fileName, out string expectedHash)
    {
        expectedHash = "";
        foreach (var line in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, @"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$");
            if (!match.Success || !string.Equals(match.Groups["name"].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                continue;

            expectedHash = match.Groups["hash"].Value.ToLowerInvariant();
            return true;
        }
        return false;
    }

    internal static bool IsTrustedGitHubUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string value) =>
        VersionPattern.Match(value).Value is { Length: > 0 } version ? version : "0.0.0";

    private static int[] ParseVersionParts(string value)
    {
        var normalized = NormalizeVersion(value);
        return normalized.Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();
    }
}
