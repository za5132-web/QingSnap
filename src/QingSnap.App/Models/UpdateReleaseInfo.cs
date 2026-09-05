namespace QingSnap.App.Models;

public sealed record UpdateReleaseInfo(
    string TagName,
    Version Version,
    DateTimeOffset PublishedAt,
    string ReleaseNotes,
    string PackageName,
    Uri DownloadUri,
    long PackageSize,
    string? ExpectedSha256,
    Uri ReleasePageUri)
{
    public bool CanDownload =>
        ExpectedSha256 is { Length: 64 } &&
        PackageName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateReleaseInfo? Release = null,
    string? Message = null);

public enum UpdateCheckStatus
{
    Skipped,
    UpToDate,
    UpdateAvailable,
    NoCompatiblePackage,
    Error
}

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100D / TotalBytes, 0, 100);
}

public sealed record UpdateDownloadResult(string FilePath, string Sha256);
