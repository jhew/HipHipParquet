using DuckDB.NET.Data;
using HipHipParquet.Models;
using System.IO;

namespace HipHipParquet.Services;

/// <summary>
/// Query Hub scripts: SELECT plus INSERT, UPDATE, DELETE, MERGE, TRUNCATE and CREATE/ALTER/DROP
/// for tables and views, with per-statement undo and explicit Save/Discard for opened files.
///
/// An opened file starts as a view over the file. The first statement that changes it copies its
/// rows into a session table of the same name; the file is untouched until <see cref="SaveSourceAsync"/>.
/// Before each write the target's prior state is kept (the file itself, a view's SQL, or a table
/// snapshot in the hhp_undo schema) so <see cref="UndoLastChangeAsync"/> can put it back.
/// </summary>
public sealed partial class NotebookSessionService
{
    private const string UndoSchema = "hhp_undo";

    /// <summary>Tables larger than this aren't snapshotted; a change to one can't be undone.</summary>
    internal long UndoSnapshotRowLimit { get; set; } = 5_000_000;

    private readonly List<UndoEntry> _undoStack = [];
    private int _undoCounter;

    private enum PriorState
    {
        /// <summary>Nothing existed under the name.</summary>
        Absent,
        /// <summary>An opened file's own view; restored from its reader expression.</summary>
        FileView,
        /// <summary>A view created in the Hub; restored from its SQL.</summary>
        UserView,
        /// <summary>A table whose rows a DML statement changed; its rows are restored in place.</summary>
        TableRows,
        /// <summary>A table dropped, altered or replaced; recreated from the snapshot.</summary>
        TableFull
    }

    private sealed class UndoEntry
    {
        public required string Target { get; init; }
        public required string Description { get; init; }
        public required PriorState Prior { get; init; }
        public bool Restorable { get; init; } = true;
        public string? SnapshotTable { get; init; }
        public string? ViewSql { get; init; }
        /// <summary>The source registered under the name before the statement, or null.</summary>
        public NotebookSource? PriorSource { get; init; }
    }

    /// <summary>True when the most recent change can be undone.</summary>
    public bool CanUndo => _undoStack.Count > 0 && _undoStack[^1].Restorable;

    /// <summary>What Undo would revert, e.g. "UPDATE sales"; null when there's nothing to undo.</summary>
    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack[^1].Description : null;

    /// <summary>The table or view Undo would revert; null when there's nothing to undo.</summary>
    public string? UndoTarget => _undoStack.Count > 0 ? _undoStack[^1].Target : null;

    /// <summary>Opened files whose session copy has changes not yet saved to the file.</summary>
    public IReadOnlyList<NotebookSource> GetSourcesWithUnsavedChanges()
        => GetSources().Where(source => source.HasUnsavedChanges).ToList();

    /// <summary>
    /// Runs every statement of <paramref name="script"/> in one transaction. If any statement
    /// fails, none of them take effect, and the error names the failing statement.
    /// </summary>
    public async Task<NotebookScriptResult> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        var statements = SqlScriptParser.Parse(script);
        var connection = await EnsureConnectionAsync();

        var savedSources = _sources.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var savedUndoCount = _undoStack.Count;
        var result = new NotebookScriptResult();

