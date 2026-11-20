using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace lyrics_overlay;

public sealed class AppUpdateInfo
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string AssetName { get; init; }
    public required string DownloadUrl { get; init; }
    public required string Sha256 { get; init; }
    public required Uri ReleaseUrl { get; init; }
}

public sealed class AppUpdater
{
    private const string Repository = "haloTT100/Spot-lyrics";
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private AppUpdateInfo? _availableUpdate;
    private string? _downloadedArchivePath;
    private string? _stagedDirectory;

    public AppUpdateInfo? AvailableUpdate => _availableUpdate;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "SpotLyrics-Updater/1.0 (https://github.com/haloTT100/Spot-lyrics)");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return http;
    }

    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int commitSeparator = informationalVersion.IndexOf('+');
            string version = commitSeparator >= 0
                ? informationalVersion[..commitSeparator]
                : informationalVersion;

            if (version.StartsWith('v'))
                version = version[1..];

            if (TryParseVersion(version, out _))
                return version;
        }

        Version? assemblyVersion = assembly.GetName().Version;
        return assemblyVersion == null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }

    public async Task<(AppUpdateInfo? Update, Exception? Error)> CheckAndDownloadAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            string currentVersion = GetCurrentVersion();
            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases/latest");

            if (!response.IsSuccessStatusCode)
            {
                return (null, new InvalidOperationException(
                    $"GitHub latest-release lookup failed with HTTP {(int)response.StatusCode}."));
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String)
            {
                return (null, new InvalidOperationException("The GitHub release has no tag_name."));
            }

            string tagName = tagElement.GetString() ?? "";
            string version = tagName.TrimStart('v');
            if (!TryParseVersion(version, out var availableVersion))
                return (null, new InvalidOperationException($"GitHub tag '{tagName}' is not valid SemVer."));

            if (!TryParseVersion(currentVersion, out var installedVersion) ||
                availableVersion <= installedVersion)
            {
                _availableUpdate = null;
                return (null, null);
            }

            if (!root.TryGetProperty("assets", out var assetsElement) ||
                assetsElement.ValueKind != JsonValueKind.Array)
            {
                return (null, new InvalidOperationException("The GitHub release has no assets."));
            }

            JsonElement? zipAsset = null;
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string name = nameElement.GetString() ?? "";
                if (name.Equals($"Spot-lyrics-v{version}.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipAsset = asset;
                    break;
                }
            }

            if (zipAsset is not { } assetElement ||
                !assetElement.TryGetProperty("browser_download_url", out var urlElement) ||
                urlElement.ValueKind != JsonValueKind.String)
            {
                return (null, new InvalidOperationException("A SpotLyrics ZIP asset was not found."));
            }

            string downloadUrl = urlElement.GetString() ?? "";
            string assetName = assetElement.GetProperty("name").GetString() ?? "";
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
                !IsAllowedGitHubDownloadHost(downloadUri))
            {
                return (null, new InvalidOperationException("The update asset URL is not from GitHub."));
            }

            if (!assetElement.TryGetProperty("digest", out var digestElement) ||
                digestElement.ValueKind != JsonValueKind.String)
            {
                return (null, new InvalidOperationException("The update asset has no GitHub SHA-256 digest."));
            }

            string digest = digestElement.GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                return (null, new InvalidOperationException("The update asset has an unsupported digest."));
            }

            string sha256 = digest["sha256:".Length..];
            if (sha256.Length != 64 ||
                !sha256.All(character => Uri.IsHexDigit(character)))
            {
                return (null, new InvalidOperationException("The update asset SHA-256 digest is invalid."));
            }

            string releaseUrlText = root.TryGetProperty("html_url", out var releaseUrlElement)
                ? releaseUrlElement.GetString() ?? ""
                : "";

            var update = new AppUpdateInfo
            {
                Version = version,
                TagName = tagName,
                AssetName = assetName,
                DownloadUrl = downloadUrl,
                Sha256 = sha256,
                ReleaseUrl = Uri.TryCreate(releaseUrlText, UriKind.Absolute, out var releaseUrl)
                    ? releaseUrl
                    : new Uri($"https://github.com/{Repository}/releases")
            };

            await DownloadAndStageAsync(update);
            _availableUpdate = update;
            return (update, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Update check failed: {ex.Message}");
            return (null, ex);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ApplyAndRestartAsync()
    {
        if (_availableUpdate == null)
            throw new InvalidOperationException("No update is available.");

        await _operationLock.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(_stagedDirectory) ||
                !Directory.Exists(_stagedDirectory))
            {
                await DownloadAndStageAsync(_availableUpdate);
            }

            int currentProcessId = Process.GetCurrentProcess().Id;
            string appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string executablePath = Environment.ProcessPath
                ?? Path.Combine(appDirectory, "SpotLyrics.exe");
            string updateScriptPath = Path.Combine(
                Path.GetTempPath(),
                $"SpotLyrics-update-{Guid.NewGuid():N}.ps1");

            string escapedAppDirectory = EscapeSingleQuoted(appDirectory);
            string escapedStagingDirectory = EscapeSingleQuoted(_stagedDirectory!);
            string escapedExecutablePath = EscapeSingleQuoted(executablePath);

            string script = $$"""
$ErrorActionPreference = 'Stop'
$processId = {{currentProcessId}}
$appDirectory = '{{escapedAppDirectory}}'
$stagingDirectory = '{{escapedStagingDirectory}}'
$executablePath = '{{escapedExecutablePath}}'

for ($attempt = 0; $attempt -lt 100; $attempt++) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) { break }
    Start-Sleep -Milliseconds 200
}

Copy-Item -LiteralPath (Join-Path $stagingDirectory '*') -Destination $appDirectory -Recurse -Force
Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Split-Path $stagingDirectory -Parent) -Recurse -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $executablePath
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""";

            await File.WriteAllTextAsync(updateScriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{updateScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task DownloadAndStageAsync(AppUpdateInfo update)
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "SpotLyricsUpdater",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        string archivePath = Path.Combine(tempRoot, "update.zip");
        using (var download = await Http.GetAsync(
                   update.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead))
        {
            download.EnsureSuccessStatusCode();
            await using var source = await download.Content.ReadAsStreamAsync();
            await using var destination = File.Create(archivePath);
            await source.CopyToAsync(destination);
        }

        string actualSha256;
        await using (var archive = File.OpenRead(archivePath))
        {
            byte[] hash = await SHA256.HashDataAsync(archive);
            actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(update.Sha256),
                Convert.FromHexString(actualSha256)))
        {
            throw new InvalidOperationException("The downloaded update failed SHA-256 verification.");
        }

        string stagingDirectory = Path.Combine(tempRoot, "staged");
        Directory.CreateDirectory(stagingDirectory);
        ExtractZipSafely(archivePath, stagingDirectory);

        _downloadedArchivePath = archivePath;
        _stagedDirectory = stagingDirectory;
        AppLogger.Log($"Downloaded and staged update {update.TagName} at {_stagedDirectory}");
    }

    private static void ExtractZipSafely(string archivePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string? targetPath = GetAllowedUpdateTarget(destinationDirectory, entry.FullName);
            if (targetPath == null)
                throw new InvalidOperationException(
                    $"The update archive contains an unexpected file: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var source = entry.Open();
            using var target = File.Create(targetPath);
            source.CopyTo(target);
        }
    }

    private static string? GetAllowedUpdateTarget(string destinationDirectory, string entryName)
    {
        return entryName switch
        {
            "SpotLyrics.exe" => Path.Combine(destinationDirectory, "SpotLyrics.exe"),
            "SpotLyrics.dll" => Path.Combine(destinationDirectory, "SpotLyrics.dll"),
            "SpotLyrics.runtimeconfig.json" => Path.Combine(destinationDirectory, "SpotLyrics.runtimeconfig.json"),
            "SpotLyrics.deps.json" => Path.Combine(destinationDirectory, "SpotLyrics.deps.json"),
            "SpotLyrics.pdb" => Path.Combine(destinationDirectory, "SpotLyrics.pdb"),
            "Kawazu.dll" => Path.Combine(destinationDirectory, "Kawazu.dll"),
            "LibNMeCab.dll" => Path.Combine(destinationDirectory, "LibNMeCab.dll"),
            "LibNMeCab.IpaDicBin.dll" => Path.Combine(destinationDirectory, "LibNMeCab.IpaDicBin.dll"),
            "Microsoft.Windows.SDK.NET.dll" => Path.Combine(destinationDirectory, "Microsoft.Windows.SDK.NET.dll"),
            "WinRT.Runtime.dll" => Path.Combine(destinationDirectory, "WinRT.Runtime.dll"),
            "YamlDotNet.dll" => Path.Combine(destinationDirectory, "YamlDotNet.dll"),
            "IpaDic/AUTHORS" => Path.Combine(destinationDirectory, "IpaDic", "AUTHORS"),
            "IpaDic/char.bin" => Path.Combine(destinationDirectory, "IpaDic", "char.bin"),
            "IpaDic/COPYING" => Path.Combine(destinationDirectory, "IpaDic", "COPYING"),
            "IpaDic/matrix.bin" => Path.Combine(destinationDirectory, "IpaDic", "matrix.bin"),
            "IpaDic/sys.dic" => Path.Combine(destinationDirectory, "IpaDic", "sys.dic"),
            "IpaDic/unk.dic" => Path.Combine(destinationDirectory, "IpaDic", "unk.dic"),
            _ => null
        };
    }

    private static bool IsAllowedGitHubDownloadHost(Uri downloadUri)
    {
        if (!downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               downloadUri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               downloadUri.Host.EndsWith(".release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSingleQuoted(string value) =>
        value.Replace("'", "''");

    private static bool TryParseVersion(string value, out Version version)
    {
        if (!Version.TryParse(value, out version!))
        {
            version = new Version();
            return false;
        }

        return version.Major >= 0 &&
               version.Minor >= 0 &&
               version.Build >= 0;
    }
}
