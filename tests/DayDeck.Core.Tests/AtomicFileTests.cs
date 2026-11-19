using DayDeck.Core.IO;

namespace DayDeck.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void WriteAllText_CreatesDirectoryAndFile()
    {
        var path = _dir.Combine("a", "b", "note.md");

        AtomicFile.WriteAllText(path, "# 안녕");

        Assert.Equal("# 안녕", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingAndLeavesNoTempFiles()
    {
        var path = _dir.Combine("note.md");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void WriteAllText_WritesUtf8WithoutBom()
    {
        var path = _dir.Combine("note.txt");

        AtomicFile.WriteAllText(path, "가");

        Assert.Equal([0xEA, 0xB0, 0x80], File.ReadAllBytes(path));
    }
}
