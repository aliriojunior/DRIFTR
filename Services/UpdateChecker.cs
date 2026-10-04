using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text.Json;
using PokeQuad.Config;
using PokeQuad.Models;

namespace PokeQuad.Services;

public interface IReleaseMetadataProvider
{
    Task<IReadOnlyList<PublishedRelease>> GetReleasesAsync(CancellationToken cancellationToken);
}

public sealed class GitHubReleaseMetadataProvider : IReleaseMetadataProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;

    public GitHubReleaseMetadataProvider(HttpClient? client = null, TimeSpan? timeout = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient();
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<IReadOnlyList<PublishedRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, AppConfig.UpdateReleasesApiUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"DRIFTR/{UpdateChecker.CurrentVersionText}");
        request.Headers.Add("X-GitHub-Api-Version", AppConfig.GitHubApiVersion);
        using HttpResponseMessage response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a releases array.");
        var releases = new List<PublishedRelease>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("html_url", out JsonElement url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? releaseUri)) continue;
            bool draft = item.TryGetProperty("draft", out JsonElement draftValue) && draftValue.ValueKind == JsonValueKind.True;
            bool prerelease = item.TryGetProperty("prerelease", out JsonElement prereleaseValue) && prereleaseValue.ValueKind == JsonValueKind.True;
            releases.Add(new PublishedRelease(tag.GetString()!, draft, prerelease, releaseUri));
        }
        return releases;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

public sealed class UpdateChecker(IReleaseMetadataProvider provider)
{
    public static string CurrentVersionText
    {
        get
        {
            string value = typeof(UpdateChecker).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int metadata = value.IndexOf('+');
            return metadata >= 0 ? value[..metadata] : value;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!SemanticVersion.TryParse(CurrentVersionText, out SemanticVersion? current))
            return new UpdateCheckResult(UpdateCheckStatus.Failed, ParseFallback());
        try
        {
            IReadOnlyList<PublishedRelease> releases = await provider.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            AvailableUpdate? update = SelectUpdate(current!, releases);
            return new UpdateCheckResult(update is null ? UpdateCheckStatus.UpToDate : UpdateCheckStatus.UpdateAvailable,
                current!, update);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current!);
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current!);
        }
        catch (JsonException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current!);
        }
        catch (IOException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current!);
        }
    }

    public static AvailableUpdate? SelectUpdate(SemanticVersion current, IEnumerable<PublishedRelease> releases) => releases
        .Where(release => !release.IsDraft)
        .Select(release => (Release: release, Parsed: SemanticVersion.TryParse(release.TagName, out SemanticVersion? parsed) ? parsed : null))
        .Where(item => item.Parsed is not null && item.Parsed.CompareTo(current) > 0)
        .Where(item => current.IsPrerelease || (!item.Release.IsPrerelease && !item.Parsed!.IsPrerelease))
        .Where(item => IsOfficialReleaseUrl(item.Release.HtmlUrl))
        .OrderByDescending(item => item.Parsed)
        .Select(item => new AvailableUpdate(item.Parsed!, item.Release.HtmlUrl))
        .FirstOrDefault();

    public static bool IsOfficialReleaseUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        string prefix = $"/{AppConfig.UpdateOwner}/{AppConfig.UpdateRepository}/releases/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldCheckAutomatically(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc) =>
        lastCheckUtc is null || nowUtc - lastCheckUtc.Value >= AppConfig.AutomaticUpdateCheckInterval || nowUtc < lastCheckUtc.Value;

    public static bool ShouldNotifyAutomatically(string? lastNotifiedVersion, SemanticVersion availableVersion) =>
        !string.Equals(lastNotifiedVersion, availableVersion.ToString(), StringComparison.OrdinalIgnoreCase);

    private static SemanticVersion ParseFallback()
    {
        SemanticVersion.TryParse("0.0.0", out SemanticVersion? fallback);
        return fallback!;
    }
}
