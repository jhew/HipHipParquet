using HipHipParquet.Models;
using System.IO;
using System.Text;

namespace HipHipParquet.Services;

/// <summary>An opened file a recipe reads, as the Query Hub registered it.</summary>
public sealed record RecipeSource(string Alias, string ReaderExpression, string? FilePath, SupportedFileFormat? Format);

/// <summary>
/// Turns a recipe into a script that repeats it without Hip Hip Parquet: a DuckDB SQL script,
/// or a Python script that runs the same SQL through the duckdb package and hands back Polars
/// DataFrames. Manual steps are listed as comments, since picked cells don't carry over.
/// </summary>
public static class RecipeExporter
{
    /// <summary>
    /// Leading and trailing whitespace as .NET's string.Trim() sees it: ASCII whitespace,
    /// vertical tab, NEL and every Unicode separator (which covers the no-break space).
    /// </summary>
    internal const string TrimPattern = @"^[\s\x0B\x{85}\p{Z}]+|[\s\x0B\x{85}\p{Z}]+$";

    public static RecipeStep CreateTrimRule(string target, string column) => new()
    {
        Kind = RecipeStepKind.Rule,
        Origin = RecipeStepOrigin.Grid,
        Target = target,
        StepType = "trim",
        Columns = [column],
        Description = $"Trim whitespace in {column}"
    };

    public static RecipeStep CreateSetNullRule(string target, string column) => new()
    {
        Kind = RecipeStepKind.Rule,
        Origin = RecipeStepOrigin.Grid,
        Target = target,
        StepType = "set_null",
        Columns = [column],
        Description = $"Set every value in {column} to null"
    };

    public static RecipeStep CreateReplaceRule(string target, string column, string find, string replace, bool matchCase) => new()
    {
        Kind = RecipeStepKind.Rule,
        Origin = RecipeStepOrigin.Grid,
        Target = target,
        StepType = "replace",
        Columns = [column],
        Parameters = new() { ["find"] = find, ["replace"] = replace, ["matchCase"] = matchCase ? "true" : "false" },
        Description = $"Replace \"{find}\" with \"{replace}\" in {column}{(matchCase ? " (match case)" : string.Empty)}"
    };

    public static RecipeStep CreateDedupeRule(string target, IReadOnlyList<string> columns) => new()
    {
        Kind = RecipeStepKind.Rule,
        Origin = RecipeStepOrigin.Grid,
        Target = target,
        StepType = "dedupe",
        Columns = [.. columns],
        Description = $"Delete duplicate rows on {string.Join(", ", columns)}, keeping the first"
    };

    public static RecipeStep CreateManualStep(string target, string description) => new()
    {
        Kind = RecipeStepKind.Manual,
        Origin = RecipeStepOrigin.Grid,
        Target = target,
        Description = description
    };

    public static RecipeStep CreateSqlStep(string target, string sql, string description) => new()
    {
        Kind = RecipeStepKind.Sql,
        Origin = RecipeStepOrigin.QueryHub,
        Target = target,
        Sql = sql.Trim().TrimEnd(';').TrimEnd(),
        Description = description
    };

