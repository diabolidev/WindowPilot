using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowPilot.App.Services;

public sealed record GitHubUpdateInfo(
    Version Version, string Tag, string DownloadUrl, string Sha256, string ReleaseUrl);

public sealed class GitHubUpdateService
{
    private const string InstallerName = "WindowPilot-Setup-win-x64.exe";
    private const long MaxInstallerSize = 350L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WindowPilot-Updater/{CurrentVersion.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(GitHubUpdateService).Assembly.GetName().Version;
            return version ?? new Version(0, 1, 0);
        }
    }

    private static string ReadRepository()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "github-repository.txt");
        if (!File.Exists(file))
            throw new InvalidOperationException("GitHub updates are not configured. See README.md in the project repository.");
        string id = File.ReadAllText(file).Trim();
        string[] parts = id.Split('/');
        if (parts.Length != 2 || parts.Any(part =>
            part.Length == 0 || part.Equals("YOUR_GITHUB_NAME", StringComparison.OrdinalIgnoreCase) ||
            part.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.'))))
            throw new InvalidOperationException("Configure your public GitHub repository in distribution/github-repository.txt as OWNER/REPO, then rebuild the release.");
        return id;
    }

    public async Task<GitHubUpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        string repo = ReadRepository();
        using HttpResponseMessage response = await Client.GetAsync(
            $"https://api.github.com/repos/{repo}/releases/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("No published GitHub release was found. Publish a release with the installer asset first.");
        response.EnsureSuccessStatusCode();
        await using Stream json = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        JsonElement release = document.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest))
            throw new InvalidDataException($"Invalid GitHub release version: {tag}");
        if (latest <= CurrentVersion) return null;

        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), InstallerName,
                StringComparison.OrdinalIgnoreCase)) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            string digest = asset.TryGetProperty("digest", out var element) ? element.GetString() ?? "" : "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update download URL.");
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("The release installer has no valid GitHub SHA-256 digest, so it will not be executed.");
            if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.GetInt64() > MaxInstallerSize)
                throw new InvalidDataException("The update exceeds the allowed size.");
            string releaseUrl = release.GetProperty("html_url").GetString() ?? "";
            return new GitHubUpdateInfo(latest, tag, url, digest[7..].ToLowerInvariant(), releaseUrl);
        }
        throw new InvalidDataException("The latest release does not contain WindowPilot-Setup-win-x64.exe.");
    }

    public async Task<string> DownloadVerifiedInstallerAsync(
        GitHubUpdateInfo update, CancellationToken cancellationToken = default)
    {
        // Each attempt gets a fresh directory, so a previous installer process
        // cannot lock the destination of a new download.
        string folder = Path.Combine(Path.GetTempPath(), "WindowPilot", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, InstallerName);
        string temporaryPath = path + ".download";
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxInstallerSize)
                throw new InvalidDataException("The update is too large.");

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream dest = new(temporaryPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long bytesRead = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    bytesRead += count;
                    if (bytesRead > MaxInstallerSize)
                        throw new InvalidDataException("The update exceeds the allowed size.");
                    await dest.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }

            string calculated;
            // On Windows, File.OpenRead denies FILE_SHARE_DELETE. Keep this
            // stream scoped and disposed before calling File.Move.
            await using (FileStream verify = new(temporaryPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true))
            {
                calculated = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            }
            if (!calculated.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer checksum does not match GitHub's SHA-256 digest. The update was cancelled.");
            File.Move(temporaryPath, path);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void LaunchInstaller(string verifiedInstallerPath)
    {
        // Explicit user consent happens in MainWindow before calling this.
        using Process? installerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = verifiedInstallerPath,
            Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });

        if (installerProcess is null)
            throw new InvalidOperationException("The installer could not be started.");
    }
}
