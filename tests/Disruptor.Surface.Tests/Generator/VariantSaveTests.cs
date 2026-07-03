using Disruptor.Surface.Runtime;
using Disruptor.Surface.Tests.Runtime;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// Deterministic edge ids + nullable-payload duplicate-update bindings (2026-07-02
/// review fixes) — pinned at both levels: emission shape (the generated SaveAsync /
/// id-anchor source) and end-to-end through REAL generation
/// (<see cref="GeneratorHarness.CompileAndLoad"/>) + the REAL
/// <see cref="SurrealSession.SaveAsync(IEntity, Disruptor.Surreal.SurrealTransaction, CancellationToken)"/>
/// dispatch against a recording fake connection.
/// <para>
/// The bug being pinned: the variant save path used to lazy-mint a RANDOM edge id and
/// send it in <c>$_content</c>; on the <c>UNIQUE (in, out)</c> duplicate path the
/// substrate kept the ORIGINAL row id while <c>MarkSaved</c> recorded the fresh mint —
/// the identity map lied. The owner-chosen fix derives the id deterministically from
/// <c>(source, edge, target)</c> via <c>RecordId.ForEdge</c> BEFORE dispatch, so the
/// same pair always replays onto the same row id and MarkSaved stays truthful.
/// </para>
/// </summary>
public sealed class VariantSaveTests
{
    /// <summary>
    /// Single-variant kind (SCHEMAFULL edge table) with typed-id endpoints — no
    /// forward-dep dispatch, so the wire sees exactly one INSERT RELATION per save.
    /// Carries a nullable payload (must bind an explicit NONE on the duplicate-update
    /// path when null).
    /// </summary>
    private const string CrossAggregateModel = """
        using Disruptor.Surface.Annotations;
        namespace M;

        public sealed class TouchesAttribute : ForwardRelation;

        [Table, AggregateRoot] public partial class Owner {
            [Id] public partial OwnerId Id { get; set; }
        }
        [Table, AggregateRoot] public partial class Foreign {
            [Id] public partial ForeignId Id { get; set; }
        }

        [Touches]
        public partial class CrossLink {
            [In]  public partial OwnerId Source { get; set; }
            [Out] public partial ForeignId Target { get; set; }
            [Property] public partial string? Note { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    /// <summary>
    /// Multi-variant kind (SCHEMALESS edge table) with entity-typed endpoints — the
    /// forward-dep walk saves the endpoints first, and the derive reads the endpoint
    /// ids through the entity refs.
    /// </summary>
    private const string EntityEndpointModel = """
        using Disruptor.Surface.Annotations;
        namespace M;

        public sealed class LinksAttribute : ForwardRelation;

        [Table, AggregateRoot] public partial class Node {
            [Id] public partial NodeId Id { get; set; }
        }
        [Table, AggregateRoot] public partial class Hub {
            [Id] public partial HubId Id { get; set; }
        }

