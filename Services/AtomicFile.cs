using System.IO;
using System.Text;

namespace HipHipParquet.Services;

/// <summary>
/// Writes files so an interrupted save never leaves a truncated one behind.
/// </summary>
/// <remarks>
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> truncates the target
/// before writing it, so a crash, full disk or power cut mid-write loses the previous contents.
/// Here the text goes to a temporary sibling first, is flushed to disk, and only then moved over
/// the target. Until that move, the original file is untouched.
/// </remarks>
public static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAsync(
        string path, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        // Same folder, so the final move is a rename on one volume. Unique per write, so two
        // saves of the same file at once don't share (and clobber) a temp file.
        var tempPath = Path.Combine(directory, $".~{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await using (var writer = new StreamWriter(stream, Utf8NoBom))
                {
                    await writer.WriteAsync(content.AsMemory(), cancellationToken);
                    await writer.FlushAsync(cancellationToken);
                    // Reach the disk before the rename, or a power cut can leave the new name
                    // pointing at an empty file.
                    stream.Flush(flushToDisk: true);
                }
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
