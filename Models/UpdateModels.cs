namespace PokeQuad.Models;

public sealed record PublishedRelease(string TagName, bool IsDraft, bool IsPrerelease, Uri HtmlUrl);

public sealed record AvailableUpdate(SemanticVersion Version, Uri ReleasePage);

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, SemanticVersion CurrentVersion,
    AvailableUpdate? Update = null);
