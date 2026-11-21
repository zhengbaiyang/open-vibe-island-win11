using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Updates;

/// <summary>
/// Checks GitHub Releases for stable Open Island updates, downloads and strictly verifies
/// SHA-256 checksums, stages portable updates, and launches detached update scripts.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryOwner = "zhengbaiyang";
    public const string RepositoryName = "open-vibe-island-win11";
    public const string RepositorySlug = $"{RepositoryOwner}/{RepositoryName}";
    public const string RepositoryUrl = $"https://github.com/{RepositorySlug}";
    public const string ReleasesPageUrl = $"{RepositoryUrl}/releases";
    public const string DefaultApiBaseUrl = "https://api.github.com";
    public const string DefaultLatestReleaseEndpoint = $"{DefaultApiBaseUrl}/repos/{RepositorySlug}/releases/latest";
    public const string InnoSetupUninstallKeyName = "{7C1E9D42-5B3A-4F0E-9B6D-2F8A41C63E17}_is1";
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    private const string UninstallRegistryRelativePath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoSetupUninstallKeyName;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadChunkTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ApplyLogRetention = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions GitHubJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public string CurrentVersion { get; }
    public Version ParsedCurrentVersion { get; }
    public string AppBaseDir { get; }
    public string UpdatesRootDir { get; }
    public string LatestReleaseEndpoint { get; }
    public UpdateInstallMode InstallMode { get; }

    public UpdateService(
        HttpClient? httpClient = null,
        string? apiBaseUrl = null,
        string? appBaseDir = null,
        string? currentVersion = null,
        string? updatesRootDir = null,
        UpdateInstallMode? installModeOverride = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = CreateDefaultHttpClient();
            _ownsHttpClient = true;
        }

        AppBaseDir = NormalizeDirPath(string.IsNullOrWhiteSpace(appBaseDir) ? AppContext.BaseDirectory : appBaseDir);
        UpdatesRootDir = NormalizeDirPath(
            string.IsNullOrWhiteSpace(updatesRootDir)
                ? Path.Combine(BridgeConstants.AppDataDir, "updates")
                : updatesRootDir);

        var rawVersion = string.IsNullOrWhiteSpace(currentVersion) ? GetCurrentAppVersion() : currentVersion;
        if (TryNormalizeCurrentVersion(rawVersion, out var parsedCurrent, out var normalizedCurrent))
        {
            ParsedCurrentVersion = parsedCurrent;
            CurrentVersion = normalizedCurrent;
        }
        else
        {
            ParsedCurrentVersion = new Version(0, 0, 0);
            CurrentVersion = "0.0.0";
        }

        LatestReleaseEndpoint = ResolveLatestReleaseEndpoint(apiBaseUrl);
        InstallMode = installModeOverride ?? DetectInstallMode(AppBaseDir);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>Returns the current Open Island Major.Minor.Patch version string from assembly metadata.</summary>
    public static string GetCurrentAppVersion(Assembly? assembly = null)
    {
        var target = assembly ?? typeof(UpdateService).Assembly;

        var asmVer = target.GetName().Version;
        if (asmVer is not null && (asmVer.Major > 0 || asmVer.Minor > 0 || asmVer.Build > 0))
        {
            return $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(asmVer.Build, 0)}";
        }

        var infoVer = target.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryNormalizeCurrentVersion(infoVer, out _, out var normalized))
        {
            return normalized;
        }

        return "0.0.0";
    }

    /// <summary>
    /// Parses a strict stable SemVer tag or version (<c>Major.Minor.Patch</c>, optional leading <c>v</c>/<c>V</c>).
    /// Rejects prerelease suffixes (e.g. <c>-beta</c>, <c>-rc.1</c>) and invalid formats.
    /// </summary>
    public static bool TryParseStableSemVer(string? tagOrVersion, out Version version, out string normalized)
    {
        version = new Version(0, 0, 0);
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(tagOrVersion))
        {
            return false;
        }

        var s = tagOrVersion.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        // Reject prerelease suffixes such as -beta, -alpha.1, -rc1.
        if (s.Contains('-'))
        {
            return false;
        }

        // Reject empty build metadata; strip valid +build metadata if present.
        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
        {
            if (plusIdx == s.Length - 1)
            {
                return false;
            }
            s = s[..plusIdx];
        }

        var parts = s.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseSemVerComponent(parts[0], out var major) ||
            !TryParseSemVerComponent(parts[1], out var minor) ||
            !TryParseSemVerComponent(parts[2], out var patch))
        {
            return false;
        }

        version = new Version(major, minor, patch);
        normalized = $"{major}.{minor}.{patch}";
        return true;
    }

    /// <summary>Compares two versions strictly by Major.Minor.Patch.</summary>
    public static int CompareSemVer(Version a, Version b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var cmp = a.Major.CompareTo(b.Major);
        if (cmp != 0) return cmp;

        cmp = a.Minor.CompareTo(b.Minor);
        if (cmp != 0) return cmp;

        return Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
    }

    /// <summary>Returns true when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewerVersion(Version candidate, Version current) =>
        CompareSemVer(candidate, current) > 0;

    /// <summary>
    /// Detects whether <paramref name="baseDir"/> is a local development build, an Inno Setup installation,
    /// or a portable directory.
    /// </summary>
    public static UpdateInstallMode DetectInstallMode(string? baseDir = null)
    {
        var dir = NormalizeDirPath(string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir);

        // 1. Development: walk up to 6 parent directories looking for OpenIsland.sln + src/OpenIsland.App.
        try
        {
            DirectoryInfo? current = new DirectoryInfo(dir);
            for (int depth = 0; depth <= 6 && current is not null; depth++, current = current.Parent)
            {
                var slnPath = Path.Combine(current.FullName, "OpenIsland.sln");
                var appSrcDir = Path.Combine(current.FullName, "src", "OpenIsland.App");
                if (File.Exists(slnPath) && Directory.Exists(appSrcDir))
                {
                    return UpdateInstallMode.Development;
                }
            }
        }
        catch
        {
            // Ignore filesystem errors during ancestry walk.
        }

        // 2. Installed: unins000.exe exists in baseDir, OR HKCU/HKLM Inno Setup Uninstall InstallLocation matches baseDir.
        try
        {
            if (File.Exists(Path.Combine(dir, "unins000.exe")))
            {
                return UpdateInstallMode.Installed;
            }
        }
        catch
        {
            // Ignore filesystem errors.
        }

        if (MatchesInnoUninstallLocation(dir))
        {
            return UpdateInstallMode.Installed;
        }

        // 3. Otherwise portable.
        return UpdateInstallMode.Portable;
    }

    /// <summary>
    /// Parses standard <c>SHA256SUMS.txt</c> contents (<c>&lt;64-hex&gt;  &lt;filename&gt;</c> or GNU binary <c>*</c> prefix)
    /// into a case-insensitive map of filename to lowercase SHA-256 hex digest.
    /// </summary>
    public static Dictionary<string, string> ParseSha256Sums(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // Find first whitespace separating the 64-char hex digest from the filename.
            int wsIdx = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsWhiteSpace(trimmed[i]))
                {
                    wsIdx = i;
                    break;
                }
            }

            if (wsIdx != 64)
            {
                continue;
            }

            var hash = trimmed[..64];
            if (!IsHex64(hash))
            {
                continue;
            }

            var rawFile = trimmed[(wsIdx + 1)..].TrimStart();
            if (rawFile.StartsWith('*'))
            {
                rawFile = rawFile[1..].TrimStart();
            }

            if (rawFile.StartsWith("./", StringComparison.Ordinal) || rawFile.StartsWith(".\\", StringComparison.Ordinal))
            {
                rawFile = rawFile[2..];
            }

            if (rawFile.Length == 0)
            {
                continue;
            }

            var normalizedHash = hash.ToLowerInvariant();
            var leafName = Path.GetFileName(rawFile);
            if (!string.IsNullOrWhiteSpace(leafName))
            {
                result[leafName] = normalizedHash;
            }

            if (!string.Equals(rawFile, leafName, StringComparison.OrdinalIgnoreCase))
            {
                result[rawFile] = normalizedHash;
            }
        }

        return result;
    }

    /// <summary>Checks GitHub Releases for the latest stable version newer than <see cref="CurrentVersion"/>.</summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MetadataTimeout);

            using var request = CreateGitHubApiRequest(LatestReleaseEndpoint);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = FormatHttpError(response);
                AppLog.Warn($"update check HTTP failure: {error} ({LatestReleaseEndpoint})");
                return new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, error, InstallMode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, GitHubJsonOptions, timeoutCts.Token)
                .ConfigureAwait(false);

            if (payload is null)
            {
                const string err = "GitHub Releases returned an empty payload.";
                AppLog.Warn($"update check failed: {err}");
                return new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, err, InstallMode);
            }

            // Stable releases only: ignore drafts and prereleases.
            if (payload.Draft || payload.Prerelease)
            {
                AppLog.Info($"update check ignoring draft/prerelease tag '{payload.TagName}'");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion, null, null, InstallMode);
            }

            if (!TryParseStableSemVer(payload.TagName, out var latestVersion, out var normalizedLatest))
            {
                AppLog.Info($"update check ignoring non-stable SemVer tag '{payload.TagName}'");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion, null, null, InstallMode);
            }

            if (!IsNewerVersion(latestVersion, ParsedCurrentVersion))
            {
                AppLog.Info($"update check up to date: current={CurrentVersion}, latest={normalizedLatest}");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion, null, null, InstallMode);
            }

            var assets = payload.Assets ?? [];
            var checksumsAsset = assets.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Name) &&
                string.Equals(a.Name.Trim(), ChecksumsFileName, StringComparison.OrdinalIgnoreCase) &&
                IsAllowedDownloadUrl(a.BrowserDownloadUrl));

            var matchedAsset = SelectPrimaryAsset(assets, InstallMode, normalizedLatest);
            if (matchedAsset is null)
            {
                if (InstallMode != UpdateInstallMode.Development)
                {
                    var err = Strings.Get("UpdateAssetMissing");
                    AppLog.Warn($"update {normalizedLatest} has no matching asset for mode={InstallMode}");
                    return new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, err, InstallMode);
                }
            }

            var htmlUrl = IsAllowedDownloadUrl(payload.HtmlUrl) ? payload.HtmlUrl! : ReleasesPageUrl;
            var releaseInfo = new UpdateReleaseInfo(
                Version: normalizedLatest,
                ParsedVersion: latestVersion,
                TagName: payload.TagName ?? $"v{normalizedLatest}",
                ReleaseName: string.IsNullOrWhiteSpace(payload.Name) ? (payload.TagName ?? $"v{normalizedLatest}") : payload.Name.Trim(),
                ReleaseNotes: payload.Body ?? string.Empty,
                HtmlUrl: htmlUrl,
                PublishedAt: payload.PublishedAt,
                AssetName: matchedAsset?.Name?.Trim() ?? string.Empty,
                AssetDownloadUrl: matchedAsset?.BrowserDownloadUrl?.Trim() ?? string.Empty,
                AssetSizeBytes: matchedAsset?.Size ?? 0,
                ChecksumsAssetUrl: checksumsAsset?.BrowserDownloadUrl?.Trim(),
                InstallMode: InstallMode);

            AppLog.Info($"update available: {CurrentVersion} -> {normalizedLatest} (mode={InstallMode}, asset='{releaseInfo.AssetName}', checksums={(releaseInfo.ChecksumsAssetUrl is not null ? "yes" : "missing")})");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, CurrentVersion, releaseInfo, null, InstallMode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            const string err = "Update check timed out.";
            AppLog.Warn(err);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, err, InstallMode);
        }
        catch (Exception ex)
        {
            AppLog.Error("update check failed", ex);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, ex.Message, InstallMode);
        }
    }

    /// <summary>
    /// Downloads <c>SHA256SUMS.txt</c>, streams the release asset to a <c>.part</c> file,
    /// strictly verifies its SHA-256 hash (deleting on mismatch), renames it into place,
    /// and extracts portable ZIPs into a staging directory before shutdown.
    /// </summary>
    public async Task<PreparedUpdate> DownloadAndVerifyAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(release);

        if (string.IsNullOrWhiteSpace(release.AssetName) || string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
        {
            throw new InvalidOperationException(Strings.Get("UpdateAssetMissing"));
        }

        if (!IsAllowedDownloadUrl(release.AssetDownloadUrl))
        {
            throw new InvalidOperationException($"Refusing insecure or invalid asset download URL: {release.AssetDownloadUrl}");
        }

        // MANDATORY SHA-256: refuse immediately when SHA256SUMS.txt is absent.
        if (string.IsNullOrWhiteSpace(release.ChecksumsAssetUrl))
        {
            AppLog.Warn($"update refused: missing {ChecksumsFileName} for {release.TagName}");
            throw new InvalidOperationException(Strings.Get("UpdateChecksumMissing"));
        }

        if (!IsAllowedDownloadUrl(release.ChecksumsAssetUrl))
        {
            throw new InvalidOperationException($"Refusing insecure or invalid checksums URL: {release.ChecksumsAssetUrl}");
        }

        if (!TryParseStableSemVer(release.Version, out _, out var safeVersion))
        {
            throw new InvalidOperationException($"Invalid release version: {release.Version}");
        }

        var safeAssetName = ValidateSafeFileName(release.AssetName);

        long? initialTotal = release.AssetSizeBytes > 0 ? release.AssetSizeBytes : null;
        progress?.Report(new UpdateDownloadProgress(UpdateDownloadStage.Downloading, 0, initialTotal, 0, initialTotal.HasValue ? 0.0 : null));

        // 1. Download and parse SHA256SUMS.txt before downloading the binary payload.
        string expectedSha256 = await FetchExpectedSha256Async(release.ChecksumsAssetUrl, safeAssetName, ct).ConfigureAwait(false);

        // 2. Prepare version directory and file paths.
        var versionDir = Path.Combine(UpdatesRootDir, safeVersion);
        Directory.CreateDirectory(versionDir);

        var finalPath = Path.Combine(versionDir, safeAssetName);
        var partPath = finalPath + ".part";
        var stagedRoot = Path.Combine(versionDir, "staged");

        TryDeleteFile(partPath);
        TryDeleteFile(finalPath);

        long bytesReceived = 0;
        long? totalBytes = initialTotal;

        // 3. Stream download to .part file.
        try
        {
            using var downloadRequest = CreateDownloadRequest(release.AssetDownloadUrl);
            using var headerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerTimeoutCts.CancelAfter(DownloadChunkTimeout);

            using var response = await _httpClient.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, headerTimeoutCts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength && contentLength > 0)
            {
                totalBytes = contentLength;
            }

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            var stopwatch = Stopwatch.StartNew();
            var lastReportMs = 0L;
            var lastBytes = 0L;
            double smoothedSpeed = 0;

            progress?.Report(new UpdateDownloadProgress(UpdateDownloadStage.Downloading, 0, totalBytes, 0, totalBytes > 0 ? 0.0 : null));

            while (true)
            {
                using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeoutCts.CancelAfter(DownloadChunkTimeout);

                int read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeoutCts.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytesReceived += read;

                var elapsedMs = stopwatch.ElapsedMilliseconds;
                var deltaMs = elapsedMs - lastReportMs;
                if (deltaMs >= 100)
                {
                    var deltaBytes = bytesReceived - lastBytes;
                    var instantSpeed = deltaMs > 0 ? (deltaBytes * 1000.0) / deltaMs : 0;
                    smoothedSpeed = smoothedSpeed <= 0 ? instantSpeed : (smoothedSpeed * 0.7) + (instantSpeed * 0.3);

                    double? fraction = totalBytes > 0
                        ? Math.Clamp((double)bytesReceived / totalBytes.Value, 0.0, 1.0)
                        : null;

                    progress?.Report(new UpdateDownloadProgress(
                        UpdateDownloadStage.Downloading,
                        bytesReceived,
                        totalBytes,
                        smoothedSpeed,
                        fraction));

                    lastReportMs = elapsedMs;
                    lastBytes = bytesReceived;
                }
            }

            if (bytesReceived > lastBytes)
            {
                var finalSpeed = smoothedSpeed > 0
                    ? smoothedSpeed
                    : bytesReceived / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double? finalFraction = totalBytes > 0
                    ? Math.Clamp((double)bytesReceived / totalBytes.Value, 0.0, 1.0)
                    : 1.0;

                progress?.Report(new UpdateDownloadProgress(
                    UpdateDownloadStage.Downloading,
                    bytesReceived,
                    totalBytes ?? bytesReceived,
                    finalSpeed,
                    finalFraction));
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDeleteFile(partPath);
            throw new TimeoutException("Update download timed out while waiting for data.");
        }
        catch
        {
            TryDeleteFile(partPath);
            throw;
        }

        // 4. Strictly verify SHA-256 of the downloaded .part file before touching finalPath.
        progress?.Report(new UpdateDownloadProgress(
            UpdateDownloadStage.Verifying,
            bytesReceived,
            totalBytes ?? bytesReceived,
            0,
            1.0));

        string actualSha256;
        try
        {
            actualSha256 = await ComputeFileSha256Async(partPath, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(partPath);
            throw;
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partPath);
            TryDeleteFile(finalPath);
            AppLog.Error($"update SHA-256 mismatch for {safeAssetName}: expected={expectedSha256}, actual={actualSha256}");
            throw new InvalidOperationException(Strings.Get("UpdateChecksumMismatch"));
        }

        // 5. Promote verified .part file to final filename.
        File.Move(partPath, finalPath, overwrite: true);
        AppLog.Info($"update verified: {finalPath} (sha256={actualSha256})");

        // 6. Prepare / stage payload (Portable ZIP is extracted BEFORE app shutdown so corrupt ZIPs fail safely).
        progress?.Report(new UpdateDownloadProgress(
            UpdateDownloadStage.Preparing,
            bytesReceived,
            totalBytes ?? bytesReceived,
            0,
            1.0));

        string effectiveStagingDir = versionDir;
        if (release.InstallMode == UpdateInstallMode.Portable)
        {
            try
            {
                effectiveStagingDir = ExtractPortableZipSafely(finalPath, stagedRoot);
            }
            catch
            {
                TryDeleteDirectory(stagedRoot);
                TryDeleteFile(finalPath);
                throw;
            }
        }

        progress?.Report(new UpdateDownloadProgress(
            UpdateDownloadStage.ReadyToInstall,
            bytesReceived,
            totalBytes ?? bytesReceived,
            0,
            1.0));

        return new PreparedUpdate(
            Release: release,
            LocalFilePath: finalPath,
            ExpectedSha256: expectedSha256,
            ActualSha256: actualSha256,
            InstallMode: release.InstallMode,
            StagingDir: effectiveStagingDir);
    }

    /// <summary>
    /// Generates and writes the detached PowerShell update script (<c>apply-update.ps1</c>) with UTF-8 BOM
    /// and returns its full path. Refuses <see cref="UpdateInstallMode.Development"/>.
    /// </summary>
    public string CreateApplyScript(PreparedUpdate prepared, int currentProcessId)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (prepared.InstallMode == UpdateInstallMode.Development)
        {
            throw new InvalidOperationException(Strings.Get("DevBuildUpdateHint"));
        }

        if (string.IsNullOrWhiteSpace(prepared.LocalFilePath) || !File.Exists(prepared.LocalFilePath))
        {
            throw new FileNotFoundException("Verified update file was not found.", prepared.LocalFilePath);
        }

        Directory.CreateDirectory(UpdatesRootDir);
        var scriptPath = Path.Combine(UpdatesRootDir, "apply-update.ps1");
        var logPath = Path.Combine(UpdatesRootDir, "apply-update.log");
        var versionDir = Path.GetDirectoryName(Path.GetFullPath(prepared.LocalFilePath)) ?? UpdatesRootDir;

        string scriptContent;
        if (prepared.InstallMode == UpdateInstallMode.Installed)
        {
            var tempDir = Path.Combine(UpdatesRootDir, "tmp");
            scriptContent = BuildInstalledApplyScript(
                installerPath: Path.GetFullPath(prepared.LocalFilePath),
                appDir: AppBaseDir,
                tempDir: tempDir,
                versionDir: versionDir,
                logPath: logPath,
                currentProcessId: currentProcessId);
        }
        else
        {
            var stagedDir = prepared.StagingDir;
            if (string.IsNullOrWhiteSpace(stagedDir) ||
                !Directory.Exists(stagedDir) ||
                !File.Exists(Path.Combine(stagedDir, "OpenIsland.exe")))
            {
                var fallbackStaged = Path.Combine(versionDir, "staged");
                stagedDir = ExtractPortableZipSafely(prepared.LocalFilePath, fallbackStaged);
            }

            scriptContent = BuildPortableApplyScript(
                stagedDir: NormalizeDirPath(stagedDir),
                appDir: AppBaseDir,
                versionDir: versionDir,
                logPath: logPath,
                currentProcessId: currentProcessId);
        }

        // Write UTF-8 WITH BOM so Windows PowerShell 5.1 handles non-ASCII (e.g. CJK) paths reliably.
        File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return scriptPath;
    }

    /// <summary>
    /// Writes and launches the detached PowerShell update helper (hidden window) for either
    /// <see cref="UpdateInstallMode.Installed"/> or <see cref="UpdateInstallMode.Portable"/>.
    /// Refuses <see cref="UpdateInstallMode.Development"/>.
    /// </summary>
    public void LaunchUpdateInstaller(PreparedUpdate prepared, int currentProcessId)
    {
        var scriptPath = CreateApplyScript(prepared, currentProcessId);

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShellExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = UpdatesRootDir
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start detached PowerShell update helper.");

        AppLog.Info($"launched update helper (pid={proc.Id}, mode={prepared.InstallMode}, target={prepared.Release.Version}, script='{scriptPath}')");
    }

    /// <summary>
    /// Safely removes stale update artifacts under <see cref="UpdatesRootDir"/> left behind by previous runs.
    /// Never throws; errors on individual files/directories are ignored.
    /// </summary>
    public void CleanupOldUpdateArtifacts()
    {
        try
        {
            if (!Directory.Exists(UpdatesRootDir))
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;

            foreach (var dir in Directory.GetDirectories(UpdatesRootDir))
            {
                TryDeleteDirectory(dir);
            }

            foreach (var file in Directory.GetFiles(UpdatesRootDir))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "apply-update.log", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var age = nowUtc - File.GetLastWriteTimeUtc(file);
                        if (age < ApplyLogRetention)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                TryDeleteFile(file);
            }
        }
        catch
        {
            // Ignore cleanup errors on startup.
        }
    }

    private async Task<string> FetchExpectedSha256Async(string checksumsUrl, string expectedAssetName, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(MetadataTimeout);

        string text;
        try
        {
            using var request = CreateDownloadRequest(checksumsUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out while downloading {ChecksumsFileName}.");
        }

        var sums = ParseSha256Sums(text);

        if (!sums.TryGetValue(expectedAssetName, out var expectedHash) || !IsHex64(expectedHash))
        {
            AppLog.Warn($"update refused: {ChecksumsFileName} has no valid entry for '{expectedAssetName}'");
            throw new InvalidOperationException(Strings.Get("UpdateChecksumMissing"));
        }

        return expectedHash;
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string ExtractPortableZipSafely(string zipPath, string stagingDir)
    {
        TryDeleteDirectory(stagingDir);
        Directory.CreateDirectory(stagingDir);

        var fullDestRoot = Path.GetFullPath(stagingDir);
        if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDestRoot += Path.DirectorySeparatorChar;
        }

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            if (archive.Entries.Count == 0)
            {
                throw new InvalidDataException("Portable update ZIP archive is empty.");
            }

            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.FullName));
                if (!destPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Portable update archive entry escapes staging directory: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.EndsWith('/') ||
                    entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        // Validate that OpenIsland.exe is present (either directly at root or in a single wrapper subdirectory).
        if (File.Exists(Path.Combine(stagingDir, "OpenIsland.exe")))
        {
            return NormalizeDirPath(stagingDir);
        }

        var topFiles = Directory.GetFiles(stagingDir);
        var topDirs = Directory.GetDirectories(stagingDir);
        if (topFiles.Length == 0 && topDirs.Length == 1 && File.Exists(Path.Combine(topDirs[0], "OpenIsland.exe")))
        {
            return NormalizeDirPath(topDirs[0]);
        }

        throw new InvalidDataException("Portable update archive does not contain OpenIsland.exe.");
    }

    private static string BuildInstalledApplyScript(
        string installerPath,
        string appDir,
        string tempDir,
        string versionDir,
        string logPath,
        int currentProcessId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$targetPid = {currentProcessId}");
        sb.AppendLine($"$installerPath = {PsSingleQuote(installerPath)}");
        sb.AppendLine($"$appDir = {PsSingleQuote(appDir)}");
        sb.AppendLine($"$tempDir = {PsSingleQuote(tempDir)}");
        sb.AppendLine($"$versionDir = {PsSingleQuote(versionDir)}");
        sb.AppendLine($"$logPath = {PsSingleQuote(logPath)}");
        sb.AppendLine();
        sb.AppendLine("function Write-UpdateLog([string]$Message) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')");
        sb.AppendLine("        Add-Content -LiteralPath $logPath -Value \"$ts [INSTALL] $Message\" -Encoding UTF8");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("try {");
        sb.AppendLine("    Write-UpdateLog \"Waiting for PID $targetPid to exit...\"");
        sb.AppendLine("    if ($targetPid -gt 0) {");
        sb.AppendLine("        $deadline = (Get-Date).AddSeconds(30)");
        sb.AppendLine("        while ((Get-Date) -lt $deadline) {");
        sb.AppendLine("            if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }");
        sb.AppendLine("            Start-Sleep -Milliseconds 250");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    Start-Sleep -Milliseconds 300");
        sb.AppendLine();
        sb.AppendLine("    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null");
        sb.AppendLine("    $origTmp = $env:TMP");
        sb.AppendLine("    $origTemp = $env:TEMP");
        sb.AppendLine("    $exitCode = -1");
        sb.AppendLine("    try {");
        sb.AppendLine("        $env:TMP = $tempDir");
        sb.AppendLine("        $env:TEMP = $tempDir");
        sb.AppendLine();
        sb.AppendLine("        $installerArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', \"/DIR=\"\"$appDir\"\"\")");
        sb.AppendLine("        Write-UpdateLog \"Running installer: $installerPath\"");
        sb.AppendLine("        $proc = Start-Process -FilePath $installerPath -ArgumentList $installerArgs -Wait -PassThru");
        sb.AppendLine("        $exitCode = if ($proc) { $proc.ExitCode } else { -1 }");
        sb.AppendLine("        Write-UpdateLog \"Installer exited with code $exitCode\"");
        sb.AppendLine("    } finally {");
        sb.AppendLine("        $env:TMP = $origTmp");
        sb.AppendLine("        $env:TEMP = $origTemp");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    if ($exitCode -eq 0) {");
        sb.AppendLine("        Remove-Item -LiteralPath $versionDir -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("        $exePath = Join-Path $appDir 'OpenIsland.exe'");
        sb.AppendLine("        if (Test-Path -LiteralPath $exePath) {");
        sb.AppendLine("            Write-UpdateLog \"Relaunching $exePath\"");
        sb.AppendLine("            Start-Process -FilePath $exePath -WorkingDirectory $appDir");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("} catch {");
        sb.AppendLine("    Write-UpdateLog \"ERROR: $($_.Exception.Message)\"");
        sb.AppendLine("} finally {");
        sb.AppendLine("    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($PSCommandPath) {");
        sb.AppendLine("        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildPortableApplyScript(
        string stagedDir,
        string appDir,
        string versionDir,
        string logPath,
        int currentProcessId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$targetPid = {currentProcessId}");
        sb.AppendLine($"$stagedDir = {PsSingleQuote(stagedDir)}");
        sb.AppendLine($"$appDir = {PsSingleQuote(appDir)}");
        sb.AppendLine($"$versionDir = {PsSingleQuote(versionDir)}");
        sb.AppendLine($"$logPath = {PsSingleQuote(logPath)}");
        sb.AppendLine();
        sb.AppendLine("function Write-UpdateLog([string]$Message) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')");
        sb.AppendLine("        Add-Content -LiteralPath $logPath -Value \"$ts [PORTABLE] $Message\" -Encoding UTF8");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("try {");
        sb.AppendLine("    Write-UpdateLog \"Waiting for PID $targetPid to exit...\"");
        sb.AppendLine("    if ($targetPid -gt 0) {");
        sb.AppendLine("        $deadline = (Get-Date).AddSeconds(30)");
        sb.AppendLine("        while ((Get-Date) -lt $deadline) {");
        sb.AppendLine("            if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }");
        sb.AppendLine("            Start-Sleep -Milliseconds 250");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    Start-Sleep -Milliseconds 400");
        sb.AppendLine();
        sb.AppendLine("    Write-UpdateLog \"Copying staged files from $stagedDir to $appDir\"");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $appDir)) {");
        sb.AppendLine("        New-Item -ItemType Directory -Force -Path $appDir | Out-Null");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    $dirs = Get-ChildItem -LiteralPath $stagedDir -Directory -Recurse -ErrorAction Stop");
        sb.AppendLine("    foreach ($d in $dirs) {");
        sb.AppendLine("        $rel = $d.FullName.Substring($stagedDir.Length).TrimStart('\\', '/')");
        sb.AppendLine("        if ($rel) {");
        sb.AppendLine("            $destDir = Join-Path $appDir $rel");
        sb.AppendLine("            if (-not (Test-Path -LiteralPath $destDir)) {");
        sb.AppendLine("                New-Item -ItemType Directory -Force -Path $destDir | Out-Null");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    $files = Get-ChildItem -LiteralPath $stagedDir -File -Recurse -ErrorAction Stop");
        sb.AppendLine("    foreach ($f in $files) {");
        sb.AppendLine("        $rel = $f.FullName.Substring($stagedDir.Length).TrimStart('\\', '/')");
        sb.AppendLine("        if (-not $rel) { continue }");
        sb.AppendLine("        if ($f.Extension -ieq '.log') { continue }");
        sb.AppendLine("        $destFile = Join-Path $appDir $rel");
        sb.AppendLine("        $destParent = Split-Path -Parent $destFile");
        sb.AppendLine("        if ($destParent -and -not (Test-Path -LiteralPath $destParent)) {");
        sb.AppendLine("            New-Item -ItemType Directory -Force -Path $destParent | Out-Null");
        sb.AppendLine("        }");
        sb.AppendLine("        for ($attempt = 1; $attempt -le 12; $attempt++) {");
        sb.AppendLine("            try {");
        sb.AppendLine("                Copy-Item -LiteralPath $f.FullName -Destination $destFile -Force -ErrorAction Stop");
        sb.AppendLine("                break");
        sb.AppendLine("            } catch {");
        sb.AppendLine("                if ($attempt -eq 12) { throw }");
        sb.AppendLine("                Start-Sleep -Milliseconds 500");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    Remove-Item -LiteralPath $versionDir -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    $exePath = Join-Path $appDir 'OpenIsland.exe'");
        sb.AppendLine("    if (Test-Path -LiteralPath $exePath) {");
        sb.AppendLine("        Write-UpdateLog \"Relaunching $exePath\"");
        sb.AppendLine("        Start-Process -FilePath $exePath -WorkingDirectory $appDir");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-UpdateLog 'Portable update complete.'");
        sb.AppendLine("} catch {");
        sb.AppendLine("    Write-UpdateLog \"ERROR: $($_.Exception.Message)\"");
        sb.AppendLine("} finally {");
        sb.AppendLine("    if ($PSCommandPath) {");
        sb.AppendLine("        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string PsSingleQuote(string value)
    {
        // Escape standard single quote and Unicode smart-quotes recognized by Windows PowerShell 5.1.
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\'':
                    sb.Append("''");
                    break;
                case '\u2018':
                case '\u2019':
                case '\u201A':
                case '\u201B':
                    sb.Append($"' + [char]0x{(int)ch:X4} + '");
                    break;
                case '\r':
                case '\n':
                    throw new ArgumentException("Update paths cannot contain newline characters.", nameof(value));
                default:
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    private static GitHubAssetDto? SelectPrimaryAsset(
        IReadOnlyList<GitHubAssetDto> assets,
        UpdateInstallMode mode,
        string normalizedVersion)
    {
        var candidates = new List<GitHubAssetDto>();
        foreach (var asset in assets)
        {
            var name = asset.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !IsAllowedDownloadUrl(asset.BrowserDownloadUrl))
            {
                continue;
            }

            bool matches = mode == UpdateInstallMode.Portable
                ? name.StartsWith("OpenIsland-", StringComparison.OrdinalIgnoreCase) &&
                  name.EndsWith("-portable-win-x64.zip", StringComparison.OrdinalIgnoreCase)
                : name.StartsWith("OpenIsland-Setup-", StringComparison.OrdinalIgnoreCase) &&
                  name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                candidates.Add(asset);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Prefer exact version match, then non-TEST builds, then first candidate.
        var exact = candidates.FirstOrDefault(a =>
            a.Name!.Contains(normalizedVersion, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var nonTest = candidates.FirstOrDefault(a =>
            !a.Name!.Contains("-TEST-", StringComparison.OrdinalIgnoreCase));
        return nonTest ?? candidates[0];
    }

    private HttpRequestMessage CreateGitHubApiRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"OpenIsland/{CurrentVersion}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private HttpRequestMessage CreateDownloadRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"OpenIsland/{CurrentVersion}");
        return req;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static string ResolveLatestReleaseEndpoint(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            return DefaultLatestReleaseEndpoint;
        }

        var trimmed = apiBaseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/repos/{RepositorySlug}/releases/latest";
    }

    private static bool TryNormalizeCurrentVersion(string? raw, out Version version, out string normalized)
    {
        version = new Version(0, 0, 0);
        normalized = "0.0.0";

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        // Strip metadata/suffix (e.g. "0.1.4-win11" or "0.1.4+abc") for current assembly version.
        var suffixIdx = s.IndexOfAny(['-', '+']);
        if (suffixIdx >= 0)
        {
            s = s[..suffixIdx];
        }

        var parts = s.Split('.');
        if (parts.Length < 3 || parts.Length > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new Version(major, minor, patch);
        normalized = $"{major}.{minor}.{patch}";
        return true;
    }

    private static bool TryParseSemVerComponent(string part, out int value)
    {
        value = 0;
        if (part.Length == 0 || (part.Length > 1 && part[0] == '0'))
        {
            return false;
        }

        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static bool IsHex64(string text)
    {
        if (text.Length != 64)
        {
            return false;
        }

        for (int i = 0; i < 64; i++)
        {
            char c = text[i];
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow plaintext HTTP only for local loopback mock servers used in automated tests.
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
    }

    private static string ValidateSafeFileName(string fileName)
    {
        var trimmed = fileName.Trim();
        var leaf = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(leaf) ||
            !string.Equals(trimmed, leaf, StringComparison.Ordinal) ||
            leaf.Contains("..", StringComparison.Ordinal) ||
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Unsafe update asset file name: '{fileName}'.");
        }

        return leaf;
    }

    private static bool MatchesInnoUninstallLocation(string normalizedBaseDir)
    {
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var subKey = baseKey.OpenSubKey(UninstallRegistryRelativePath, writable: false);
                    var installLocation = subKey?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(installLocation))
                    {
                        continue;
                    }

                    var normalizedInstall = NormalizeDirPath(installLocation);
                    if (string.Equals(normalizedInstall, normalizedBaseDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore registry access errors.
                }
            }
        }

        return false;
    }

    private static string FormatHttpError(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remValues) &&
            remValues.Any(v => v.Trim() == "0"))
        {
            return "GitHub API rate limit exceeded (HTTP 403). Please try again later.";
        }

        return string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? $"HTTP {code}"
            : $"HTTP {code} ({response.ReasonPhrase})";
    }

    private static string ResolvePowerShellExecutable()
    {
        var systemPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(systemPs) ? systemPs : "powershell.exe";
    }

    private static string NormalizeDirPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var full = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && full.Length <= root.Length)
        {
            return full;
        }
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
