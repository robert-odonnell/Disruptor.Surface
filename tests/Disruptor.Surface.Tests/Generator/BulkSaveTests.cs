using Disruptor.Surface.Runtime;
using Disruptor.Surface.Tests.Runtime;
using Disruptor.Surreal;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// End-to-end coverage for the batch write surface —
/// <c>SurrealSession.SaveAsync(IEnumerable&lt;IEntity&gt;, SurrealTransaction, ct)</c> —
/// through REAL generation (<see cref="GeneratorHarness.CompileAndLoad"/>) and the REAL
/// session dispatch against a recording fake connection. Pins the wire contract:
/// contiguous same-table plain CREATEs coalesce into one
/// <c>INSERT INTO $_table $_records</c> statement (CBOR array binding, per-record id
/// embedded); everything else (UPSERT, [Version]-guarded UPDATE) dispatches immediately
/// in original order after flushing the buffer; a run of one flushes as the
/// single-save-identical <c>CREATE $_record_id CONTENT $_content</c>.
/// </summary>
public sealed class BulkSaveTests
{
    private const string TwoTableModel = """
        using Disruptor.Surface.Annotations;
        namespace M;

        [Table, AggregateRoot] public partial class Alpha {
            [Id] public partial AlphaId Id { get; set; }
            [Property] public partial string Name { get; set; }
        }

        [Table, AggregateRoot] public partial class Widget {
            [Id] public partial WidgetId Id { get; set; }
            [Property] public partial string Label { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    private const string ParentChildModel = """
        using System.Collections.Generic;
        using Disruptor.Surface.Annotations;
        namespace M;

        [Table, AggregateRoot] public partial class Root {
            [Id] public partial RootId Id { get; set; }
            [Property] public partial string Name { get; set; }
            [Children] public partial IReadOnlyCollection<Leaf> Leaves { get; }
        }

