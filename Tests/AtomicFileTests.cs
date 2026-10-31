using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HipHipParquet.Services;
using Xunit;

namespace HipHipParquet.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hhp-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task WriteAllTextAsync_ReplacesContent_AndLeavesNoTempFile()
    {
        var path = Path.Combine(_folder, "notes.md");
        await File.WriteAllTextAsync(path, "old contents that are longer than the new ones");

        await AtomicFile.WriteAllTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task WriteAllTextAsync_WritesUtf8WithoutBom_AndCreatesTheFolder()
    {
        var path = Path.Combine(_folder, "nested", "café.md");

        await AtomicFile.WriteAllTextAsync(path, "é");

        Assert.Equal(new byte[] { 0xC3, 0xA9 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_WhenTheWriteFails_KeepsTheOriginalFile()
    {
        var path = Path.Combine(_folder, "notes.md");
        await File.WriteAllTextAsync(path, "original");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AtomicFile.WriteAllTextAsync(path, "replacement", cancelled.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }
}
