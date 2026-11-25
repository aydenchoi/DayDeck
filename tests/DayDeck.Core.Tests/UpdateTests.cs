using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DayDeck.Core.Updates;

namespace DayDeck.Core.Tests;

/// <summary>자동 업데이트: 릴리스 해석, 버전 비교, 내려받기 확인, 파일 바꾸기.</summary>
public sealed class UpdateTests : IDisposable
{
    private const string Download = "https://github.com/aydenchoi/DayDeck/releases/download/v0.3.0/";
    private const string Prefix = "https://github.com/aydenchoi/DayDeck/releases/download/";
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string ReleaseJson(string tag = "v0.3.0", bool prerelease = false, string? digest = null, string assetUrl = Download + "DayDeck-0.3.0-x64.zip") => $$"""
        {
          "tag_name": "{{tag}}", "name": "DayDeck 0.3.0", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/aydenchoi/DayDeck/releases/tag/{{tag}}",
          "published_at": "2026-10-28T23:00:00Z",
          "body": "## 새 기능\r\n- 자동 업데이트",
          "assets": [
            { "name": "DayDeck-0.3.0-x64.zip", "size": 100, "browser_download_url": "{{assetUrl}}"{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} },
            { "name": "DayDeck-0.3.0-x64-framework-dependent.zip", "size": 50, "browser_download_url": "{{Download}}DayDeck-0.3.0-x64-framework-dependent.zip" },
            { "name": "SHA256SUMS.txt", "size": 10, "browser_download_url": "{{Download}}SHA256SUMS.txt" }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsVersionNotesAndPackages()
    {
        var release = ReleaseFeed.Parse(ReleaseJson(digest: "AB" + new string('0', 62)), Prefix)!;

        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal("DayDeck 0.3.0", release.Title);
        Assert.Equal("## 새 기능\n- 자동 업데이트", release.Notes);
        Assert.Equal("DayDeck-0.3.0-x64.zip", release.PackageFor(selfContained: true)!.Name);
        Assert.Equal("ab" + new string('0', 62), release.PackageFor(true)!.Sha256);
        Assert.Equal("DayDeck-0.3.0-x64-framework-dependent.zip", release.PackageFor(selfContained: false)!.Name);
        Assert.Null(release.PackageFor(false)!.Sha256);
        Assert.NotNull(release.Checksums);
    }

    [Fact]
    public void Parse_IgnoresPrereleaseBadTagAndForeignDownloads()
    {
        Assert.Null(ReleaseFeed.Parse(ReleaseJson(prerelease: true), Prefix));
        Assert.Null(ReleaseFeed.Parse(ReleaseJson(tag: "latest"), Prefix));
        var foreign = ReleaseFeed.Parse(ReleaseJson(assetUrl: "https://evil.example.com/DayDeck-0.3.0-x64.zip"), Prefix)!;
        Assert.Null(foreign.PackageFor(true));
    }

    [Theory]
    [InlineData("v0.3.0", "0.2.0", true)]
    [InlineData("v0.2.0", "0.2.0.0", false)]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("v0.10", "0.9.5", true)]
    [InlineData("v0.1.9", "0.2.0", false)]
    public void IsNewer(string tag, string current, bool expected) =>
        Assert.Equal(expected, ReleaseFeed.IsNewer(ReleaseFeed.ParseVersion(tag)!, Version.Parse(current)));

    [Fact]
    public void ParseVersion_RejectsPrereleaseSuffix() => Assert.Null(ReleaseFeed.ParseVersion("v1.0.0-beta"));

    [Fact]
    public void FindChecksum_ReadsSha256sumOutput()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  other.zip\n{hash} *DayDeck-0.3.0-x64.zip\n";
        Assert.Equal(hash, ReleaseFeed.FindChecksum(sums, "DayDeck-0.3.0-x64.zip"));
        Assert.Null(ReleaseFeed.FindChecksum(sums, "missing.zip"));
    }

    [Fact]
    public void ApiOverride_OnlyDownloadsBesideIt()
    {
        var client = new UpdateClient(new HttpClient(), new Uri("http://localhost:8123/latest"));
        Assert.Equal("http://localhost:8123/", client.DownloadPrefix);
        Assert.Equal(Prefix, new UpdateClient(new HttpClient()).DownloadPrefix);
    }

    [Fact]
    public async Task GetLatest_NoReleaseYet_ReturnsNull()
    {
        var client = new UpdateClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await client.GetLatestAsync());
    }

    [Fact]
    public async Task Download_VerifiesHashFromChecksumsFile_AndExtracts()
    {
        var zip = MakeZip(("DayDeck.exe", "new exe"), ("e_sqlite3.dll", "native"));
        var sums = $"{Sha256(zip)}  DayDeck-0.3.0-x64-framework-dependent.zip\n";
        var client = Client(zip, sums);
        var release = ReleaseFeed.Parse(ReleaseJson(), Prefix)!;
        var reports = new List<double>();

        var files = await client.DownloadAsync(release, release.PackageFor(selfContained: false)!, _dir.Combine("work"), new SyncProgress(reports.Add));

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(files, "DayDeck.exe")));
        Assert.True(File.Exists(Path.Combine(files, "e_sqlite3.dll")));
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task Download_WrongHash_Refuses()
    {
        var zip = MakeZip(("DayDeck.exe", "tampered"));
        var release = ReleaseFeed.Parse(ReleaseJson(digest: new string('0', 64)), Prefix)!;

        var error = await Assert.ThrowsAsync<UpdateException>(() => Client(zip, "").DownloadAsync(release, release.PackageFor(true)!, _dir.Combine("work")));
        Assert.Contains("해시", error.Message);
        Assert.False(Directory.Exists(_dir.Combine("work", "files")));
    }

    [Fact]
    public async Task Download_WithoutAnyHash_Refuses()
    {
        var json = ReleaseJson().Replace("""{ "name": "SHA256SUMS.txt", "size": 10, "browser_download_url": "https://github.com/aydenchoi/DayDeck/releases/download/v0.3.0/SHA256SUMS.txt" }""", """{ "name": "readme.txt", "browser_download_url": "x" }""");
        var release = ReleaseFeed.Parse(json, Prefix)!;
        Assert.Null(release.Checksums);

        await Assert.ThrowsAsync<UpdateException>(() => Client(MakeZip(("DayDeck.exe", "x")), "").DownloadAsync(release, release.PackageFor(true)!, _dir.Combine("work")));
    }

    [Fact]
    public async Task Download_PackageWithoutExe_Refuses()
    {
        var zip = MakeZip(("readme.txt", "hi"));
        var release = ReleaseFeed.Parse(ReleaseJson(digest: Sha256(zip)), Prefix)!;

        await Assert.ThrowsAsync<UpdateException>(() => Client(zip, "").DownloadAsync(release, release.PackageFor(true)!, _dir.Combine("work")));
    }

    [Fact]
    public void Apply_ReplacesFiles_KeepsOldUntilCleanup_AndLeavesDataAlone()
    {
        var app = Directory.CreateDirectory(_dir.Combine("app")).FullName;
        File.WriteAllText(Path.Combine(app, "DayDeck.exe"), "v1");
        Directory.CreateDirectory(Path.Combine(app, "Data"));
        File.WriteAllText(Path.Combine(app, "Data", "settings.json"), "{}");
        var staged = Directory.CreateDirectory(_dir.Combine("new")).FullName;
        File.WriteAllText(Path.Combine(staged, "DayDeck.exe"), "v2");
        File.WriteAllText(Path.Combine(staged, "e_sqlite3.dll"), "native");

        UpdateInstaller.Apply(staged, app);

        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "DayDeck.exe")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "DayDeck.exe.old")));
        Assert.Equal("native", File.ReadAllText(Path.Combine(app, "e_sqlite3.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(app, "Data", "settings.json")));

        Assert.Equal(1, UpdateInstaller.CleanupOldFiles(app));
        Assert.False(File.Exists(Path.Combine(app, "DayDeck.exe.old")));
    }

    [Fact]
    public void Apply_Failure_RollsBack()
    {
        var app = Directory.CreateDirectory(_dir.Combine("app")).FullName;
        File.WriteAllText(Path.Combine(app, "a.dll"), "old a");
        File.WriteAllText(Path.Combine(app, "DayDeck.exe"), "old exe");
        // 같은 이름의 폴더가 있으면 복사가 실패한다 (b.dll).
        Directory.CreateDirectory(Path.Combine(app, "b.dll"));
        var staged = Directory.CreateDirectory(_dir.Combine("new")).FullName;
        File.WriteAllText(Path.Combine(staged, "a.dll"), "new a");
        File.WriteAllText(Path.Combine(staged, "b.dll"), "new b");
        File.WriteAllText(Path.Combine(staged, "DayDeck.exe"), "new exe");

        Assert.Throws<UpdateException>(() => UpdateInstaller.Apply(staged, app));

        Assert.Equal("old a", File.ReadAllText(Path.Combine(app, "a.dll")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(app, "DayDeck.exe")));
        Assert.Empty(Directory.GetFiles(app, "*.old"));
    }

    [Fact]
    public void RetryWhileLocked_WaitsOutBriefLocks_ButNotOtherErrors()
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        const int DiskFull = unchecked((int)0x80070070);
        var waits = new List<TimeSpan>();
        var previous = UpdateInstaller.Sleep;
        UpdateInstaller.Sleep = waits.Add;
        try
        {
            // 백신 검사처럼 잠깐 잡혀 있다 풀리면 성공한다.
            var calls = 0;
            UpdateInstaller.RetryWhileLocked(() =>
            {
                if (++calls < 3)
                {
                    throw new IOException("locked", SharingViolation);
                }
            });
            Assert.Equal(3, calls);
            Assert.Equal(UpdateInstaller.RetryDelays[..2], waits);

            // 잠김이 아닌 오류는 기다리지 않고 바로 던진다.
            calls = 0;
            waits.Clear();
            Assert.Throws<IOException>(() => UpdateInstaller.RetryWhileLocked(() =>
            {
                calls++;
                throw new IOException("disk full", DiskFull);
            }));
            Assert.Equal(1, calls);
            Assert.Empty(waits);

            // 끝까지 잠겨 있으면 정해진 만큼 기다린 뒤 던진다.
            calls = 0;
            Assert.Throws<IOException>(() => UpdateInstaller.RetryWhileLocked(() =>
            {
                calls++;
                throw new IOException("locked", LockViolation);
            }));
            Assert.Equal(UpdateInstaller.RetryDelays.Length + 1, calls);
            Assert.InRange(UpdateInstaller.RetryDelays.Sum(d => d.TotalSeconds), 5, 15);
        }
        finally
        {
            UpdateInstaller.Sleep = previous;
        }
    }

    private UpdateClient Client(byte[] zip, string sums) => new(new HttpClient(new FakeHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) })));

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Progress&lt;T&gt;는 동기화 컨텍스트로 늦게 알리므로 바로 기록하는 것을 쓴다.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