        [Table] public partial class Leaf {
            [Id] public partial LeafId Id { get; set; }
            [Parent] public partial Root Root { get; set; }
            [Property] public partial string Label { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    private const string VersionedModel = """
        using Disruptor.Surface.Annotations;
        namespace M;

        [Table, AggregateRoot] public partial class Doc {
            [Id] public partial DocId Id { get; set; }
            [Property] public partial string Title { get; set; }
            [Version, Property] public partial int Version { get; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    private const string MandatoryRefModel = """
        using Disruptor.Surface.Annotations;
        namespace M;

        [Table, AggregateRoot] public partial class Owner {
            [Id] public partial OwnerId Id { get; set; }
            [Property] public partial string Name { get; set; }
            [Reference] public partial Profile Profile { get; }
        }

        [Table, AggregateRoot] public partial class Profile {
            [Id] public partial ProfileId Id { get; set; }
            [Property] public partial string Bio { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    // ───────────────────────────── emission shape ────────────────────────────

    [Fact]
    public void Emits_EntitySaveAsync_DispatchingThroughSaveContextSeam()
    {
        // Every wire send in the emitted body routes through the ISaveContext seam
        // (whose defaults are the previous direct ctx.Transaction calls), so the
        // batch context can substitute buffering without generator knowledge.
        var (result, _, _, compileDiags) = GeneratorHarness.Run(TwoTableModel);
        Assert.Empty(compileDiags);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("await ctx.DispatchCreateAsync(__id, __content, ct);", allSrc);
        Assert.Contains("await ctx.DispatchUpsertAsync(__id, __content, ct);", allSrc);
        Assert.DoesNotContain("ctx.Transaction.CreateAsync", allSrc);
        Assert.DoesNotContain("ctx.Transaction.UpsertAsync", allSrc);

        // The [Version]-guarded UPDATE goes through the raw-query seam (it inspects
        // the response for the empty-result conflict) — never the upsert path.
        var (vResult, _, _, vDiags) = GeneratorHarness.Run(VersionedModel);
        Assert.Empty(vDiags);
        var vSrc = GeneratorHarness.AllGeneratedSource(vResult);
        Assert.Contains("var __versionResponse = await ctx.DispatchQueryAsync(__versionSql, __versionBindings, ct);", vSrc);
        Assert.DoesNotContain("DispatchUpsertAsync", vSrc);
    }

    // ───────────────────────────── batching shape ────────────────────────────

    [Fact]
    public async Task Batch_NewEntitiesAcrossTwoTables_GroupsCreatesPerTableIntoInsertStatements()
    {
        var (asm, session) = LoadModel(TwoTableModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var a1 = NewEntity(asm, "M.Alpha", "a_one", "Name", "first");
        var a2 = NewEntity(asm, "M.Alpha", "a_two", "Name", "second");
        var b1 = NewEntity(asm, "M.Widget", "b_one", "Label", "third");
        var b2 = NewEntity(asm, "M.Widget", "b_two", "Label", "fourth");

        await session.SaveAsync(new[] { a1, a2, b1, b2 }, tx);

        // Exactly two wire statements: one INSERT per table, in first-create order.
        // No CREATE statements at all — every create was batched.
        var rpcs = QueryRpcs(conn);
        Assert.Equal(2, rpcs.Count);
        Assert.All(rpcs, r => Assert.Equal("INSERT INTO $_table $_records", r.Sql));
        Assert.DoesNotContain(rpcs, r => r.Sql.StartsWith("CREATE", StringComparison.Ordinal));

        // First statement: alphas, records in batch order with ids embedded.
        Assert.Equal("alphas", GetVar(rpcs[0].Vars, "_table").ToString());
        var alphaRecords = RecordsOf(rpcs[0].Vars);
        Assert.Equal(2, alphaRecords.Count);
        Assert.Equal("alphas:a_one", alphaRecords[0]["id"].ToString());
        Assert.Equal("first", HydrationValue.ReadOrDefault<string>(new SurrealObjectValue(alphaRecords[0]), "name"));
        Assert.Equal("alphas:a_two", alphaRecords[1]["id"].ToString());
        Assert.Equal("second", HydrationValue.ReadOrDefault<string>(new SurrealObjectValue(alphaRecords[1]), "name"));

        // Second statement: widgets.
        Assert.Equal("widgets", GetVar(rpcs[1].Vars, "_table").ToString());
        var widgetRecords = RecordsOf(rpcs[1].Vars);
        Assert.Equal(2, widgetRecords.Count);
        Assert.Equal("widgets:b_one", widgetRecords[0]["id"].ToString());
        Assert.Equal("widgets:b_two", widgetRecords[1]["id"].ToString());

        // MarkSaved ran per entity: all four are tracked; session stays open.
        Assert.True(session.IsTracked(a1.Id));
        Assert.True(session.IsTracked(a2.Id));
        Assert.True(session.IsTracked(b1.Id));
        Assert.True(session.IsTracked(b2.Id));
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task Batch_InterleavedTables_FlushesPerRun_DegeneratingToSingleSaveCreates()
    {
        // The pinned flush policy: the buffer only survives across CONSECUTIVE
        // same-table creates. Interleaving tables produces runs of one, and a run of
        // one dispatches as the single-save-identical CREATE statement.
        var (asm, session) = LoadModel(TwoTableModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var a1 = NewEntity(asm, "M.Alpha", "a_one", "Name", "first");
        var b1 = NewEntity(asm, "M.Widget", "b_one", "Label", "second");
        var a2 = NewEntity(asm, "M.Alpha", "a_two", "Name", "third");

        await session.SaveAsync(new[] { a1, b1, a2 }, tx);

        var rpcs = QueryRpcs(conn);
        Assert.Equal(3, rpcs.Count);
        Assert.All(rpcs, r => Assert.Equal("CREATE $_record_id CONTENT $_content", r.Sql));
        Assert.Equal("alphas:a_one", GetVar(rpcs[0].Vars, "_record_id").ToString());
        Assert.Equal("widgets:b_one", GetVar(rpcs[1].Vars, "_record_id").ToString());
        Assert.Equal("alphas:a_two", GetVar(rpcs[2].Vars, "_record_id").ToString());
    }

    // ─────────────────────── equivalence with single saves ───────────────────

    [Fact]
    public async Task MixedBatch_ProducesSameWireTrafficAndState_AsEquivalentSingleSaves()
    {
        // Mixed batch: a new root with a new child (children walk crosses tables, so
        // each create is a run of one) + an already-persisted root with a pending
        // update. Wire traffic and session state must be exactly what the equivalent
        // sequence of single SaveAsync calls produces.
        var asm = GeneratorHarness.CompileAndLoad(ParentChildModel);

        var (batchTraffic, batchLog) = await RunMixedScenarioAsync(asm, batched: true);
        var (singleTraffic, singleLog) = await RunMixedScenarioAsync(asm, batched: false);

        Assert.Equal(singleTraffic, batchTraffic);
        Assert.Equal(singleLog, batchLog);

        // And the shared expectation, pinned explicitly so this test can't pass
        // vacuously if both paths drift together: CREATE new root, CREATE new child
        // (dependency order), UPSERT the existing root.
        Assert.Equal(3, batchTraffic.Count);
        Assert.StartsWith("query [CREATE $_record_id CONTENT $_content", batchTraffic[0]);
        Assert.Contains("roots:fresh", batchTraffic[0]);
        Assert.StartsWith("query [CREATE $_record_id CONTENT $_content", batchTraffic[1]);
        Assert.Contains("leaves:leaf_one", batchTraffic[1]);
        Assert.StartsWith("query [UPSERT $_record_id CONTENT $_content", batchTraffic[2]);
        Assert.Contains("roots:existing", batchTraffic[2]);
    }

    /// <summary>
    /// One run of the mixed scenario: pre-persist a root, then save (batched or as
    /// singles) a new tracked root carrying a new child plus the mutated existing root.
    /// Returns the post-pre-step wire traffic (method + rendered params — CBOR-ordered,
    /// so a stable, deep comparison key) and the session's command log.
    /// </summary>
    private static async Task<(List<string> Traffic, List<string> Log)> RunMixedScenarioAsync(
        System.Reflection.Assembly asm, bool batched)
    {
        var session = NewSession(asm);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        // Pre-step: an "already in the DB" root (single save promotes it to tracked).
        var existing = NewEntity(asm, "M.Root", "existing", "Name", "old");
        await session.SaveAsync(existing, tx);
        var mark = conn.Sent.Count;

        // A new root, tracked, with a new child adopted via the [Parent] setter.
        var fresh = NewEntity(asm, "M.Root", "fresh", "Name", "fresh-root");
        session.Track(fresh);
        var leaf = NewEntity(asm, "M.Leaf", "leaf_one", "Label", "leaf");
        leaf.GetType().GetProperty("Root")!.SetValue(leaf, fresh);
        Assert.True(session.IsTracked(leaf.Id));

        // A pending update on the existing root.
        existing.GetType().GetProperty("Name")!.SetValue(existing, "renamed");

        if (batched)
        {
            await session.SaveAsync(new[] { fresh, existing }, tx);
        }
        else
        {
            await session.SaveAsync(fresh, tx);
            await session.SaveAsync(existing, tx);
        }

        Assert.False(session.IsClosed);
        Assert.True(session.IsTracked(fresh.Id));
        Assert.True(session.IsTracked(leaf.Id));
        Assert.True(session.IsTracked(existing.Id));

        var traffic = conn.Sent.Skip(mark)
            .Select(s => $"{s.Method} {s.Params}")
            .ToList();
        var log = session.Log.Entries.Select(c => $"{c.Op} {c.Target}").ToList();
        return (traffic, log);
    }

    // ─────────────────────── version-guarded update ordering ─────────────────

    [Fact]
    public async Task Batch_FlushesBufferedCreates_BeforeVersionGuardedUpdate()
    {
        // New versioned entities CREATE with version 1 unguarded — they batch. The
        // tracked entity's guarded UPDATE is non-batchable (its empty-result conflict
        // check is per-statement), so it flushes the buffer first and dispatches after.
        var (asm, session) = LoadModel(VersionedModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var tracked = NewEntity(asm, "M.Doc", "tracked", "Title", "already-there");
        await session.SaveAsync(tracked, tx);
        var mark = conn.Sent.Count;

        conn.Responder = (method, @params, _) =>
            method == "query" && RpcSql(@params).StartsWith("UPDATE", StringComparison.Ordinal)
                ? MatchedUpdateResponse()
                : SurrealValue.None;

        var n1 = NewEntity(asm, "M.Doc", "doc_one", "Title", "new-1");
        var n2 = NewEntity(asm, "M.Doc", "doc_two", "Title", "new-2");
        await session.SaveAsync(new[] { n1, n2, tracked }, tx);

        var rpcs = QueryRpcs(conn, mark);
        Assert.Equal(2, rpcs.Count);

        // Statement 1: the coalesced INSERT of both new docs, each seeded version 1.
        Assert.Equal("INSERT INTO $_table $_records", rpcs[0].Sql);
        Assert.Equal("docs", GetVar(rpcs[0].Vars, "_table").ToString());
        var records = RecordsOf(rpcs[0].Vars);
        Assert.Equal(2, records.Count);
        Assert.Equal("docs:doc_one", records[0]["id"].ToString());
        Assert.Equal("docs:doc_two", records[1]["id"].ToString());
        Assert.All(records, r => Assert.Equal(1, HydrationValue.ReadOrDefault<int>(new SurrealObjectValue(r), "version")));

        // Statement 2 (strictly after the flush): the guarded UPDATE.
        Assert.Equal("UPDATE $_record_id CONTENT $_content WHERE version = $_expected_version;", rpcs[1].Sql);
        Assert.Equal("docs:tracked", GetVar(rpcs[1].Vars, "_record_id").ToString());

        // Confirmed match bumps the in-memory version; session stays open.
        Assert.Equal(2, (int)tracked.GetType().GetProperty("Version")!.GetValue(tracked)!);
        Assert.False(session.IsClosed);
    }

    // ───────────────────────────── failure modes ─────────────────────────────

    [Fact]
    public async Task Batch_VersionConflictMidBatch_ClosesSession_WithBatchSaveFailed()
    {
        var (asm, session) = LoadModel(VersionedModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var tracked = NewEntity(asm, "M.Doc", "tracked", "Title", "already-there");
        await session.SaveAsync(tracked, tx);

        // The guard matches nothing — a competing writer bumped the version.
        conn.Responder = (method, @params, _) =>
            method == "query" && RpcSql(@params).StartsWith("UPDATE", StringComparison.Ordinal)
                ? EmptyUpdateResponse()
                : SurrealValue.None;

        var fresh = NewEntity(asm, "M.Doc", "doc_one", "Title", "new-1");
        var ex = await Assert.ThrowsAsync<SurrealVersionConflictException>(
            () => session.SaveAsync(new[] { fresh, tracked }, tx));

        Assert.True(session.IsClosed);
        Assert.NotNull(session.CloseReason);
        Assert.Equal(SessionCloseKind.BatchSaveFailed, session.CloseReason!.Kind);
        Assert.Equal(tracked.Id, session.CloseReason.EntityId);
        Assert.Same(ex, session.CloseReason.Cause);
        Assert.Contains("a batch SaveAsync of docs:tracked failed", session.CloseReason.Summary);
    }

    [Fact]
    public async Task Batch_WireFailureOnFinalFlush_ClosesSession_WithBatchSaveFailed()
    {
        // An all-creates batch defers its wire send to the end-of-batch flush; the
        // failure still closes the session as one logical Save, stamped with the last
        // batch entity in flight.
        var (asm, session) = LoadModel(TwoTableModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var boom = new IOException("wire down");
        conn.Responder = (method, @params, _) =>
            method == "query" && RpcSql(@params).StartsWith("INSERT INTO", StringComparison.Ordinal)
                ? throw boom
                : SurrealValue.None;

        var a1 = NewEntity(asm, "M.Alpha", "a_one", "Name", "first");
        var a2 = NewEntity(asm, "M.Alpha", "a_two", "Name", "second");
        var thrown = await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(new[] { a1, a2 }, tx));

        Assert.Same(boom, thrown);
        Assert.True(session.IsClosed);
        Assert.Equal(SessionCloseKind.BatchSaveFailed, session.CloseReason!.Kind);
        Assert.Equal(a2.Id, session.CloseReason.EntityId);
        Assert.Same(boom, session.CloseReason.Cause);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(a1, tx));
    }

    // ─────────────────────── auto-bind / initialize parity ───────────────────

    [Fact]
    public async Task Batch_AutoBindsAndInitializes_MandatoryReferences_LikeSingleSave()
    {
        // An unbound entity in the batch gets Bind + Initialize (OnCreate* minting of
        // the mandatory [Reference]) exactly like the single-save path, and the minted
        // dependency saves FIRST via the forward-dep walk.
        var (asm, session) = LoadModel(MandatoryRefModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        var owner = NewEntity(asm, "M.Owner", "owner_one", "Name", "holder");
        Assert.Null(owner.Session);

        await session.SaveAsync(new[] { owner }, tx);

        // Bound + initialized: the mandatory Profile reference was minted.
        Assert.Same(session, owner.Session);
        var profile = owner.GetType().GetProperty("Profile")!.GetValue(owner);
        Assert.NotNull(profile);
        var profileId = ((IEntity)profile!).Id;
        Assert.True(session.IsTracked(profileId));
        Assert.True(session.IsTracked(owner.Id));

        // Dependency-first dispatch: profile's create precedes owner's. Different
        // tables → two runs of one → two single-save-shaped CREATEs.
        var rpcs = QueryRpcs(conn);
        Assert.Equal(2, rpcs.Count);
        Assert.All(rpcs, r => Assert.Equal("CREATE $_record_id CONTENT $_content", r.Sql));
        Assert.Equal(profileId.ToString(), GetVar(rpcs[0].Vars, "_record_id").ToString());
        Assert.Equal("owners:owner_one", GetVar(rpcs[1].Vars, "_record_id").ToString());
    }

    // ───────────────────────────── argument edges ─────────────────────────────

    [Fact]
    public async Task Batch_EmptyEnumerable_IsNoOp()
    {
        var (asm, session) = LoadModel(TwoTableModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        var mark = conn.Sent.Count;

        await session.SaveAsync(Array.Empty<IEntity>(), tx);

        Assert.Equal(mark, conn.Sent.Count);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task Batch_OnClosedSession_Throws()
    {
        var (asm, session) = LoadModel(TwoTableModel);
        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();

        session.Abandon();
        var a1 = NewEntity(asm, "M.Alpha", "a_one", "Name", "first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(new[] { a1 }, tx));
    }

    [Fact]
    public async Task Batch_NullEntityInBatch_Throws_BeforeAnyDispatch()
    {
        var (asm, session) = LoadModel(TwoTableModel);
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        var mark = conn.Sent.Count;

        var a1 = NewEntity(asm, "M.Alpha", "a_one", "Name", "first");
        await Assert.ThrowsAsync<ArgumentException>(() => session.SaveAsync(new[] { a1, null! }, tx));

        // Fails argument validation up front: nothing dispatched, session untouched.
        Assert.Equal(mark, conn.Sent.Count);
        Assert.False(session.IsClosed);
        Assert.False(session.IsTracked(a1.Id));
    }

    // ───────────────────────────── helpers ───────────────────────────────────

    private static (System.Reflection.Assembly Asm, SurrealSession Session) LoadModel(string source)
    {
        var asm = GeneratorHarness.CompileAndLoad(source);
        return (asm, NewSession(asm));
    }

    private static SurrealSession NewSession(System.Reflection.Assembly asm)
    {
        var registry = (IReferenceRegistry)asm.GetType("M.Workspace", throwOnError: true)!
            .GetProperty("ReferenceRegistry")!.GetValue(null)!;
        return new SurrealSession(registry);
    }

    /// <summary>
    /// Instantiate a generated entity with a pinned slug id (deterministic wire
    /// assertions) and one string property set.
    /// </summary>
    private static IEntity NewEntity(
        System.Reflection.Assembly asm, string typeName, string idSlug, string propName, string propValue)
    {
        var type = asm.GetType(typeName, throwOnError: true)!;
        var entity = (IEntity)Activator.CreateInstance(type)!;
        var idProp = type.GetProperty("Id")!;
        idProp.SetValue(entity, Activator.CreateInstance(idProp.PropertyType, idSlug));
        type.GetProperty(propName)!.SetValue(entity, propValue);
        return entity;
    }

    /// <summary>Every recorded "query" RPC from <paramref name="from"/> on, as (sql, vars).</summary>
    private static List<(string Sql, SurrealObject Vars)> QueryRpcs(
        FakeSurreal.RecordingConnection conn, int from = 0)
        => conn.Sent.Skip(from)
            .Where(s => s.Method == "query")
            .Select(s =>
            {
                var list = Assert.IsType<SurrealListValue>(s.Params);
                var sql = Assert.IsType<StringSurrealValue>(list.List[0]).Value;
                var vars = Assert.IsType<SurrealObjectValue>(list.List[1]).Object;
                return (sql, vars);
            })
            .ToList();

    /// <summary>Unwraps the <c>$_records</c> array binding of a batched INSERT.</summary>
    private static List<SurrealObject> RecordsOf(SurrealObject vars)
    {
        var records = Assert.IsType<SurrealListValue>(GetVar(vars, "_records"));
        return records.List.Select(v => Assert.IsType<SurrealObjectValue>(v).Object).ToList();
    }

    private static SurrealValue GetVar(SurrealObject vars, string key)
    {
        Assert.True(vars.TryGetValue(key, out var value), $"binding '{key}' missing");
        return value!;
    }

    private static string RpcSql(SurrealValue? @params) =>
        @params is SurrealListValue { List: { Count: > 0 } list } && list[0] is StringSurrealValue s
            ? s.Value
            : string.Empty;

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
}
