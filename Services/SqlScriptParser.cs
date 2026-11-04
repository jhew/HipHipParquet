using System.Text;

namespace HipHipParquet.Services;

public enum SqlStatementKind
{
    Query,
    Insert,
    Update,
    Delete,
    Merge,
    Truncate,
    CreateTable,
    CreateView,
    AlterTable,
    DropTable,
    DropView
}

/// <summary>One statement of a Query Hub script and the object it writes, if any.</summary>
public sealed class SqlStatement
{
    public required string Sql { get; init; }
    public required SqlStatementKind Kind { get; init; }

    /// <summary>The table or view the statement writes; null for queries.</summary>
    public string? Target { get; init; }

    /// <summary>For DROP and CREATE: IF EXISTS / IF NOT EXISTS was given.</summary>
    public bool IfExistsClause { get; init; }

    public bool IsWrite => Kind != SqlStatementKind.Query;

    /// <summary>Queries that DuckDB only accepts as a subquery inside CREATE TABLE ... AS.</summary>
    public bool NeedsSubqueryWrap { get; init; }

    public string KindLabel => Kind switch
    {
        SqlStatementKind.Query => "SELECT",
        SqlStatementKind.CreateTable => "CREATE TABLE",
        SqlStatementKind.CreateView => "CREATE VIEW",
        SqlStatementKind.AlterTable => "ALTER TABLE",
        SqlStatementKind.DropTable => "DROP TABLE",
        SqlStatementKind.DropView => "DROP VIEW",
        _ => Kind.ToString().ToUpperInvariant()
    };
}

