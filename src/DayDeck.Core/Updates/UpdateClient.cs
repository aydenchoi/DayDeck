using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DayDeck.Core.Updates;

/// <summary>업데이트 중 사용자에게 보여줄 수 있는 실패.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GitHub 릴리스에서 새 버전을 찾고 내려받는다. 내려받은 파일은 SHA-256으로 확인한 뒤에만 푼다.
/// <paramref name="latestApi"/>를 주면 그 주소의 릴리스 정보를 쓰고 같은 주소 아래에서만 내려받는다 (업데이트 시험용).
/// </summary>
public sealed class UpdateClient(HttpClient http, Uri? latestApi = null)
{
    public const string Owner = "aydenchoi";
    public const string Repo = "DayDeck";
    /// <summary>릴리스 zip 안의 exe 이름.</summary>
    public const string ExeName = "DayDeck.exe";

    /// <summary>시험용 릴리스 정보 주소를 주는 환경 변수 (CI의 업데이트 시험이 로컬 서버를 가리킬 때).</summary>
    public const string ApiOverrideVariable = "DAYDECK_UPDATE_API";

    /// <summary>받을 수 있는 가장 큰 설치 파일 (잘못된 응답으로 디스크를 채우지 않도록).</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    public Uri LatestReleaseApi { get; } = latestApi ?? new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    public Uri ReleasesPage => new($"https://github.com/{Owner}/{Repo}/releases");

    /// <summary>설치 파일을 받아도 되는 주소의 시작.</summary>
    public string DownloadPrefix { get; } = latestApi is null
        ? $"https://github.com/{Owner}/{Repo}/releases/download/"
        : new Uri(latestApi, ".").AbsoluteUri;

    /// <summary>최신 릴리스. 아직 공개된 릴리스가 없으면 null.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        AddUserAgent(request);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"새 버전을 확인하지 못했습니다 (HTTP {(int)response.StatusCode}).");
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ReleaseFeed.Parse(json, DownloadPrefix);
    }

    /// <summary>
    /// 설치 파일(zip)을 <paramref name="workDirectory"/>에 내려받아 해시를 확인하고 풀어서, 새 파일이 든 폴더를 돌려준다.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, UpdateAsset package, string workDirectory,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var expected = package.Sha256;
        if (expected is null && release.Checksums is { } sums)
        {
            expected = ReleaseFeed.FindChecksum(await GetStringAsync(sums.DownloadUrl, cancellationToken).ConfigureAwait(false), package.Name);
        }
        if (expected is null)
        {
            throw new UpdateException("설치 파일을 확인할 해시가 없어 업데이트하지 않습니다.");
        }
        if (package.Size > MaxPackageBytes)
        {
            throw new UpdateException("설치 파일이 너무 큽니다.");
        }

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }
        Directory.CreateDirectory(workDirectory);
        var zipPath = Path.Combine(workDirectory, "package.zip");

        using (var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl))
        {
            AddUserAgent(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"설치 파일을 내려받지 못했습니다 (HTTP {(int)response.StatusCode}).");
            }
            var total = response.Content.Headers.ContentLength ?? package.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxPackageBytes)
                    {
                        throw new UpdateException("설치 파일이 너무 큽니다.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(1.0, (double)received / total));
                    }
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("내려받은 파일이 손상되었거나 바뀌었습니다 (해시가 다름). 업데이트하지 않습니다.");
            }
        }

        var files = Path.Combine(workDirectory, "files");
        try
        {
            // 압축 파일 밖으로 나가는 경로(../)는 ExtractToDirectory가 거부한다.
            ZipFile.ExtractToDirectory(zipPath, files);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException("설치 파일을 풀 수 없습니다.", ex);
        }
        if (!File.Exists(Path.Combine(files, ExeName)) || Directory.GetDirectories(files).Length > 0)
        {
            throw new UpdateException("설치 파일 내용이 올바르지 않습니다.");
        }
        return files;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddUserAgent(request);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"해시 파일을 내려받지 못했습니다 (HTTP {(int)response.StatusCode}).");
        }
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddUserAgent(HttpRequestMessage request) =>
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DayDeck", typeof(UpdateClient).Assembly.GetName().Version?.ToString(3) ?? "0"));
}
