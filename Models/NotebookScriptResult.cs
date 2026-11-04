using HipHipParquet.Services;

namespace HipHipParquet.Models;

/// <summary>What one statement of a Query Hub script did.</summary>
public sealed class NotebookStatementResult
{
    public required SqlStatement Statement { get; init; }

    /// <summary>Rows inserted, updated or deleted; null for queries and DDL.</summary>
    public long? RowsAffected { get; init; }

    /// <summary>For a query, the result table it produced.</summary>
    public NotebookSource? ResultSource { get; init; }

    /// <summary>False when the table was too large to snapshot, so this change can't be undone.</summary>
    public bool CanUndo { get; init; } = true;

    public string Summary => Statement.Kind switch
    {
        SqlStatementKind.Query => $"{ResultSource?.RowCount ?? 0:N0} rows → {ResultSource?.Alias}",
        SqlStatementKind.Insert => $"{RowsAffected ?? 0:N0} rows inserted into {Statement.Target}",
        SqlStatementKind.Update => $"{RowsAffected ?? 0:N0} rows updated in {Statement.Target}",
        SqlStatementKind.Delete => $"{RowsAffected ?? 0:N0} rows deleted from {Statement.Target}",
        SqlStatementKind.Merge => $"{RowsAffected ?? 0:N0} rows merged into {Statement.Target}",
        SqlStatementKind.Truncate => $"Emptied {Statement.Target}",
        SqlStatementKind.CreateTable => $"Created table {Statement.Target}",
        SqlStatementKind.CreateView => $"Created view {Statement.Target}",
        SqlStatementKind.AlterTable => $"Altered {Statement.Target}",
        SqlStatementKind.DropTable or SqlStatementKind.DropView => $"Dropped {Statement.Target}",
        _ => Statement.KindLabel
    };
}

public sealed class NotebookScriptResult
{
    public List<NotebookStatementResult> Statements { get; } = [];

    /// <summary>Aliases created, changed or dropped by the script's writes, in order.</summary>
    public List<string> ChangedAliases { get; } = [];
}