/// <summary>
/// Splits a Query Hub script into statements and works out what each one writes. The Hub needs
/// the target before running a statement: to copy an opened file into a session table the first
/// time it is changed, to snapshot the table for undo, and to track unsaved changes. Statements
/// that reach outside the session (COPY, ATTACH, INSTALL, SET, transactions...) are refused.
/// </summary>
public static class SqlScriptParser
{
    private static readonly HashSet<string> QueryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "VALUES", "TABLE"
    };

    // DuckDB accepts these as a subquery (SELECT * FROM (DESCRIBE t)) but not directly after AS.
    private static readonly HashSet<string> WrappedQueryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHOW", "DESCRIBE", "DESC", "SUMMARIZE", "PIVOT", "UNPIVOT"
    };

    private static readonly HashSet<string> SessionSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory", "main", "temp"
    };

    /// <summary>Splits on top-level semicolons, skipping strings, quoted names and comments.</summary>
    public static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var i = 0;

        while (i < script.Length)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == ';')
            {
                AddStatement(statements, current);
                i++;
                continue;
            }

            var end = c switch
            {
                '\'' or '"' => SkipQuoted(script, i, c),
                '-' when next == '-' => SkipLineComment(script, i),
                '/' when next == '*' => SkipBlockComment(script, i),
                '$' => SkipDollarQuoted(script, i),
                _ => i + 1
            };

            current.Append(script, i, end - i);
            i = end;
        }

        AddStatement(statements, current);
        return statements;
    }

    /// <summary>Splits and classifies a script. Throws with a user-facing message on a refused statement.</summary>
    public static IReadOnlyList<SqlStatement> Parse(string script)
    {
        var statements = SplitStatements(script).Select(Classify).ToList();
        if (statements.Count == 0)
            throw new InvalidOperationException("Enter a SQL statement first.");
        return statements;
    }

    public static SqlStatement Classify(string sql)
    {
        var tokens = Tokenize(sql);
        if (tokens.Count == 0)
            throw new InvalidOperationException("Enter a SQL statement first.");

        var start = 0;
        if (tokens[0].IsWord("WITH"))
        {
            // The main statement is the first top-level keyword after the CTEs, whose bodies
            // are all inside parentheses.
            start = tokens.FindIndex(1, t => t.Depth == 0 && t.IsWord() &&
                (QueryKeywords.Contains(t.Text) || t.IsWord("INSERT") || t.IsWord("UPDATE") || t.IsWord("DELETE")));
            if (start < 0)
                throw Refused("WITH");
        }

        var first = tokens[start];
        if (first.IsWord() && QueryKeywords.Contains(first.Text))
            return new SqlStatement { Sql = sql, Kind = SqlStatementKind.Query };
        if (first.IsWord() && WrappedQueryKeywords.Contains(first.Text))
            return new SqlStatement { Sql = sql, Kind = SqlStatementKind.Query, NeedsSubqueryWrap = true };

        var reader = new TokenReader(tokens, start + 1);
        switch (first.Text.ToUpperInvariant())
        {
            case "INSERT":
                reader.SkipWords("OR", "REPLACE", "IGNORE");
                reader.Expect("INTO");
                return Write(sql, SqlStatementKind.Insert, reader.ReadName());
            case "UPDATE":
                return Write(sql, SqlStatementKind.Update, reader.ReadName());
            case "DELETE":
                reader.Expect("FROM");
                return Write(sql, SqlStatementKind.Delete, reader.ReadName());
            case "MERGE":
                reader.Expect("INTO");
                return Write(sql, SqlStatementKind.Merge, reader.ReadName());
            case "TRUNCATE":
                reader.SkipWords("TABLE");
                return Write(sql, SqlStatementKind.Truncate, reader.ReadName());
            case "CREATE":
            {
                reader.SkipWords("OR", "REPLACE", "TEMP", "TEMPORARY");
                var kind = reader.ReadWord() switch
                {
                    "TABLE" => SqlStatementKind.CreateTable,
                    "VIEW" => SqlStatementKind.CreateView,
                    var other => throw new InvalidOperationException(
                        $"CREATE {other} isn't supported in the Query Hub. Use CREATE TABLE or CREATE VIEW.")
                };
                var ifNotExists = reader.TrySkipSequence("IF", "NOT", "EXISTS");
                return Write(sql, kind, reader.ReadName(), ifNotExists);
            }
            case "ALTER":
            {
                if (reader.ReadWord() != "TABLE")
                    throw new InvalidOperationException("Only ALTER TABLE is supported in the Query Hub.");
                reader.TrySkipSequence("IF", "EXISTS");
                var target = reader.ReadName();
                // RENAME TO renames the table; RENAME COLUMN a TO b is an ordinary ALTER.
                var rest = tokens.Skip(reader.Position).Where(t => t.Depth == 0).ToList();
                var rename = rest.FindIndex(t => t.IsWord("RENAME"));
                if (rename >= 0 && rename + 1 < rest.Count && rest[rename + 1].IsWord("TO"))
                    throw new InvalidOperationException(
                        "Renaming a table isn't supported in the Query Hub. Use CREATE TABLE new_name AS SELECT * FROM old_name, then DROP the old one.");
                return Write(sql, SqlStatementKind.AlterTable, target);
            }
            case "DROP":
            {
                var kind = reader.ReadWord() switch
                {
                    "TABLE" => SqlStatementKind.DropTable,
                    "VIEW" => SqlStatementKind.DropView,
                    var other => throw new InvalidOperationException(
                        $"DROP {other} isn't supported in the Query Hub. Use DROP TABLE or DROP VIEW.")
                };
                var ifExists = reader.TrySkipSequence("IF", "EXISTS");
                var target = reader.ReadName();
                if (reader.Peek().Text == ",")
                    throw new InvalidOperationException("Drop one table or view per statement.");
                return Write(sql, kind, target, ifExists);
            }
            default:
                throw Refused(first.Text);
        }
    }

    private static SqlStatement Write(string sql, SqlStatementKind kind, string target, bool ifClause = false)
        => new() { Sql = sql, Kind = kind, Target = target, IfExistsClause = ifClause };

    private static InvalidOperationException Refused(string keyword)
        => new($"{keyword.ToUpperInvariant()} statements aren't supported in the Query Hub. " +
               "It runs SELECT, INSERT, UPDATE, DELETE, MERGE, TRUNCATE, and CREATE/ALTER/DROP for tables and views.");

    private static void AddStatement(List<string> statements, StringBuilder current)
    {
        var text = current.ToString().Trim();
        current.Clear();
        if (Tokenize(text).Count > 0)
            statements.Add(text);
    }

    private static int SkipQuoted(string s, int start, char quote)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static int SkipLineComment(string s, int start)
    {
        var end = s.IndexOf('\n', start);
        return end < 0 ? s.Length : end;
    }

    private static int SkipBlockComment(string s, int start)
    {
        var end = s.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + 2;
    }

    private static int SkipDollarQuoted(string s, int start)
    {
        // $tag$ ... $tag$ (tag may be empty). A lone $ (a parameter like $1) is not a quote.
        var tagEnd = s.IndexOf('$', start + 1);
        if (tagEnd < 0) return start + 1;
        var tagName = s[(start + 1)..tagEnd];
        var validTag = tagName.Length == 0 ||
            (!char.IsDigit(tagName[0]) && tagName.All(ch => char.IsLetterOrDigit(ch) || ch == '_'));
        if (!validTag) return start + 1;
        var tag = s[start..(tagEnd + 1)];
        var close = s.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
        return close < 0 ? s.Length : close + tag.Length;
    }

    private readonly record struct Token(string Text, bool Quoted, int Depth)
    {
        public bool IsWord() => !Quoted && Text.Length > 0 && (char.IsLetter(Text[0]) || Text[0] == '_');
        public bool IsWord(string word) => IsWord() && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && next == '-') { i = SkipLineComment(sql, i); continue; }
            if (c == '/' && next == '*') { i = SkipBlockComment(sql, i); continue; }

            if (c == '"')
            {
                var end = SkipQuoted(sql, i, '"');
                var inner = sql[(i + 1)..Math.Max(i + 1, end - 1)].Replace("\"\"", "\"");
                tokens.Add(new Token(inner, true, depth));
                i = end;
                continue;
            }
            if (c == '\'')
            {
                var end = SkipQuoted(sql, i, '\'');
                tokens.Add(new Token("'", true, depth));
                i = end;
                continue;
            }
            if (c == '$')
            {
                var end = SkipDollarQuoted(sql, i);
                tokens.Add(new Token("$", true, depth));
                i = end;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(new Token(sql[start..i], false, depth));
                continue;
            }

            if (c == ')') depth = Math.Max(0, depth - 1);
            tokens.Add(new Token(c.ToString(), false, depth));
            if (c == '(') depth++;
            i++;
        }
        return tokens;
    }

    private sealed class TokenReader(List<Token> tokens, int position)
    {
        public int Position { get; private set; } = position;

        public Token Peek() => Position < tokens.Count ? tokens[Position] : new Token(string.Empty, false, 0);

        public string ReadWord()
        {
            var token = Peek();
            if (!token.IsWord())
                throw new InvalidOperationException("Couldn't read this statement. Check its syntax.");
            Position++;
            return token.Text.ToUpperInvariant();
        }

        public void Expect(string word)
        {
            if (!Peek().IsWord(word))
                throw new InvalidOperationException($"Expected {word} in this statement.");
            Position++;
        }

        public void SkipWords(params string[] words)
        {
            while (words.Any(w => Peek().IsWord(w)))
                Position++;
        }

        public bool TrySkipSequence(params string[] words)
        {
            for (var k = 0; k < words.Length; k++)
            {
                if (Position + k >= tokens.Count || !tokens[Position + k].IsWord(words[k]))
                    return false;
            }
            Position += words.Length;
            return true;
        }

        /// <summary>
        /// Reads a possibly qualified name and returns its last part. Only the session's own
        /// schemas are accepted, so a statement can't write somewhere the Hub doesn't track.
        /// </summary>
        public string ReadName()
        {
            var parts = new List<string>();
            while (true)
            {
                var token = Peek();
                if (!token.Quoted && !token.IsWord() || token.Text is "'" or "$")
                    throw new InvalidOperationException("Couldn't tell which table this statement changes.");
                parts.Add(token.Text);
                Position++;
                if (Peek().Text != ".")
                    break;
                Position++;
            }

            if (parts.Count > 1 && !parts.Take(parts.Count - 1).All(SessionSchemas.Contains))
                throw new InvalidOperationException(
                    $"The Query Hub can only change tables in this session, not {string.Join(".", parts)}.");

            return parts[^1];
        }
    }
}
