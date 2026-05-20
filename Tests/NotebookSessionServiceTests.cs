using HipHipParquet.Models;
using HipHipParquet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace HipHipParquet.Tests;

public sealed class NotebookSessionServiceTests
{
    [Fact]
    public async Task GetPreviewPageAsync_AppliesFiltersSortAndPaging()
    {
        var csvPath = await CreateTempFileAsync(
            """
            id,name
            1,Ada
            2,Grace
            3,Ada
            """,
            ".csv");

        try
        {
            using var session = new NotebookSessionService(NullLogger<NotebookSessionService>.Instance);
            var source = await session.RegisterFileSourceAsync([csvPath], preferredAlias: "people");

            var queryState = new GridQueryState
            {
                ColumnFilters = new Dictionary<string, HashSet<string>>
                {
                    ["name"] = ["Ada"]
                },
                AvailableColumns = ["id", "name"],
                Sort = "id DESC",
                RowLimit = 1,
                RowOffset = 0
            };

            var preview = await session.GetPreviewPageAsync(source.Alias, queryState);

            Assert.Single(preview.Rows);
            Assert.Equal("3", preview.Rows[0]["id"]?.ToString());
            Assert.Equal(2, queryState.FilteredRowCount);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task ExecuteReadOnlyQueryAsync_CanJoinAcrossRegisteredSources()
    {
        var customersPath = await CreateTempFileAsync(
            """
            customer_id,customer_name
            1,Ada
            2,Grace
            """,
            ".csv");

        var ordersPath = await CreateTempFileAsync(
            """
            customer_id,amount
            1,10
            1,5
            2,7
            """,
            ".csv");

        try
        {
            using var session = new NotebookSessionService(NullLogger<NotebookSessionService>.Instance);
            await session.RegisterFileSourceAsync([customersPath], preferredAlias: "customers");
            await session.RegisterFileSourceAsync([ordersPath], preferredAlias: "orders");

            var result = await session.ExecuteReadOnlyQueryAsync(
                """
                SELECT c.customer_name, SUM(o.amount) AS total_amount
                FROM customers c
                JOIN orders o ON c.customer_id = o.customer_id
                GROUP BY c.customer_name
                ORDER BY c.customer_name
                """,
                preferredAlias: "customer_totals");

            var table = await session.MaterializeSourceAsync(result.Alias);

            Assert.Equal(2, table.Rows.Count);
            Assert.Equal("Ada", table.Rows[0]["customer_name"]?.ToString());
            Assert.Equal("15", table.Rows[0]["total_amount"]?.ToString());
            Assert.Equal("Grace", table.Rows[1]["customer_name"]?.ToString());
            Assert.Equal("7", table.Rows[1]["total_amount"]?.ToString());
        }
        finally
        {
            File.Delete(customersPath);
            File.Delete(ordersPath);
        }
    }

    [Fact]
    public async Task ValidationChecks_ReportNullsDuplicatesAndRegexMismatches()
    {
        var csvPath = await CreateTempFileAsync(
            """
            id,email
            1,ada@example.com
            1,
            2,invalid-email
            """,
            ".csv");

        try
        {
            using var session = new NotebookSessionService(NullLogger<NotebookSessionService>.Instance);
            var source = await session.RegisterFileSourceAsync([csvPath], preferredAlias: "contacts");

            var nullCheck = await session.RunNullEmptyCheckAsync(source.Alias);
            var duplicateCheck = await session.RunDuplicateCheckAsync(source.Alias, ["id"]);
            var regexCheck = await session.RunRegexCheckAsync(source.Alias, "email", @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

            Assert.Contains(nullCheck.Findings, finding => finding.ColumnName == "email" && finding.AffectedRows == 1);
            Assert.Single(duplicateCheck.Findings);
            Assert.Equal(1, duplicateCheck.Findings[0].AffectedRows);
            Assert.Single(regexCheck.Findings);
            Assert.Equal(1, regexCheck.Findings[0].AffectedRows);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task ValidateSchemaTemplateAsync_FindsMissingAndUnexpectedColumns()
    {
        var csvPath = await CreateTempFileAsync(
            """
            id,name,extra_field
            1,Ada,true
            """,
            ".csv");

        try
        {
            using var session = new NotebookSessionService(NullLogger<NotebookSessionService>.Instance);
            var source = await session.RegisterFileSourceAsync([csvPath], preferredAlias: "people");

            var template = new SchemaTemplate
            {
                Name = "people_template",
                Columns =
                [
                    new SchemaTemplateColumn { Name = "id", Type = "BIGINT", Required = true, Nullable = false },
                    new SchemaTemplateColumn { Name = "email", Type = "VARCHAR", Required = true, Nullable = true }
                ]
            };

            var result = await session.ValidateSchemaTemplateAsync(source.Alias, template);

            Assert.Contains(result.Findings, finding => finding.ColumnName == "email" && finding.Severity == NotebookValidationSeverity.Error);
            Assert.Contains(result.Findings, finding => finding.ColumnName == "extra_field" && finding.Severity == NotebookValidationSeverity.Info);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    private static async Task<string> CreateTempFileAsync(string contents, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, contents.Replace("\r\n", "\n"));
        return path;
    }
}
