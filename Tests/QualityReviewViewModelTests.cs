using HipHipParquet.Models;
using HipHipParquet.Services;
using HipHipParquet.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace HipHipParquet.Tests;

public sealed class QualityReviewViewModelTests
{
    [Fact]
    public async Task AnalyzeCommand_UsesConfiguredAnalysisProvider()
    {
        var viewModel = CreateViewModel();
        var providerCalled = false;

        viewModel.SetAnalysisContext(
            displayPath: "query://people",
            analysisProvider: (_, _) =>
            {
                providerCalled = true;
                return Task.FromResult(CreateProfile("Current People Set"));
            },
            readyMessage: "Ready to analyze current set.");

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(providerCalled);
        Assert.True(viewModel.HasProfile);
        Assert.Equal("Current People Set", viewModel.ProfileDisplayName);
        Assert.NotEmpty(viewModel.AvailableDimensions);
    }

    [Fact]
    public async Task ApplyGroupByCommand_UsesConfiguredGroupedStatisticsProvider()
    {
        var viewModel = CreateViewModel();
        var groupedProviderCalled = false;

        viewModel.SetAnalysisContext(
            displayPath: "query://people",
            analysisProvider: (_, _) => Task.FromResult(CreateProfile("Current People Set")),
            groupedStatisticsProvider: (dimensions, _, _) =>
            {
                groupedProviderCalled = true;
                Assert.Equal(["segment"], dimensions);

                var groupedProfile = CreateProfile("Enterprise");
                groupedProfile.RowCount = 2;
                return Task.FromResult(new Dictionary<string, FileProfile>
                {
                    ["Enterprise"] = groupedProfile
                });
            });

        await viewModel.AnalyzeCommand.ExecuteAsync(null);
        var dimension = Assert.Single(viewModel.AvailableDimensions);
        dimension.IsSelected = true;

        await viewModel.ApplyGroupByCommand.ExecuteAsync(null);

        Assert.True(groupedProviderCalled);
        Assert.True(viewModel.HasGroupedResults);
        Assert.Single(viewModel.GroupedResults);
        Assert.Equal("Enterprise", viewModel.GroupedResults[0].GroupKey);
    }

    [Fact]
    public async Task ApplyGroupByCommand_LoadsInitialBatchAndSupportsLoadMore()
    {
        var viewModel = CreateViewModel();

        viewModel.SetAnalysisContext(
            displayPath: "query://people",
            analysisProvider: (_, _) => Task.FromResult(CreateProfile("Current People Set")),
            groupedStatisticsProvider: (_, _, _) => Task.FromResult(
                Enumerable.Range(1, 30).ToDictionary(
                    index => $"Group {index}",
                    index =>
                    {
                        var profile = CreateProfile($"Group {index}");
                        profile.RowCount = 30 - index;
                        return profile;
                    })));

        await viewModel.AnalyzeCommand.ExecuteAsync(null);
        var dimension = Assert.Single(viewModel.AvailableDimensions);
        dimension.IsSelected = true;

        await viewModel.ApplyGroupByCommand.ExecuteAsync(null);

        Assert.Equal(25, viewModel.GroupedResults.Count);
        Assert.True(viewModel.CanLoadMoreGroupedResults);
        Assert.Contains("Showing top 25 of 30 groups", viewModel.GroupedResultsSummary);

        viewModel.LoadMoreGroupedResultsCommand.Execute(null);

        Assert.Equal(30, viewModel.GroupedResults.Count);
        Assert.False(viewModel.CanLoadMoreGroupedResults);
        Assert.Contains("Showing all 30 groups", viewModel.GroupedResultsSummary);
    }

    [Fact]
    public async Task AnalyzeCommand_WithZeroRows_ShowsNoDataStateInsteadOfScore()
    {
        var viewModel = CreateViewModel();

        viewModel.SetAnalysisContext(
            displayPath: "query://empty",
            analysisProvider: (_, _) => Task.FromResult(CreateEmptyProfile("Empty Set")),
            readyMessage: "Ready to analyze current set.");

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasProfile);
        Assert.True(viewModel.IsEmptyProfile);
        Assert.Equal(0, viewModel.OverallScore);
        Assert.Equal(string.Empty, viewModel.OverallGrade);
        Assert.Contains("No rows are available in the current set", viewModel.EmptyProfileMessage);
        Assert.Contains("0 rows in the current set", viewModel.StatusMessage);
        Assert.DoesNotContain(viewModel.Findings, finding => finding.Description.Contains("Overall quality:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(viewModel.Findings, finding => finding.Description.Contains("Overall data quality score", StringComparison.OrdinalIgnoreCase));
    }

    private static QualityReviewViewModel CreateViewModel()
        => new(
            NullLogger<QualityReviewViewModel>.Instance,
            new QualityScoreService(),
            new NarrativeService(),
            new ReportService());

    private static FileProfile CreateProfile(string fileName)
        => new()
        {
            FileName = fileName,
            FilePath = fileName,
            SourceFormat = SupportedFileFormat.Parquet,
            RowCount = 3,
            ColumnCount = 2,
            Columns =
            [
                new ColumnProfile
                {
                    Name = "segment",
                    DuckDbType = "VARCHAR",
                    Category = ColumnCategory.String,
                    TotalRows = 3,
                    DistinctCount = 2,
                    TopValues =
                    [
                        new ValueFrequency { Value = "Enterprise", Count = 2, Percentage = 66.7, RelativeWidth = 100 },
                        new ValueFrequency { Value = "SMB", Count = 1, Percentage = 33.3, RelativeWidth = 50 }
                    ]
                },
                new ColumnProfile
                {
                    Name = "amount",
                    DuckDbType = "DOUBLE",
                    Category = ColumnCategory.Numeric,
                    TotalRows = 3,
                    DistinctCount = 3,
                    Min = 10,
                    Max = 30,
                    Mean = 20,
                    Median = 20,
                    Q1 = 15,
                    Q3 = 25
                }
            ]
        };

    private static FileProfile CreateEmptyProfile(string fileName)
        => new()
        {
            FileName = fileName,
            FilePath = fileName,
            SourceFormat = SupportedFileFormat.Parquet,
            RowCount = 0,
            ColumnCount = 2,
            Columns =
            [
                new ColumnProfile
                {
                    Name = "segment",
                    DuckDbType = "VARCHAR",
                    Category = ColumnCategory.String,
                    TotalRows = 0,
                    DistinctCount = 0
                },
                new ColumnProfile
                {
                    Name = "amount",
                    DuckDbType = "DOUBLE",
                    Category = ColumnCategory.Numeric,
                    TotalRows = 0,
                    DistinctCount = 0
                }
            ]
        };
}