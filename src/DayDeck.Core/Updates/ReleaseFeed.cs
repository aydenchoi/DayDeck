using System.Globalization;
using System.Text.Json;

namespace DayDeck.Core.Updates;

/// <summary>릴리스에 올라간 파일 하나. <see cref="Sha256"/>은 GitHub가 계산한 해시(소문자 16진수)이며 없을 수 있다.</summary>
public sealed record UpdateAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>공개된 새 버전 (GitHub 릴리스).</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Title, string Notes, DateTimeOffset? PublishedAt, Uri PageUrl,
    IReadOnlyList<UpdateAsset> Assets)
{
    public const string ChecksumsName = "SHA256SUMS.txt";

    /// <summary>업데이트 zip 이름의 시작.</summary>
    public const string PackagePrefix = "DayDeck-";

    /// <summary>
    /// 지금 실행 중인 것과 같은 종류의 설치 파일. 단독 실행(런타임 포함) exe는 <c>DayDeck-버전-x64.zip</c>,
    /// .NET 런타임이 따로 필요한 exe는 <c>DayDeck-버전-x64-framework-dependent.zip</c>.
    /// </summary>
    public UpdateAsset? PackageFor(bool selfContained) =>
        Assets.FirstOrDefault(a => a.Name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(selfContained ? "-x64.zip" : "-x64-framework-dependent.zip", StringComparison.OrdinalIgnoreCase));

    public UpdateAsset? Checksums => Assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumsName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>GitHub "최신 릴리스" 응답 해석과 버전 비교.</summary>
public static class ReleaseFeed
{
    /// <summary>
    /// 최신 릴리스 JSON을 읽는다. 초안·시험판, 버전이 아닌 태그는 null.
    /// 내려받기 주소가 <paramref name="downloadPrefix"/>(이 저장소의 릴리스 주소)로 시작하지 않는 파일은 버린다 (다른 곳에서 받지 않도록).
    /// </summary>
    public static ReleaseInfo? Parse(string json, string downloadPrefix)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")
            || ParseVersion(String(root, "tag_name")) is not { } version
            || !Uri.TryCreate(String(root, "html_url"), UriKind.Absolute, out var page))
        {
            return null;
        }

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var url = String(asset, "browser_download_url");
                if (!url.StartsWith(downloadPrefix, StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    continue;
                }
                var digest = String(asset, "digest");
                var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..].ToLowerInvariant() : null;
                assets.Add(new UpdateAsset(String(asset, "name"), uri,
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0, sha256));
            }
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(String(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
        var title = String(root, "name");
        return new ReleaseInfo(version, String(root, "tag_name"), title.Length > 0 ? title : $"DayDeck {version.ToString(3)}",
            String(root, "body").Replace("\r\n", "\n"), published, page, assets);
    }

    /// <summary>"v1.2.3", "1.2" 같은 태그를 버전으로. 뒤에 "-beta" 등이 붙은 것은 시험판이라 null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        var text = tag?.Trim() ?? "";
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>비교할 수 있도록 major.minor.build 세 자리로 맞춘다 (어셈블리 버전의 네 번째 자리는 버린다).</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary><c>sha256sum</c> 출력("해시  파일이름")에서 파일의 해시를 찾는다.</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }
        return null;
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