    /// <summary>The SQL a step runs; null for a manual step.</summary>
    public static string? BuildStepSql(RecipeStep step)
    {
        if (step.Kind == RecipeStepKind.Sql)
            return step.Sql;
        if (step.Kind != RecipeStepKind.Rule)
            return null;

        var table = Ident(step.Target);
        switch (step.StepType)
        {
            case "trim":
            {
                var column = Ident(step.Columns[0]);
                return $"UPDATE {table} SET {column} = regexp_replace({column}, {Literal(TrimPattern)}, '', 'g') WHERE {column} IS NOT NULL";
            }
            case "set_null":
                return $"UPDATE {table} SET {Ident(step.Columns[0])} = NULL";
            case "replace":
            {
                var column = Ident(step.Columns[0]);
                var find = step.Parameters["find"];
                var replace = step.Parameters["replace"];
                if (step.Parameters.TryGetValue("matchCase", out var matchCase) && matchCase == "true")
                    return $"UPDATE {table} SET {column} = replace({column}, {Literal(find)}, {Literal(replace)}) WHERE contains({column}, {Literal(find)})";

                // RE2 has no literal-replace mode, so escape the pattern and the replacement.
                return $"UPDATE {table} SET {column} = regexp_replace({column}, {Literal("(?i)" + EscapeRegex(find))}, {Literal(replace.Replace("\\", "\\\\"))}, 'g') WHERE regexp_matches({column}, {Literal("(?i)" + EscapeRegex(find))})";
            }
            case "dedupe":
            {
                var partition = string.Join(", ", step.Columns.Select(Ident));
                // rowid follows load order, so "first" means the same row the grid kept, and a
                // DELETE leaves the remaining rows in their order.
                return $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM (SELECT rowid, row_number() OVER (PARTITION BY {partition} ORDER BY rowid) AS n FROM {table}) WHERE n > 1)";
            }
            default:
                throw new NotSupportedException($"Unknown recipe rule '{step.StepType}'.");
        }
    }

    public static string ToDuckDbSql(IReadOnlyList<RecipeStep> steps, IReadOnlyList<RecipeSource> sources, DateTime generatedAtLocal)
    {
        var plan = BuildPlan(steps, sources);
        var sb = new StringBuilder();
        sb.AppendLine("-- Hip Hip Parquet recipe");
        sb.AppendLine($"-- Generated {generatedAtLocal:yyyy-MM-dd HH:mm}");
        sb.AppendLine("--");
        sb.AppendLine("-- Run it with the DuckDB CLI:  duckdb -f recipe.sql");
        sb.AppendLine("-- To apply it to a different file, change the paths in the \"Read\" and \"Write\" sections.");
        sb.AppendLine();

        foreach (var section in plan)
        {
            sb.AppendLine($"-- {section.Heading}");
            foreach (var item in section.Items)
            {
                if (item.Comment != null)
                    sb.AppendLine($"-- {item.Comment}");
                if (item.Sql != null)
                    sb.AppendLine(item.Sql + ";");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToPython(IReadOnlyList<RecipeStep> steps, IReadOnlyList<RecipeSource> sources, DateTime generatedAtLocal)
    {
        var plan = BuildPlan(steps, sources);
        var sb = new StringBuilder();
        sb.AppendLine("\"\"\"Hip Hip Parquet recipe.");
        sb.AppendLine();
        sb.AppendLine($"Generated {generatedAtLocal:yyyy-MM-dd HH:mm}.");
        sb.AppendLine();
        sb.AppendLine("Needs:  pip install duckdb polars pyarrow");
        sb.AppendLine("Runs the same SQL as the app through DuckDB and returns the results as Polars");
        sb.AppendLine("DataFrames. To apply it to a different file, change the paths under \"Read\".");
        sb.AppendLine("\"\"\"");
        sb.AppendLine("import duckdb");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def run(con: duckdb.DuckDBPyConnection) -> dict:");

        var outputs = new List<string>();
        foreach (var section in plan)
        {
            if (section.IsWrite)
            {
                outputs.AddRange(section.Outputs);
                continue;
            }

            sb.AppendLine($"    # {section.Heading}");
            foreach (var item in section.Items)
            {
                if (item.Comment != null)
                    sb.AppendLine($"    # {item.Comment}");
                if (item.Sql != null)
                    sb.AppendLine($"    con.execute({PythonString(item.Sql)})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("    # Results as Polars DataFrames, by table name");
        sb.AppendLine("    return {");
        foreach (var alias in outputs)
            sb.AppendLine($"        {PythonString(alias)}: con.sql({PythonString("SELECT * FROM " + Ident(alias))}).pl(),");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("if __name__ == \"__main__\":");
        sb.AppendLine("    frames = run(duckdb.connect())");
        sb.AppendLine("    for name, frame in frames.items():");
        sb.AppendLine("        print(name, frame.shape)");
        sb.AppendLine("        # frame.write_parquet(f\"{name}.cleaned.parquet\")");
        return sb.ToString();
    }

    private sealed record PlanItem(string? Comment, string? Sql);

    private sealed class PlanSection(string heading, bool isWrite = false)
    {
        public string Heading { get; } = heading;
        public bool IsWrite { get; } = isWrite;
        public List<PlanItem> Items { get; } = [];
        public List<string> Outputs { get; } = [];
    }

    private static List<PlanSection> BuildPlan(IReadOnlyList<RecipeStep> steps, IReadOnlyList<RecipeSource> sources)
    {
        var byAlias = sources
            .GroupBy(source => source.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Files the recipe changes become tables, so UPDATE and DELETE work on them. The other
        // opened files are views, so statements that join to them still run.
        var changed = steps
            .Where(step => step.Kind != RecipeStepKind.Manual)
            .Select(step => step.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var changedFiles = changed.Where(byAlias.ContainsKey).ToList();

        var sections = new List<PlanSection>();

        var setup = new PlanSection("Setup");
        if (sources.Any(source => source.Format == SupportedFileFormat.Excel))
        {
            setup.Items.Add(new PlanItem("Excel files are read and written through the spatial extension.", "INSTALL spatial"));
            setup.Items.Add(new PlanItem(null, "LOAD spatial"));
        }
        if (setup.Items.Count > 0)
            sections.Add(setup);

        var read = new PlanSection("Read");
        foreach (var source in sources)
        {
            var kind = changedFiles.Contains(source.Alias, StringComparer.OrdinalIgnoreCase) ? "TABLE" : "VIEW";
            read.Items.Add(new PlanItem(null, $"CREATE OR REPLACE {kind} {Ident(source.Alias)} AS SELECT * FROM {source.ReaderExpression}"));
        }
        sections.Add(read);

        var transform = new PlanSection("Steps");
        var number = 0;
        foreach (var step in steps)
        {
            number++;
            if (step.Kind == RecipeStepKind.Manual)
            {
                transform.Items.Add(new PlanItem($"{number}. {step.Description} on {step.Target}: a hand edit to picked cells or rows, so it is not repeated here.", null));
                continue;
            }

            transform.Items.Add(new PlanItem($"{number}. {step.Description}", BuildStepSql(step)));
        }
        if (transform.Items.Count == 0)
            transform.Items.Add(new PlanItem("No steps recorded.", null));
        sections.Add(transform);

        var write = new PlanSection("Write", isWrite: true);
        foreach (var alias in changed)
        {
            write.Outputs.Add(alias);
            if (byAlias.TryGetValue(alias, out var source) && source.FilePath != null && source.Format != null)
            {
                var output = GetCleanedPath(source.FilePath).Replace("\\", "/");
                var format = FileFormatDetector.GetDuckDbExportFormat(source.Format.Value);
                var options = FileFormatDetector.GetDuckDbExportOptions(source.Format.Value);
                write.Items.Add(new PlanItem(null, $"COPY {Ident(alias)} TO {Literal(output)} (FORMAT {format}{options})"));
            }
            else
            {
                write.Items.Add(new PlanItem($"{alias} was created by the recipe. Add a COPY statement to write it to a file.", null));
            }
        }
        if (write.Items.Count > 0)
            sections.Add(write);

        return sections;
    }

    /// <summary>"C:\data\sales.csv" becomes "C:\data\sales.cleaned.csv".</summary>
    public static string GetCleanedPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        return Path.Combine(directory, $"{name}.cleaned{Path.GetExtension(filePath)}");
    }

    private static string Ident(string name) => ParquetService.EscapeDuckDbIdentifier(name);

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Escapes RE2 metacharacters so the text matches literally.</summary>
    internal static string EscapeRegex(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            if ("\\.^$|?*+()[]{}-/".Contains(ch))
                sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string PythonString(string value)
    {
        // Raw triple-quoted strings keep SQL readable (quotes and regex backslashes as written);
        // fall back to escaping only when neither form can hold the text.
        if (value.IndexOfAny(['\\', '"', '\'', '\n', '\r']) < 0)
            return "\"" + value + "\"";
        if (!value.EndsWith('\\'))
        {
            if (!value.Contains("\"\"\"") && !value.EndsWith('"'))
                return "r\"\"\"" + value + "\"\"\"";
            if (!value.Contains("'''") && !value.EndsWith('\''))
                return "r'''" + value + "'''";
        }

        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString()
            });
        }
        return sb.Append('"').ToString();
    }
}
