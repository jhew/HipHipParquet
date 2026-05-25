using System.IO;
using HipHipParquet.Models;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class WorkspaceServiceMarkdownStateTests
{
    [Fact]
    public async Task SaveMarkdownEditorStateAsync_RoundTripsState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hiphipparquet-workspace-{Guid.NewGuid():N}");

        try
        {
            var service = new WorkspaceService(tempRoot);
            var expected = new MarkdownEditorState
            {
                FilePath = @"C:\docs\notes.md",
                DraftContent = "# Draft\n\n- item",
                SelectedProfile = MarkdownFlavorProfile.ExtendedBestEffort,
                IsDirty = true,
                SavedAtUtc = DateTime.UtcNow
            };

            await service.SaveMarkdownEditorStateAsync(expected);

            var reloadedService = new WorkspaceService(tempRoot);
            var actual = await reloadedService.GetMarkdownEditorStateAsync();

            Assert.NotNull(actual);
            Assert.Equal(expected.FilePath, actual!.FilePath);
            Assert.Equal(expected.DraftContent, actual.DraftContent);
            Assert.Equal(expected.SelectedProfile, actual.SelectedProfile);
            Assert.Equal(expected.IsDirty, actual.IsDirty);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
