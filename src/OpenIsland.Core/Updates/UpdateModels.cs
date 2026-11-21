namespace OpenIsland.Core.Updates;

/// <summary>How the currently running Open Island copy was installed.</summary>
public enum UpdateInstallMode
{
    Installed,
    Portable,
    Development
}

/// <summary>Outcome of checking GitHub Releases for a newer stable version.</summary>
public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    Failed
}

/// <summary>Pipeline stage reported during download, verification, and staging.</summary>
public enum UpdateDownloadStage
{
    Downloading,
    Verifying,
    Preparing,
    ReadyToInstall
}

/// <summary>Metadata about an available stable GitHub Release and its matched asset.</summary>
public sealed record UpdateReleaseInfo(
    string Version,
    System.Version ParsedVersion,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    string AssetName,
    string AssetDownloadUrl,
    long AssetSizeBytes,
    string? ChecksumsAssetUrl,
    UpdateInstallMode InstallMode);

/// <summary>Result of an update check against GitHub Releases.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    UpdateReleaseInfo? Release,
    string? ErrorMessage,
    UpdateInstallMode InstallMode)
{
    public bool IsUpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable && Release is not null;
}

/// <summary>Progress snapshot emitted during update download, SHA-256 verification, and staging.</summary>
public sealed record UpdateDownloadProgress(
    UpdateDownloadStage Stage,
    long BytesReceived,
    long? TotalBytes,
    double SpeedBytesPerSecond,
    double? Fraction);

/// <summary>A downloaded, SHA-256-verified update ready to be applied by the detached updater script.</summary>
public sealed record PreparedUpdate(
    UpdateReleaseInfo Release,
    string LocalFilePath,
    string ExpectedSha256,
    string ActualSha256,
    UpdateInstallMode InstallMode,
    string StagingDir);
