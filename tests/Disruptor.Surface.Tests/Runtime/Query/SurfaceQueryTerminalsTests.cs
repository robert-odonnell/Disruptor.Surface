using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Disruptor.Surface.Tests.Runtime;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// Behavioral coverage for the scalar/single-row terminals on <see cref="SurfaceQuery{T}"/>
/// and <see cref="ProjectionQuery{T, TRow}"/> — <c>ExistsAsync</c>, <c>FirstOrDefaultAsync</c>,
/// <c>SingleAsync</c>, <c>SingleOrDefaultAsync</c> — via the FakeSurreal recording pattern:
/// the responder scripts the row set, the recorded RPC pins the dispatched SQL, and the
/// assertions cover the 0/1/2-row materialise semantics. Includes the regression test for
/// the ExecuteIntoSessionAsync row/entity pairing desync (non-object row mid-response).
/// </summary>
public sealed class SurfaceQueryTerminalsTests
{
    // ─────────────────────── ExistsAsync ───────────────────────

    [Fact]
    public async Task ExistsAsync_NoMatchingRows_ReturnsFalse_AndDispatchesIdLimitOneProbe()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([]))
            : SurrealValue.None;

        var exists = await new SurfaceQuery<TestEntity>("symbols")
            .Where(new PropertyExpr<string>("kind").Eq("method"))
            .ExistsAsync(db);

        Assert.False(exists);
        var (sql, bindings) = ExtractQueryParts(conn.Sent.Single(s => s.Method == "query").Params);
        Assert.Equal("SELECT id FROM symbols WHERE kind = $_p0 LIMIT 1;", sql);
        Assert.Equal(new StringSurrealValue("method"), bindings["_p0"]);
    }

    [Fact]
    public async Task ExistsAsync_AtLeastOneRow_ReturnsTrue()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([IdOnlyRow("symbols", "a")]))
            : SurrealValue.None;

        var exists = await new SurfaceQuery<TestEntity>("symbols").ExistsAsync(db);

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_TransactionOverload_DispatchesSameProbe()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method switch
        {
            "query" => WrapAsQueryResponse(new SurrealListValue([IdOnlyRow("symbols", "a")])),
            "begin" => new SurrealUuidValue(Guid.NewGuid()),
            _ => SurrealValue.None,
        };

        await using var tx = await db.BeginTransactionAsync();
        var exists = await new SurfaceQuery<TestEntity>("symbols").ExistsAsync(tx);

        Assert.True(exists);
        var (sql, _) = ExtractQueryParts(conn.Sent.Single(s => s.Method == "query").Params);
        Assert.Equal("SELECT id FROM symbols LIMIT 1;", sql);
    }

    // ─────────────────────── FirstOrDefaultAsync ───────────────────────

    [Fact]
    public async Task FirstOrDefaultAsync_EmptyResult_ReturnsNull()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([]))
            : SurrealValue.None;

        var first = await new SurfaceQuery<TestEntity>("symbols").FirstOrDefaultAsync(db);

        Assert.Null(first);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_OneRow_HydratesEntity_AndOverridesUserLimitWithOne()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([NamedRow("symbols", "a", "alpha")]))
            : SurrealValue.None;

        var first = await new SurfaceQuery<TestEntity>("symbols")
            .OrderBy(new PropertyExpr<string>("name"))
            .Limit(50)
            .Start(10)
            .FirstOrDefaultAsync(db);

        Assert.NotNull(first);
        Assert.Equal("alpha", first.Name);
        // Entity SELECT shape with the user's LIMIT overridden to 1; ORDER BY and START
        // are preserved (first-of-an-ordering / paged-first stay meaningful).
        var (sql, _) = ExtractQueryParts(conn.Sent.Single(s => s.Method == "query").Params);
        Assert.Equal("SELECT * FROM symbols ORDER BY name ASC LIMIT 1 START 10;", sql);
    }

    // ─────────────────────── SingleAsync / SingleOrDefaultAsync ───────────────────────

    [Fact]
    public async Task SingleAsync_ExactlyOneRow_ReturnsIt_AndProbesWithLimitTwo()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([NamedRow("symbols", "a", "alpha")]))
            : SurrealValue.None;

        var single = await new SurfaceQuery<TestEntity>("symbols")
            .Where(new PropertyExpr<string>("name").Eq("alpha"))
            .SingleAsync(db);

        Assert.Equal("alpha", single.Name);
        var (sql, _) = ExtractQueryParts(conn.Sent.Single(s => s.Method == "query").Params);
        Assert.Equal("SELECT * FROM symbols WHERE name = $_p0 LIMIT 2;", sql);
    }

    [Fact]
    public async Task SingleAsync_NoRows_ThrowsNamingTheTable()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([]))
            : SurrealValue.None;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SurfaceQuery<TestEntity>("symbols").SingleAsync(db));

        Assert.Contains("'symbols'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no rows", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleAsync_TwoRows_ThrowsMoreThanOne()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue(
            [
                NamedRow("symbols", "a", "alpha"),
                NamedRow("symbols", "b", "beta"),
            ]))
            : SurrealValue.None;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SurfaceQuery<TestEntity>("symbols").SingleAsync(db));

        Assert.Contains("more than one row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_NoRows_ReturnsNull()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([]))
            : SurrealValue.None;

        var single = await new SurfaceQuery<TestEntity>("symbols").SingleOrDefaultAsync(db);

        Assert.Null(single);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_TwoRows_StillThrows()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue(
            [
                NamedRow("symbols", "a", "alpha"),
                NamedRow("symbols", "b", "beta"),
            ]))
            : SurrealValue.None;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SurfaceQuery<TestEntity>("symbols").SingleOrDefaultAsync(db));
    }

    // ─────────────────────── Projection terminals ───────────────────────

    private sealed record NameRow(string Name);

    private static readonly ISurfaceProjection<NameRow> NameProjection =
        SurfaceProjection.For<NameRow>(row => new NameRow(row.Read(new PropertyExpr<string>("name"))));

    [Fact]
    public async Task Projection_FirstOrDefaultAsync_EmptyResult_ReturnsDefault()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([]))
            : SurrealValue.None;

        var first = await new SurfaceQuery<TestEntity>("symbols")
            .Select(NameProjection)
            .FirstOrDefaultAsync(db);

        Assert.Null(first);
    }

    [Fact]
    public async Task Projection_FirstOrDefaultAsync_OneRow_Materialises_AndCompilesLimitOne()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([NamedRow("symbols", "a", "alpha")]))
            : SurrealValue.None;

        var first = await new SurfaceQuery<TestEntity>("symbols")
            .Select(NameProjection)
            .Limit(25)
            .FirstOrDefaultAsync(db);

        Assert.Equal(new NameRow("alpha"), first);
        var (sql, _) = ExtractQueryParts(conn.Sent.Single(s => s.Method == "query").Params);
        Assert.Equal("SELECT name FROM symbols LIMIT 1;", sql);
    }

    [Fact]
    public async Task Projection_SingleAsync_TwoRows_Throws_AndCompilesLimitTwo()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue(
            [
                NamedRow("symbols", "a", "alpha"),
                NamedRow("symbols", "b", "beta"),
            ]))
            : SurrealValue.None;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SurfaceQuery<TestEntity>("symbols").Select(NameProjection).SingleAsync(db));

        Assert.Contains("more than one row", ex.Message, StringComparison.Ordinal);
        var (sql, _) = ExtractQueryParts(conn.Sent.Single(s => s.Method == "query").Params);
        Assert.Equal("SELECT name FROM symbols LIMIT 2;", sql);
    }

    [Fact]
    public async Task Projection_SingleAsync_OneRow_ReturnsIt()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([NamedRow("symbols", "a", "alpha")]))
            : SurrealValue.None;

        var single = await new SurfaceQuery<TestEntity>("symbols")
            .Select(NameProjection)
            .SingleAsync(db);

        Assert.Equal(new NameRow("alpha"), single);
    }

    [Fact]
    public async Task Projection_SingleOrDefaultAsync_NoRows_ReturnsDefault_TwoRows_Throws()
    {
        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([]))
            : SurrealValue.None;

        var none = await new SurfaceQuery<TestEntity>("symbols")
            .Select(NameProjection)
            .SingleOrDefaultAsync(db);
        Assert.Null(none);

        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue(
            [
                NamedRow("symbols", "a", "alpha"),
                NamedRow("symbols", "b", "beta"),
            ]))
            : SurrealValue.None;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SurfaceQuery<TestEntity>("symbols").Select(NameProjection).SingleOrDefaultAsync(db));
    }

    // ─────────────────────── Row/entity pairing regression ───────────────────────

    [Fact]
    public async Task ExecuteAsync_NonObjectRowMidResponse_KeepsEntityAndNestedRowPairingAligned()
    {
        // Regression: the nested-hydration pass used to index the raw row array with the
        // filtered entity index — one NONE row mid-response shifted every subsequent
        // entity onto its neighbour's row, so the last entity's children were never
        // hydrated. Both children must be seen, in order.
        var hydratedChildren = new List<string>();
        var include = new IncludeChildrenNode(
            ChildTable: "constraints",
            ParentField: "design",
            Filter: null,
            Nested: [],
            Hydrator: (row, _) =>
            {
                if (row is SurrealObjectValue obj)
                {
                    hydratedChildren.Add(HydrationValue.ReadString(obj, "name"));
                }
            },
            ParentSliceKey: "constraints");

        var rowA = new SurrealObjectValue(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(new SurrealRecordId("designs", "a")),
            ["name"] = "designA",
            ["constraints"] = new SurrealListValue(
            [
                NamedRow("constraints", "ca", "childA"),
            ]),
        });
        var rowB = new SurrealObjectValue(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(new SurrealRecordId("designs", "b")),
            ["name"] = "designB",
            ["constraints"] = new SurrealListValue(
            [
                NamedRow("constraints", "cb", "childB"),
            ]),
        });

        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method == "query"
            ? WrapAsQueryResponse(new SurrealListValue([rowA, SurrealValue.None, rowB]))
            : SurrealValue.None;

        var entities = await new SurfaceQuery<TestEntity>("designs")
            .WithInclude(include)
            .ExecuteAsync(db);

        Assert.Equal(2, entities.Count);
        Assert.Equal("designA", entities[0].Name);
        Assert.Equal("designB", entities[1].Name);
        Assert.Equal(["childA", "childB"], hydratedChildren);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Test stand-in for a generated entity: minimal IEntity shape plus a Hydrate body
    /// that captures the row's <c>name</c> so tests can tell which row produced which
    /// entity.
    /// </summary>
    private sealed class TestEntity : IEntity
    {
        public string Name { get; private set; } = "";
        public RecordId Id => default;
        public SurrealSession? Session => null;
        public void Bind(SurrealSession session) { }
        public void Initialize(SurrealSession session) { }
        public void OnDeleting() { }
        public void MarkAllSlicesLoaded(IHydrationSink sink) { }

        void IEntity.Hydrate(SurrealValue row, IHydrationSink sink)
        {
            if (row is SurrealObjectValue obj)
            {
                Name = HydrationValue.ReadString(obj, "name");
            }
        }
    }

    private static SurrealObjectValue NamedRow(string table, string key, string name)
        => new(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(new SurrealRecordId(table, key)),
            ["name"] = name,
        });

    private static SurrealObjectValue IdOnlyRow(string table, string key)
        => new(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(new SurrealRecordId(table, key)),
        });

    private static (string Sql, SurrealObject Bindings) ExtractQueryParts(SurrealValue? @params)
    {
        var list = Assert.IsType<SurrealListValue>(@params);
        var sql = Assert.IsType<StringSurrealValue>(list.List[0]).Value;
        var bindings = Assert.IsType<SurrealObjectValue>(list.List[1]).Object;
        return (sql, bindings);
    }

    private static SurrealValue WrapAsQueryResponse(SurrealValue rows)
        => new SurrealListValue(
        [
            new SurrealObjectValue(new SurrealObject
            {
                ["status"] = "OK",
                ["time"] = "1ms",
                ["result"] = rows,
            }),
        ]);
}
