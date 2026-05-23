namespace HipHipParquet.Models;

public enum MarkdownFlavorProfile
{
    CommonMark,
    GitHubStyle,
    ExtendedBestEffort
}

public static class MarkdownFlavorProfileExtensions
{
    public static string GetDisplayName(this MarkdownFlavorProfile profile)
        => profile switch
        {
            MarkdownFlavorProfile.CommonMark => "CommonMark",
            MarkdownFlavorProfile.GitHubStyle => "GitHub-style",
            MarkdownFlavorProfile.ExtendedBestEffort => "Extended / Best Effort",
            _ => profile.ToString()
        };
}