        [Links]
        public partial class NodeToHub {
            [In]  public partial Node Source { get; set; }
            [Out] public partial Hub Target { get; set; }
        }
        [Links]
        public partial class HubToNode {
            [In]  public partial Hub Source { get; set; }
            [Out] public partial Node Target { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    // ───────────────────────────── emission shape ────────────────────────────

    [Fact]
    public void Emits_MintId_DeterministicDerive()
    {
        var (result, _, _, compileDiags) = GeneratorHarness.Run(CrossAggregateModel);
        Assert.Empty(compileDiags);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CrossLink.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant.ToString();

        // The id anchor lazy-mints through __MintId — NOT a bare {Kind}Id.New(). The
        // mint must live in the anchor because the session's SaveContext reads
        // IEntity.Id (for its visited-set) BEFORE the emitted SaveAsync body runs.
        Assert.Contains("global::Disruptor.Surface.Runtime.IEntity.Id => _id ??= __MintId();", src);
        // Deterministic derive from the (in, edge, out) triple via RecordId.ForEdge.
        Assert.Contains("global::Disruptor.Surface.Runtime.RecordId.ForEdge(\"touches\", __inId, __outId).Value", src);
        // Typed-id endpoints only derive from a populated struct — a default-unset or
        // nullable-null endpoint yields null and falls back to the random mint (the
        // save path still fails before dispatch for unset endpoints).
        Assert.Contains("var __in = _source is { Value: not null } __v_source ? (global::Disruptor.Surface.Runtime.RecordId?)(global::Disruptor.Surface.Runtime.RecordId)__v_source : null;", src);
        Assert.Contains(": global::M.TouchesId.New();", src);
    }

    [Fact]
    public void Emits_NullablePayload_BindsExplicitNone_OnDuplicateUpdatePath()
    {
        // The ON DUPLICATE KEY UPDATE clause ALWAYS references $_p_note, but
        // ContentValue.Set omits null values — so a nullable payload set to null used
        // to leave the variable unbound. The emitted bindings now branch: null binds
        // an explicit NONE (converging with the insert path, where the null field is
        // omitted from $_content and lands as NONE); non-null goes through the same
        // ContentValue.Set wrapping as before.
        var (result, _, _, compileDiags) = GeneratorHarness.Run(CrossAggregateModel);
        Assert.Empty(compileDiags);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CrossLink.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant.ToString();

        Assert.Contains("INSERT RELATION INTO touches $_content ON DUPLICATE KEY UPDATE note = $_p_note;", src);
        Assert.Contains("if (_note is null)", src);
        Assert.Contains("__bindings[\"_p_note\"] = global::Disruptor.Surreal.Values.SurrealValue.None;", src);
        Assert.Contains("global::Disruptor.Surface.Runtime.ContentValue.Set(__bindings, \"_p_note\", _note);", src);
        // The $_content build keeps the null-omission contract (schema DEFAULT applies
        // on insert) — only the SQL-variable bindings get the explicit NONE.
        Assert.Contains("global::Disruptor.Surface.Runtime.ContentValue.Set(__content, \"note\", _note);", src);
    }

    // ───────────────────────────── end-to-end save ───────────────────────────

    [Fact]
    public async Task E2E_SameEndpoints_TwoSaves_DispatchTheSameDerivedEdgeId()
    {
        var asm = GeneratorHarness.CompileAndLoad(CrossAggregateModel);

        var first = await SaveCrossLinkAsync(asm, sourceUlid: "01H0000000000000000000000A", targetUlid: "01H0000000000000000000000B");
        var second = await SaveCrossLinkAsync(asm, sourceUlid: "01H0000000000000000000000A", targetUlid: "01H0000000000000000000000B");

        // Same (source, target) → same edge id in $_content, save after save — the
        // UNIQUE(in,out) duplicate path therefore updates the very row id the session
        // recorded via MarkSaved (replay-replace, no id drift).
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("touches", first.Id.Table);
        // The derived value is the documented ForEdge scheme (hash form — valid per
        // RecordIdFormat, which the {Kind}Id ctor enforces).
        Assert.True(RecordIdFormat.IsValid(first.Id.Value));
        Assert.Equal(RecordId.ForEdge("touches", first.In, first.Out), first.Id);
    }

    [Fact]
    public async Task E2E_DifferentTargets_DispatchDifferentDerivedEdgeIds()
    {
        var asm = GeneratorHarness.CompileAndLoad(CrossAggregateModel);

        var first = await SaveCrossLinkAsync(asm, sourceUlid: "01H0000000000000000000000A", targetUlid: "01H0000000000000000000000B");
        var second = await SaveCrossLinkAsync(asm, sourceUlid: "01H0000000000000000000000A", targetUlid: "01H0000000000000000000000C");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task E2E_EntityTypedEndpoints_MultiVariantKind_DeriveTheSameIdAcrossSaves()
    {
        // Entity-typed endpoints (multi-variant SCHEMALESS kind): the derive resolves
        // the endpoint ids through the entity refs, and the forward-dep walk still
        // saves the endpoints first. Fresh sessions + fresh instances with the same
        // entity ids must land on the same edge id.
        var asm = GeneratorHarness.CompileAndLoad(EntityEndpointModel);

        var first = await SaveNodeToHubAsync(asm, nodeUlid: "01H0000000000000000000000A", hubUlid: "01H0000000000000000000000B");
        var second = await SaveNodeToHubAsync(asm, nodeUlid: "01H0000000000000000000000A", hubUlid: "01H0000000000000000000000B");

        Assert.Equal(first, second);
        Assert.Equal("links", first.Table);
        Assert.True(RecordIdFormat.IsValid(first.Value));
    }

    [Fact]
    public async Task E2E_NullableNullPayload_BindsNone_NonNullBindsValue()
    {
        var asm = GeneratorHarness.CompileAndLoad(CrossAggregateModel);

        // Note left null: every parameter the duplicate-update SQL references must be
        // bound — $_p_note carries an explicit NONE, and $_content omits the field.
        var (nullSql, nullVars) = await SaveCrossLinkRawAsync(asm, "01H0000000000000000000000A", "01H0000000000000000000000B", note: null);
        Assert.Contains("ON DUPLICATE KEY UPDATE note = $_p_note", nullSql);
        Assert.IsType<SurrealNoneValue>(GetVar(nullVars, "_p_note"));
        var nullContent = Assert.IsType<SurrealObjectValue>(GetVar(nullVars, "_content"));
        Assert.False(nullContent.Object.ContainsKey("note"));

        // Note set: the binding carries the value (same ContentValue wrapping as ever).
        var (_, setVars) = await SaveCrossLinkRawAsync(asm, "01H0000000000000000000000A", "01H0000000000000000000000C", note: "hello");
        Assert.Equal("hello", Assert.IsType<StringSurrealValue>(GetVar(setVars, "_p_note")).Value);
        var setContent = Assert.IsType<SurrealObjectValue>(GetVar(setVars, "_content"));
        Assert.True(setContent.Object.ContainsKey("note"));
    }

    [Fact]
    public async Task E2E_NullableEndpoint_NullAtSave_StillFailsBeforeDispatch()
    {
        // Nullable endpoints left null keep their pre-derive failure behavior: the
        // derive never runs from a null endpoint (__MintId falls back to a random
        // mint), and the save path fails before any INSERT RELATION reaches the wire.
        var source = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public sealed class ProdsAttribute : ForwardRelation;

            [Table, AggregateRoot] public partial class Owner {
                [Id] public partial OwnerId Id { get; set; }
            }

            [Prods]
            public partial class OptionalLink {
                [In]  public partial OwnerId Source { get; set; }
                [Out] public partial OwnerId? Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var asm = GeneratorHarness.CompileAndLoad(source);

        var linkT = asm.GetType("M.OptionalLink", throwOnError: true)!;
        var link = (IEntity)Activator.CreateInstance(linkT)!;
        var ownerIdT = asm.GetType("M.OwnerId", throwOnError: true)!;
        linkT.GetProperty("Source")!.SetValue(link, Activator.CreateInstance(ownerIdT, "01H0000000000000000000000A"));
        // Target stays null.

        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => session.SaveAsync(link, tx));
        Assert.DoesNotContain(conn.Sent, s => s.Method == "query" && RpcSql(s.Params).StartsWith("INSERT RELATION", StringComparison.Ordinal));
        Assert.True(session.IsClosed);
    }

    // ───────────────────────────── helpers ───────────────────────────────────

    private sealed record DispatchedEdge(RecordId Id, RecordId In, RecordId Out);

    /// <summary>
    /// Creates a CrossLink with the given typed-id endpoints, saves it through a fresh
    /// <see cref="SurrealSession"/> against a recording fake, and returns the (id, in,
    /// out) record ids from the dispatched <c>$_content</c>.
    /// </summary>
    private static async Task<DispatchedEdge> SaveCrossLinkAsync(
        System.Reflection.Assembly asm, string sourceUlid, string targetUlid)
    {
        var (_, vars) = await SaveCrossLinkRawAsync(asm, sourceUlid, targetUlid, note: null);
        var content = Assert.IsType<SurrealObjectValue>(GetVar(vars, "_content"));
        return new DispatchedEdge(
            ReadRecordId(content, "id"),
            ReadRecordId(content, "in"),
            ReadRecordId(content, "out"));
    }

    private static async Task<(string Sql, SurrealObject Vars)> SaveCrossLinkRawAsync(
        System.Reflection.Assembly asm, string sourceUlid, string targetUlid, string? note)
    {
        var linkT = asm.GetType("M.CrossLink", throwOnError: true)!;
        var link = (IEntity)Activator.CreateInstance(linkT)!;
        linkT.GetProperty("Source")!.SetValue(link, Activator.CreateInstance(asm.GetType("M.OwnerId", throwOnError: true)!, sourceUlid));
        linkT.GetProperty("Target")!.SetValue(link, Activator.CreateInstance(asm.GetType("M.ForeignId", throwOnError: true)!, targetUlid));
        if (note is not null)
        {
            linkT.GetProperty("Note")!.SetValue(link, note);
        }

        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        await session.SaveAsync(link, tx);

        return SingleQueryRpc(conn, s => s.StartsWith("INSERT RELATION", StringComparison.Ordinal));
    }

    /// <summary>
    /// Creates fresh Node / Hub entities with pinned ids plus a NodeToHub variant,
    /// saves through a fresh session, and returns the edge id from the dispatched
    /// INSERT RELATION content.
    /// </summary>
    private static async Task<RecordId> SaveNodeToHubAsync(System.Reflection.Assembly asm, string nodeUlid, string hubUlid)
    {
        var nodeT = asm.GetType("M.Node", throwOnError: true)!;
        var hubT = asm.GetType("M.Hub", throwOnError: true)!;
        var node = Activator.CreateInstance(nodeT)!;
        var hub = Activator.CreateInstance(hubT)!;
        nodeT.GetProperty("Id")!.SetValue(node, Activator.CreateInstance(asm.GetType("M.NodeId", throwOnError: true)!, nodeUlid));
        hubT.GetProperty("Id")!.SetValue(hub, Activator.CreateInstance(asm.GetType("M.HubId", throwOnError: true)!, hubUlid));

        var linkT = asm.GetType("M.NodeToHub", throwOnError: true)!;
        var link = (IEntity)Activator.CreateInstance(linkT)!;
        linkT.GetProperty("Source")!.SetValue(link, node);
        linkT.GetProperty("Target")!.SetValue(link, hub);

        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        await session.SaveAsync(link, tx);

        var (_, vars) = SingleQueryRpc(conn, s => s.StartsWith("INSERT RELATION", StringComparison.Ordinal));
        var content = Assert.IsType<SurrealObjectValue>(GetVar(vars, "_content"));
        return ReadRecordId(content, "id");
    }

    private static RecordId ReadRecordId(SurrealObjectValue content, string key)
    {
        var value = Assert.IsType<SurrealRecordIdValue>(GetVar(content.Object, key));
        return new RecordId(
            value.SurrealRecordId.Table.Name,
            ((SurrealStringRecordIdKey)value.SurrealRecordId.Key).Value);
    }

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
}
