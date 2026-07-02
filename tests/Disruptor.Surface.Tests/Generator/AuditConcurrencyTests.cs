using Disruptor.Surface.Runtime;
using Disruptor.Surface.Tests.Runtime;
using Disruptor.Surreal;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// End-to-end coverage for the audit columns ([CreatedAt] / [UpdatedAt]) and the
/// optimistic-concurrency token ([Version]) — through REAL generation
/// (<see cref="GeneratorHarness.CompileAndLoad"/>) and the REAL
/// <see cref="SurrealSession.SaveAsync(IEntity, SurrealTransaction, CancellationToken)"/>
/// dispatch against a recording fake connection.
/// </summary>
public sealed class AuditConcurrencyTests
{
    private const string AuditModel = """
        using System;
        using Disruptor.Surface.Annotations;
        namespace M;

        [Table, AggregateRoot] public partial class Document {
            [Id] public partial DocumentId Id { get; set; }
            [Property] public partial string Title { get; set; }
            [CreatedAt, Property] public partial DateTimeOffset CreatedAtUtc { get; }
            [UpdatedAt, Property] public partial DateTimeOffset UpdatedAtUtc { get; }
            [Version, Property] public partial int Version { get; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    // ───────────────────────────── emission shape ────────────────────────────

    [Fact]
    public void Emits_AuditStamping_And_VersionGuardedUpdate()
    {
        var (result, _, _, compileDiags) = GeneratorHarness.Run(AuditModel);
        Assert.Empty(compileDiags);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // One instant read per dispatch; CREATE stamps created + version seed; every
        // dispatch stamps updated.
        Assert.Contains("var __now = ctx.UtcNow;", allSrc);
        Assert.Contains("_createdAtUtc = __now;", allSrc);
        Assert.Contains("_updatedAtUtc = __now;", allSrc);
        Assert.Contains("_version = 1;", allSrc);

        // UPDATE goes through the guarded SQL, not the SDK's UpsertAsync.
        Assert.Contains("UPDATE $_record_id CONTENT $_content WHERE version = $_expected_version;", allSrc);
        Assert.Contains("SurrealVersionConflictException(__id, __expectedVersion)", allSrc);
        Assert.DoesNotContain("UpsertAsync", allSrc);
    }

    [Fact]
    public void Schema_MapsAuditAndVersion_AsOrdinaryScalars()
    {
        var (result, _, _, _) = GeneratorHarness.Run(AuditModel);
        var schema = GeneratorHarness.FindGeneratedFile(result, "Workspace.Schema.g.cs");
        Assert.NotNull(schema);

        var src = schema!.ToString();
        Assert.Contains("DEFINE FIELD IF NOT EXISTS created_at_utc ON documents TYPE datetime;", src);
        Assert.Contains("DEFINE FIELD IF NOT EXISTS updated_at_utc ON documents TYPE datetime;", src);
        Assert.Contains("DEFINE FIELD IF NOT EXISTS version ON documents TYPE int DEFAULT 0;", src);
    }

    // ───────────────────────────── end-to-end save ───────────────────────────

    [Fact]
    public async Task E2E_Create_StampsBothAuditFields_AndDispatchesVersion1()
    {
        var (doc, docT, session, db, conn) = await LoadDocumentModelAsync();
        await using var tx = await db.BeginTransactionAsync();

        await session.SaveAsync(doc, tx);

        // Both audit fields carry the SAME instant (one ctx.UtcNow read), version is 1.
        var created = (DateTimeOffset)docT.GetProperty("CreatedAtUtc")!.GetValue(doc)!;
        var updated = (DateTimeOffset)docT.GetProperty("UpdatedAtUtc")!.GetValue(doc)!;
        Assert.NotEqual(default, created);
        Assert.Equal(created, updated);
        Assert.Equal(1, (int)docT.GetProperty("Version")!.GetValue(doc)!);

        // The wire saw a CREATE whose content already carries the stamped values.
        var (sql, vars) = SingleQueryRpc(conn, s => s.StartsWith("CREATE", StringComparison.Ordinal));
        Assert.Equal("CREATE $_record_id CONTENT $_content", sql);
        var content = Assert.IsType<SurrealObjectValue>(GetVar(vars, "_content"));
        Assert.Equal(1, HydrationValue.ReadOrDefault<int>(content, "version"));
        Assert.Equal(created, HydrationValue.ReadOrDefault<DateTimeOffset>(content, "created_at_utc"));
        Assert.Equal(created, HydrationValue.ReadOrDefault<DateTimeOffset>(content, "updated_at_utc"));
    }

    [Fact]
    public async Task E2E_Update_RefreshesOnlyUpdatedAt_AndBumpsVersion_ViaGuardedSql()
    {
        var (doc, docT, session, db, conn) = await LoadDocumentModelAsync();
        await using var tx = await db.BeginTransactionAsync();

        await session.SaveAsync(doc, tx);
        var createdAfterCreate = (DateTimeOffset)docT.GetProperty("CreatedAtUtc")!.GetValue(doc)!;

        // Second save on the (now tracked) entity dispatches the guarded UPDATE. The
        // responder confirms the guard matched (one updated row).
        conn.Responder = (method, @params, _) =>
            method == "query" && RpcSql(@params).StartsWith("UPDATE", StringComparison.Ordinal)
                ? MatchedUpdateResponse()
                : SurrealValue.None;
        await session.SaveAsync(doc, tx);

        // Created keeps its CREATE-time value; only updated refreshed; version bumped.
        var created = (DateTimeOffset)docT.GetProperty("CreatedAtUtc")!.GetValue(doc)!;
        var updated = (DateTimeOffset)docT.GetProperty("UpdatedAtUtc")!.GetValue(doc)!;
        Assert.Equal(createdAfterCreate, created);
        Assert.True(updated >= created);
        Assert.Equal(2, (int)docT.GetProperty("Version")!.GetValue(doc)!);
        Assert.False(session.IsClosed);

        // The wire saw the WHERE-guarded UPDATE with expected version 1 and content
        // version 2.
        var (sql, vars) = SingleQueryRpc(conn, s => s.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Equal("UPDATE $_record_id CONTENT $_content WHERE version = $_expected_version;", sql);
        Assert.Equal(1L, ReadInt64(GetVar(vars, "_expected_version")));
        var content = Assert.IsType<SurrealObjectValue>(GetVar(vars, "_content"));
        Assert.Equal(2, HydrationValue.ReadOrDefault<int>(content, "version"));
    }

    [Fact]
    public async Task E2E_VersionConflict_ThrowsAndClosesSession()
    {
        var (doc, _, session, db, conn) = await LoadDocumentModelAsync();
        await using var tx = await db.BeginTransactionAsync();

        await session.SaveAsync(doc, tx);

        // Guard matches no row — the substrate answers the UPDATE with an empty result
        // set (a competing writer bumped the version since load).
        conn.Responder = (method, @params, _) =>
            method == "query" && RpcSql(@params).StartsWith("UPDATE", StringComparison.Ordinal)
                ? EmptyUpdateResponse()
                : SurrealValue.None;

        var ex = await Assert.ThrowsAsync<SurrealVersionConflictException>(() => session.SaveAsync(doc, tx));
        Assert.Equal(doc.Id, ex.EntityId);
        Assert.Equal(1L, ex.ExpectedVersion);

        // The throw propagated through SurrealSession.SaveAsync's fail-closed catch.
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task SaveContext_UtcNow_IsInjectable_ForDeterministicStamps()
    {
        // The seam: ISaveContext.UtcNow. A custom context pins the clock, so the
        // stamped values are exactly assertable (no wall-clock tolerance games).
        var asm = GeneratorHarness.CompileAndLoad(AuditModel);
        var doc = (IEntity)Activator.CreateInstance(asm.GetType("M.Document", throwOnError: true)!)!;
        var docT = doc.GetType();

        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var t1 = new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero);
        await doc.SaveAsync(new FixedClockSaveContext(tx, t1, tracked: false), CancellationToken.None);
        Assert.Equal(t1, (DateTimeOffset)docT.GetProperty("CreatedAtUtc")!.GetValue(doc)!);
        Assert.Equal(t1, (DateTimeOffset)docT.GetProperty("UpdatedAtUtc")!.GetValue(doc)!);
        Assert.Equal(1, (int)docT.GetProperty("Version")!.GetValue(doc)!);

        // UPDATE pass with a later pinned instant: created keeps t1, updated becomes t2.
        conn.Responder = (method, @params, _) =>
            method == "query" && RpcSql(@params).StartsWith("UPDATE", StringComparison.Ordinal)
                ? MatchedUpdateResponse()
                : SurrealValue.None;
        var t2 = t1.AddMinutes(42);
        await doc.SaveAsync(new FixedClockSaveContext(tx, t2, tracked: true), CancellationToken.None);
        Assert.Equal(t1, (DateTimeOffset)docT.GetProperty("CreatedAtUtc")!.GetValue(doc)!);
        Assert.Equal(t2, (DateTimeOffset)docT.GetProperty("UpdatedAtUtc")!.GetValue(doc)!);
        Assert.Equal(2, (int)docT.GetProperty("Version")!.GetValue(doc)!);
    }

    // ───────────────────────────── helpers ───────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ISaveContext"/> for driving an entity's emitted SaveAsync
    /// directly with a pinned clock. <c>UtcNow</c> overrides the interface's default
    /// member; <c>tracked</c> selects the CREATE vs UPDATE dispatch branch.
    /// </summary>
    private sealed class FixedClockSaveContext(SurrealTransaction tx, DateTimeOffset now, bool tracked) : ISaveContext
    {
        public SurrealTransaction Transaction => tx;
        public DateTimeOffset UtcNow => now;
        public bool IsTracked(IRecordId id) => tracked;
        public Task SaveAsync(IEntity entity, CancellationToken ct) => entity.SaveAsync(this, ct);
        public void MarkSaved(IEntity entity) { }
    }

    private static async Task<(IEntity Doc, Type DocT, SurrealSession Session, SurrealClient Db, FakeSurreal.RecordingConnection Conn)> LoadDocumentModelAsync()
    {
        var asm = GeneratorHarness.CompileAndLoad(AuditModel);
        var registry = (IReferenceRegistry)asm.GetType("M.Workspace", throwOnError: true)!
            .GetProperty("ReferenceRegistry")!.GetValue(null)!;
        var session = new SurrealSession(registry);

        var docT = asm.GetType("M.Document", throwOnError: true)!;
        var doc = (IEntity)Activator.CreateInstance(docT)!;
        docT.GetProperty("Title")!.SetValue(doc, "spec");

        var (db, conn) = FakeSurreal.NullWithRecording();
        await Task.CompletedTask;
        return (doc, docT, session, db, conn);
    }

    /// <summary>The wire result for a guarded UPDATE whose WHERE matched: one row.</summary>
    private static SurrealValue MatchedUpdateResponse() =>
        StatementList(new SurrealListValue([new SurrealObjectValue(new SurrealObject())]));

    /// <summary>The wire result for a guarded UPDATE whose WHERE matched nothing: empty array.</summary>
    private static SurrealValue EmptyUpdateResponse() =>
        StatementList(new SurrealListValue([]));

    private static SurrealValue StatementList(SurrealValue result) =>
        new SurrealListValue([
            new SurrealObjectValue(new SurrealObject
            {
                ["status"] = "OK",
                ["result"] = result,
            })
        ]);

    private static string RpcSql(SurrealValue? @params) =>
        @params is SurrealListValue { List: { Count: > 0 } list } && list[0] is StringSurrealValue s
            ? s.Value
            : string.Empty;

    /// <summary>Finds the single recorded "query" RPC whose SQL satisfies <paramref name="sqlPredicate"/>.</summary>
    private static (string Sql, SurrealObject Vars) SingleQueryRpc(
        FakeSurreal.RecordingConnection conn, Func<string, bool> sqlPredicate)
    {
        var hit = Assert.Single(conn.Sent, s => s.Method == "query" && sqlPredicate(RpcSql(s.Params)));
        var list = Assert.IsType<SurrealListValue>(hit.Params);
        var sql = Assert.IsType<StringSurrealValue>(list.List[0]).Value;
        var vars = Assert.IsType<SurrealObjectValue>(list.List[1]).Object;
        return (sql, vars);
    }

    private static SurrealValue GetVar(SurrealObject vars, string key)
    {
        Assert.True(vars.TryGetValue(key, out var value), $"binding '{key}' missing");
        return value!;
    }

    private static long ReadInt64(SurrealValue value) =>
        HydrationValue.ReadOrDefault<long>(new SurrealObjectValue(new SurrealObject { ["v"] = value }), "v");
}