        await ExecuteNonQueryAsync(connection, "BEGIN TRANSACTION");
        var index = 0;
        try
        {
            for (; index < statements.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var statement = statements[index];
                result.Statements.Add(statement.IsWrite
                    ? await ExecuteWriteAsync(connection, statement, result, cancellationToken)
                    : await ExecuteQueryAsync(connection, statement, cancellationToken));
            }

            await ExecuteNonQueryAsync(connection, "COMMIT");
            return result;
        }
        catch (Exception ex)
        {
            try { await ExecuteNonQueryAsync(connection, "ROLLBACK"); } catch { /* the transaction may already be aborted */ }

            _sources.Clear();
            foreach (var kvp in savedSources)
                _sources[kvp.Key] = kvp.Value;
            _undoStack.RemoveRange(savedUndoCount, _undoStack.Count - savedUndoCount);

            if (ex is OperationCanceledException)
                throw;

            var prefix = statements.Count > 1
                ? $"Statement {index + 1} of {statements.Count} failed, so none of the script was applied. "
                : string.Empty;
            throw new InvalidOperationException(prefix + ex.Message, ex);
        }
    }

    /// <summary>Reverts the most recent change and returns what was undone.</summary>
    public async Task<string> UndoLastChangeAsync()
    {
        if (_undoStack.Count == 0)
            throw new InvalidOperationException("There's nothing to undo.");

        var entry = _undoStack[^1];
        if (!entry.Restorable)
            throw new InvalidOperationException(
                $"{entry.Description} can't be undone: the table was too large to snapshot. Discard reverts an opened file to its saved state.");

        var connection = await EnsureConnectionAsync();
        await ExecuteNonQueryAsync(connection, "BEGIN TRANSACTION");
        try
        {
            await RestorePriorStateAsync(connection, entry);
            if (entry.SnapshotTable != null)
                await ExecuteNonQueryAsync(connection, $"DROP TABLE IF EXISTS {entry.SnapshotTable}");
            await ExecuteNonQueryAsync(connection, "COMMIT");
        }
        catch
        {
            try { await ExecuteNonQueryAsync(connection, "ROLLBACK"); } catch { /* best effort */ }
            throw;
        }

        _undoStack.RemoveAt(_undoStack.Count - 1);
        if (entry.PriorSource == null)
        {
            _sources.Remove(entry.Target);
        }
        else
        {
            var restored = entry.PriorSource.Clone();
            await RefreshShapeAsync(restored);
            _sources[restored.Alias] = restored;
        }

        return entry.Description;
    }

    /// <summary>
    /// Writes a source to <paramref name="filePath"/>, or to its own file when that is null.
    /// Saving an opened file in place (or Save As) makes the written file its saved state; saving
    /// a created table or query result just exports it.
    /// </summary>
    public async Task SaveSourceAsync(string alias, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var source = GetSourceOrThrow(alias);
        var target = filePath ?? (source.CanSaveInPlace
            ? source.FilePath!
            : throw new InvalidOperationException($"Choose a file to save {alias} to."));

        var connection = await EnsureConnectionAsync();
        var format = FileFormatDetector.DetectFormat(target);
        if (format == SupportedFileFormat.Excel)
            await EnsureExcelExtensionAsync(connection);

        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(alias);
        var exportSource = format == SupportedFileFormat.Excel
            ? $"(SELECT {ParquetService.BuildExcelColumnList(source.Columns.Select(c => c.Name).ToList(), source.Columns.Select(c => c.Type).ToList())} FROM {escapedAlias})"
            : escapedAlias;
        await ParquetService.CopyToFileAtomicallyAsync(connection, exportSource, target, cancellationToken);

        if (source.Kind != NotebookSourceKind.File)
            return;

        // The written file is now what Discard returns to. It's read back with default options:
        // the export wrote it in the format's default layout, not the original import options.
        if (filePath != null && !PathsEqual(filePath, source.FilePath))
        {
            source.FilePath = target;
            source.FilePaths = [target];
            source.DisplayName = Path.GetFileName(target);
            source.Format = format;
        }
        source.CsvOptions = null;
        source.JsonOptions = null;
        source.ReaderExpression = FileFormatDetector.GetDuckDbReaderExpression(target.Replace("\\", "/"), format, null, null);
        source.HasUnsavedChanges = false;
        await ForgetUndoEntriesAsync(connection, alias);
    }

    /// <summary>Throws away an opened file's unsaved changes, returning it to its saved state.</summary>
    public async Task DiscardChangesAsync(string alias)
    {
        var source = GetSourceOrThrow(alias);
        if (source.Kind != NotebookSourceKind.File || string.IsNullOrEmpty(source.ReaderExpression))
            throw new InvalidOperationException($"{alias} isn't an opened file, so there's nothing to discard.");

        var connection = await EnsureConnectionAsync();
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(alias);
        await DropObjectIfExistsAsync(connection, alias);
        await ExecuteNonQueryAsync(connection, $"CREATE VIEW {escapedAlias} AS SELECT * FROM {source.ReaderExpression}");

        source.IsMaterialized = false;
        source.HasUnsavedChanges = false;
        await RefreshShapeAsync(source);
        await ForgetUndoEntriesAsync(connection, alias);
    }

    private async Task<NotebookStatementResult> ExecuteQueryAsync(
        DuckDBConnection connection, SqlStatement statement, CancellationToken cancellationToken)
    {
        var alias = CreateUniqueAlias("query_result");
        var body = statement.NeedsSubqueryWrap ? $"SELECT * FROM ({statement.Sql})" : statement.Sql;
        await ExecuteNonQueryAsync(connection, $"CREATE TABLE {ParquetService.EscapeDuckDbIdentifier(alias)} AS {body}");

        var source = new NotebookSource
        {
            Alias = alias,
            DisplayName = alias,
            Kind = NotebookSourceKind.QueryResult,
            QuerySql = statement.Sql
        };
        await RefreshShapeAsync(source);
        _sources[alias] = source;
        return new NotebookStatementResult { Statement = statement, ResultSource = source };
    }

    private async Task<NotebookStatementResult> ExecuteWriteAsync(
        DuckDBConnection connection, SqlStatement statement, NotebookScriptResult result, CancellationToken cancellationToken)
    {
        var entry = await PrepareWriteAsync(connection, statement);

        long? rowsAffected;
        using (var command = new DuckDBCommand(statement.Sql, connection))
        {
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            rowsAffected = statement.Kind is SqlStatementKind.Insert or SqlStatementKind.Update
                or SqlStatementKind.Delete or SqlStatementKind.Merge ? changed : null;
        }

        await ApplyWriteToSourcesAsync(connection, statement);
        _undoStack.Add(entry);
        if (!result.ChangedAliases.Contains(statement.Target!, StringComparer.OrdinalIgnoreCase))
            result.ChangedAliases.Add(statement.Target!);

        return new NotebookStatementResult { Statement = statement, RowsAffected = rowsAffected, CanUndo = entry.Restorable };
    }

    /// <summary>
    /// Records the target's state before a write, and copies an opened file into a session
    /// table when the statement changes its rows or columns.
    /// </summary>
    private async Task<UndoEntry> PrepareWriteAsync(DuckDBConnection connection, SqlStatement statement)
    {
        var target = statement.Target!;
        var objectKind = await GetObjectKindAsync(connection, target);
        _sources.TryGetValue(target, out var source);
        var priorSource = source?.Clone();
        var description = $"{statement.KindLabel} {target}";

        if (objectKind == null)
            return new UndoEntry { Target = target, Description = description, Prior = PriorState.Absent, PriorSource = priorSource };

        if (objectKind == "view")
        {
            if (source is { Kind: NotebookSourceKind.File, IsMaterialized: false } && source.ReaderExpression != null)
            {
                if (statement.Kind is not (SqlStatementKind.DropView or SqlStatementKind.CreateView or SqlStatementKind.CreateTable))
                    await MaterializeFileSourceAsync(connection, source);
                return new UndoEntry { Target = target, Description = description, Prior = PriorState.FileView, PriorSource = priorSource };
            }

            var viewSql = await GetViewSqlAsync(connection, target);
            return new UndoEntry
            {
                Target = target,
                Description = description,
                Prior = PriorState.UserView,
                ViewSql = viewSql,
                Restorable = viewSql != null,
                PriorSource = priorSource
            };
        }

        var escapedTarget = ParquetService.EscapeDuckDbIdentifier(target);
        var rowCount = await ExecuteScalarLongAsync(connection, $"SELECT COUNT(*) FROM {escapedTarget}");
        var prior = statement.Kind is SqlStatementKind.Insert or SqlStatementKind.Update or SqlStatementKind.Delete
            or SqlStatementKind.Merge or SqlStatementKind.Truncate
            ? PriorState.TableRows
            : PriorState.TableFull;

        if (rowCount > UndoSnapshotRowLimit)
            return new UndoEntry { Target = target, Description = description, Prior = prior, Restorable = false, PriorSource = priorSource };

        // Every time, not once: a rolled-back script also rolls back a schema it created.
        await ExecuteNonQueryAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {UndoSchema}");

        var snapshot = $"{UndoSchema}.undo_{++_undoCounter}";
        await ExecuteNonQueryAsync(connection, $"CREATE TABLE {snapshot} AS SELECT * FROM {escapedTarget}");
        return new UndoEntry { Target = target, Description = description, Prior = prior, SnapshotTable = snapshot, PriorSource = priorSource };
    }

    private async Task MaterializeFileSourceAsync(DuckDBConnection connection, NotebookSource source)
    {
        var escapedAlias = ParquetService.EscapeDuckDbIdentifier(source.Alias);
        await ExecuteNonQueryAsync(connection, $"DROP VIEW {escapedAlias}");
        await ExecuteNonQueryAsync(connection, $"CREATE TABLE {escapedAlias} AS SELECT * FROM {source.ReaderExpression}");
        source.IsMaterialized = true;
    }

    /// <summary>Updates the source list after a write: a created object, a changed file, or a drop.</summary>
    private async Task ApplyWriteToSourcesAsync(DuckDBConnection connection, SqlStatement statement)
    {
        var target = statement.Target!;
        var objectKind = await GetObjectKindAsync(connection, target);
        if (objectKind == null)
        {
            _sources.Remove(target);
            return;
        }

        _sources.TryGetValue(target, out var source);
        var replacesSource = statement.Kind is SqlStatementKind.CreateTable or SqlStatementKind.CreateView
            && source is not { Kind: NotebookSourceKind.Table };

        if (source == null || replacesSource)
        {
            // A CREATE under an opened file's name replaces it in the session; the file stays on disk.
            if (source != null && statement.IfExistsClause)
                return; // CREATE ... IF NOT EXISTS left the existing source alone

            source = new NotebookSource
            {
                Alias = await GetCatalogNameAsync(connection, target) ?? target,
                DisplayName = objectKind == "view" ? "view" : "table",
                Kind = NotebookSourceKind.Table,
                QuerySql = statement.Sql
            };
            _sources[source.Alias] = source;
        }
        else if (source.Kind == NotebookSourceKind.File)
        {
            source.HasUnsavedChanges = true;
        }

        await RefreshShapeAsync(source);
    }

    private async Task RestorePriorStateAsync(DuckDBConnection connection, UndoEntry entry)
    {
        var escapedTarget = ParquetService.EscapeDuckDbIdentifier(entry.Target);
        switch (entry.Prior)
        {
            case PriorState.Absent:
                await DropObjectIfExistsAsync(connection, entry.Target);
                break;
            case PriorState.FileView:
                await DropObjectIfExistsAsync(connection, entry.Target);
                await ExecuteNonQueryAsync(connection, $"CREATE VIEW {escapedTarget} AS SELECT * FROM {entry.PriorSource!.ReaderExpression}");
                break;
            case PriorState.UserView:
                await DropObjectIfExistsAsync(connection, entry.Target);
                await ExecuteNonQueryAsync(connection, entry.ViewSql!);
                break;
            case PriorState.TableRows when await GetObjectKindAsync(connection, entry.Target) == "table":
                // Rows only, so the table keeps its own definition (constraints, defaults).
                await ExecuteNonQueryAsync(connection, $"DELETE FROM {escapedTarget}");
                await ExecuteNonQueryAsync(connection, $"INSERT INTO {escapedTarget} SELECT * FROM {entry.SnapshotTable}");
                break;
            default:
                await DropObjectIfExistsAsync(connection, entry.Target);
                await ExecuteNonQueryAsync(connection, $"CREATE TABLE {escapedTarget} AS SELECT * FROM {entry.SnapshotTable}");
                break;
        }
    }

    /// <summary>Drops undo snapshots for a source whose changes were just saved or discarded.</summary>
    private async Task ForgetUndoEntriesAsync(DuckDBConnection connection, string alias)
    {
        foreach (var entry in _undoStack.Where(e => string.Equals(e.Target, alias, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (entry.SnapshotTable != null)
                await ExecuteNonQueryAsync(connection, $"DROP TABLE IF EXISTS {entry.SnapshotTable}");
            _undoStack.Remove(entry);
        }
    }

    private async Task RefreshShapeAsync(NotebookSource source)
    {
        source.Columns = await DescribeSourceAsync(source.Alias);
        source.RowCount = await GetSourceRowCountAsync(source.Alias);
    }

    /// <summary>"table", "view", or null when nothing in the main schema has that name.</summary>
    private static async Task<string?> GetObjectKindAsync(DuckDBConnection connection, string name)
    {
        var literal = SqlLiteral(name);
        using var command = new DuckDBCommand($"""
            SELECT 'table' FROM duckdb_tables() WHERE schema_name = 'main' AND lower(table_name) = lower({literal})
            UNION ALL
            SELECT 'view' FROM duckdb_views() WHERE schema_name = 'main' AND NOT internal AND lower(view_name) = lower({literal})
            LIMIT 1
            """, connection);
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>The name as the catalog stores it, so a created source shows the user's casing.</summary>
    private static async Task<string?> GetCatalogNameAsync(DuckDBConnection connection, string name)
    {
        var literal = SqlLiteral(name);
        using var command = new DuckDBCommand($"""
            SELECT table_name FROM duckdb_tables() WHERE schema_name = 'main' AND lower(table_name) = lower({literal})
            UNION ALL
            SELECT view_name FROM duckdb_views() WHERE schema_name = 'main' AND NOT internal AND lower(view_name) = lower({literal})
            LIMIT 1
            """, connection);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> GetViewSqlAsync(DuckDBConnection connection, string name)
    {
        using var command = new DuckDBCommand(
            $"SELECT sql FROM duckdb_views() WHERE schema_name = 'main' AND NOT internal AND lower(view_name) = lower({SqlLiteral(name)}) LIMIT 1",
            connection);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task DropObjectIfExistsAsync(DuckDBConnection connection, string name)
    {
        var kind = await GetObjectKindAsync(connection, name);
        if (kind != null)
            await ExecuteNonQueryAsync(connection, $"DROP {kind.ToUpperInvariant()} {ParquetService.EscapeDuckDbIdentifier(name)}");
    }

    private static string SqlLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static bool PathsEqual(string? a, string? b)
        => a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
