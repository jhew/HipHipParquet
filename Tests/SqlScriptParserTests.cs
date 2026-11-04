using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public sealed class SqlScriptParserTests
{
    [Fact]
    public void SplitStatements_IgnoresSemicolonsInStringsQuotedNamesAndComments()
    {
        var statements = SqlScriptParser.SplitStatements("""
            UPDATE t SET note = 'a;b' WHERE "odd;name" = 1; -- trailing; comment
            /* block; comment */ DELETE FROM t WHERE x = $$c;d$$;
            ;
            SELECT 1
            """);

        Assert.Equal(3, statements.Count);
        Assert.StartsWith("UPDATE t SET note = 'a;b'", statements[0]);
        Assert.Contains("$$c;d$$", statements[1]);
        Assert.Equal("SELECT 1", statements[2]);
    }

    [Theory]
    [InlineData("SELECT * FROM sales", SqlStatementKind.Query, null)]
    [InlineData("FROM sales LIMIT 5", SqlStatementKind.Query, null)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", SqlStatementKind.Query, null)]
    [InlineData("INSERT INTO sales VALUES (1)", SqlStatementKind.Insert, "sales")]
    [InlineData("insert or replace into \"Sales Data\" select 1", SqlStatementKind.Insert, "Sales Data")]
    [InlineData("UPDATE main.sales SET a = 1", SqlStatementKind.Update, "sales")]
    [InlineData("WITH old AS (SELECT 1) DELETE FROM sales WHERE id IN (SELECT * FROM old)", SqlStatementKind.Delete, "sales")]
    [InlineData("TRUNCATE TABLE sales", SqlStatementKind.Truncate, "sales")]
    [InlineData("CREATE OR REPLACE TEMP TABLE eu AS SELECT 1", SqlStatementKind.CreateTable, "eu")]
    [InlineData("CREATE VIEW IF NOT EXISTS v AS SELECT 1", SqlStatementKind.CreateView, "v")]
    [InlineData("ALTER TABLE sales ADD COLUMN note VARCHAR", SqlStatementKind.AlterTable, "sales")]
    [InlineData("ALTER TABLE sales RENAME COLUMN a TO b", SqlStatementKind.AlterTable, "sales")]
    [InlineData("DROP TABLE IF EXISTS sales", SqlStatementKind.DropTable, "sales")]
    [InlineData("DROP VIEW v", SqlStatementKind.DropView, "v")]
    public void Classify_FindsKindAndTarget(string sql, SqlStatementKind kind, string? target)
    {
        var statement = SqlScriptParser.Classify(sql);
        Assert.Equal(kind, statement.Kind);
        Assert.Equal(target, statement.Target);
    }

    [Theory]
    [InlineData("DESCRIBE sales")]
    [InlineData("SUMMARIZE sales")]
    [InlineData("SHOW TABLES")]
    public void Classify_WrapsQueriesDuckDbOnlyAcceptsAsSubqueries(string sql)
    {
        var statement = SqlScriptParser.Classify(sql);
        Assert.Equal(SqlStatementKind.Query, statement.Kind);
        Assert.True(statement.NeedsSubqueryWrap);
    }

    [Theory]
    [InlineData("COPY sales TO 'out.csv'")]
    [InlineData("ATTACH 'other.db'")]
    [InlineData("INSTALL httpfs")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("CREATE MACRO m(x) AS x")]
    [InlineData("ALTER TABLE sales RENAME TO sales2")]
    [InlineData("DROP TABLE a, b")]
    [InlineData("UPDATE other_db.main.sales SET a = 1")]
    public void Classify_RefusesStatementsOutsideTheSession(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => SqlScriptParser.Classify(sql));
    }
}
