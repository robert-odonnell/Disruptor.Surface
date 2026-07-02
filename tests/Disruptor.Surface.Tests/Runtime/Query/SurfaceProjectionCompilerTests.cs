using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// Pinned-SQL coverage for <c>SurfaceQueryCompiler.CompileProjection</c> via the public
/// <see cref="ProjectionQuery{T,TRow}.Compile"/> surface. The load-bearing invariant:
/// ORDER BY fields must appear in the SELECT list — SurrealDB 3.x rejects
/// <c>ORDER BY x</c> when <c>x</c> isn't projected ("Missing order idiom"), so the
/// compiler widens the projection with any missing order fields. The reader path
/// (<c>ValueProjectionRow</c>) looks fields up by name, so the extra columns are
/// wire-only and never surface in the materialised rows.
/// </summary>
public sealed class SurfaceProjectionCompilerTests
{
    /// <summary>Projection target — probe-safe positional record (accepts default values).</summary>
    private sealed record SymbolRow(string Name, int Line);

    private static readonly ISurfaceProjection<SymbolRow> Projection =
        SurfaceProjection.For<SymbolRow>(row => new SymbolRow(
            Name: row.Read(new PropertyExpr<string>("name")),
            Line: row.Read(new PropertyExpr<int>("line"))));

    [Fact]
    public void CompileProjection_Plain_SelectsDiscoveredFieldsOnly()
    {
        var (sql, bindings) = new SurfaceQuery<TestTable>("symbols")
            .Select(Projection)
            .Compile();

        Assert.Equal("SELECT name, line FROM symbols;", sql);
        Assert.Empty(bindings);
    }

    [Fact]
    public void CompileProjection_WithWhere_AppendsPredicateAndBinds()
    {
        var (sql, bindings) = new SurfaceQuery<TestTable>("symbols")
            .Select(Projection)
            .Where(new PropertyExpr<string>("kind").Eq("method"))
            .Compile();

        Assert.Equal("SELECT name, line FROM symbols WHERE kind = $_p0;", sql);
        Assert.Equal(new StringSurrealValue("method"), bindings["_p0"]);
    }

    [Fact]
    public void CompileProjection_OrderByNonProjectedField_WidensSelectList()
    {
        // The fix: `kind` isn't in the projection's field list, so it's appended to the
        // SELECT — otherwise SurrealDB 3.x rejects the statement with "Missing order
        // idiom `kind` in statement selection".
        var (sql, _) = new SurfaceQuery<TestTable>("symbols")
            .Select(Projection)
            .OrderBy(new PropertyExpr<string>("kind"))
            .Compile();

        Assert.Equal("SELECT name, line, kind FROM symbols ORDER BY kind ASC;", sql);
    }

    [Fact]
    public void CompileProjection_OrderByProjectedField_DoesNotDuplicateColumn()
    {
        var (sql, _) = new SurfaceQuery<TestTable>("symbols")
            .Select(Projection)
            .OrderBy(new PropertyExpr<string>("name"), OrderDirection.Descending)
            .Compile();

        Assert.Equal("SELECT name, line FROM symbols ORDER BY name DESC;", sql);
    }

    [Fact]
    public void CompileProjection_MixedOrderFields_WidensOnlyMissingOnes_Deduplicated()
    {
        // `name` is projected (no widening), `kind` isn't (widened once even though it
        // appears in two order clauses).
        var (sql, _) = new SurfaceQuery<TestTable>("symbols")
            .Select(Projection)
            .OrderBy(new PropertyExpr<string>("kind"))
            .ThenBy(new PropertyExpr<string>("name"))
            .ThenBy(new PropertyExpr<string>("kind"), OrderDirection.Descending)
            .Compile();

        Assert.Equal(
            "SELECT name, line, kind FROM symbols ORDER BY kind ASC, name ASC, kind DESC;",
            sql);
    }

    [Fact]
    public void CompileProjection_FullClauseStack_RendersInCanonicalOrder()
    {
        // WHERE → ORDER BY → LIMIT → START, with the order field widened into SELECT.
        var (sql, bindings) = new SurfaceQuery<TestTable>("symbols")
            .Select(Projection)
            .Where(new PropertyExpr<string>("kind").Eq("method"))
            .OrderBy(new PropertyExpr<int>("priority"))
            .Limit(5)
            .Start(10)
            .Compile();

        Assert.Equal(
            "SELECT name, line, priority FROM symbols WHERE kind = $_p0 ORDER BY priority ASC LIMIT 5 START 10;",
            sql);
        Assert.Equal(new StringSurrealValue("method"), bindings["_p0"]);
    }

    /// <summary>Test stand-in for a generated entity — minimal IEntity shape so SurfaceQuery&lt;T&gt; can construct.</summary>
    private sealed class TestTable : IEntity
    {
        public RecordId Id => default;
        public SurrealSession? Session => null;
        public void Bind(SurrealSession session) { }
        public void Initialize(SurrealSession session) { }
        public void OnDeleting() { }
        public void MarkAllSlicesLoaded(IHydrationSink sink) { }
    }
}
