## Schema and model

1. No indexes beyond the edge UNIQUE(in, out) index. SchemaEmitter only ever emits one DEFINE INDEX. There's no attribute ([Index], [Unique], [Index("fk_design_id")]) and no DDL emit for per-property or composite indexes. The first time a Workspace.Query.Constraints.Where(ConstraintQ.Description.Contains(...)) query runs over 100k rows, you'll do a full scan. Equally, no support for DEFINE INDEX ... SEARCH ANALYZER (full-text), MTREE (vector), or HNSW — SurrealDB's whole differentiator on the search side.

2. No ASSERT or value-level validators in DDL. The skill explicitly says "Validation is yours… A [Property] int Rating will happily persist -7." That's fine as a stance, but SurrealDB has ASSERT $value > 0 AND $value <= 5 baked into DEFINE FIELD. An [Assert(Expression)] attribute, or even a simple [Range(min, max)] / [StringLength(max)] set, would push the guarantee down into the substrate where it actually holds. Right now any non-Disruptor.Surface writer (raw Disruptor.Surreal, surreal sql, a future MCP tool) can poison the data.

3. No PERMISSIONS clause. Every emitted DEFINE TABLE ... SCHEMAFULL has no permissions. For loopback-only Project Brain this is fine; the moment a tool runs anywhere multi-user, the schema is wide open. An [AccessControl("FOR select WHERE ...")] or higher-level [OwnedByActor] would fit naturally.

4. No migration story at all. intro.md is explicit: "Schema generation is forward-only and additive." That's a defensible scoping decision, but it's a real gap in practice — there's no Workspace.Diff(SchemaSnapshot), no fingerprint of "what schema did I apply last time," no helper to render a REMOVE FIELD/rename. For a tool meant to evolve a typed design tree, this becomes painful around preview.70+ as the model shifts shape.

5. Scalar type coverage is narrow. SchemaEmitter.MapScalarType maps string, int/long, bool, float/double, decimal, DateTime/DateTimeOffset, Guid, Ulid. Missing: byte[] (bytes), TimeSpan (duration), enums beyond the string-stored path the compiler does, Uri, JsonElement, geometry types. There's no extension hook either — MapScalarType is internal with a fixed switch, so you can't register a converter without forking. The IEnumerable arm in SurfaceQueryCompiler.WrapAsSurrealValue accepts more types than the schema emitter does, so a [Property] TimeSpan Duration will silently emit no DDL field (it does emit a // SCHEMA: ... not mapped; field omitted. comment, which is at least honest, but still produces a runtime-only failure).

6. No "owned record" / fully-inline POCO option in scalar position. You can inline-element-collection a record ([Property] IReadOnlyList<TElem>) and you can [Reference, Inline] a separate [Table]. But there's no [Property] partial Address Address { get; set; } where Address is a value-type POCO embedded in the parent row. That forces every value-shaped sub-object to either be a separate table (lifecycle overhead) or a singleton list (semantically wrong).

## Query layer

1. No Count() / aggregation terminal. Five terminals: IdsAsync, Select(p).ExecuteAsync, ExecuteAsync, LoadAsync, Hydrate.{Table}(ids). There's no CountAsync — getting "how many open todos" requires IdsAsync + .Count which transfers every id. SurrealQL has SELECT count() FROM ... GROUP ALL, and a CountAsync terminal on SurfaceQuery<T> is a couple-hundred-line addition.

2. No GROUP BY, no MIN/MAX/SUM/AVG. Projections are flat per-row materialisers. There's no path to "open todos per list" or "max created_at per author" without dropping to raw SurrealQL. For an internal power-user tool this is okay; it does mean every dashboard query becomes a hand-rolled SQL string.

3. Predicate operators are sparse. Eq, Lt/Le/Gt/Ge, In, and Contains on PropertyExpr<string>. Missing the everyday ones: StartsWith / EndsWith (string::starts_with, string::ends_with), case-insensitive variants, IsNullOrEmpty, regex match (~), Between (a single op instead of Ge.And(Le)), NotIn. Adding them is one record per predicate plus one arm in CompilePredicate — not architectural, just missing.

4. No live query support. SurrealDB's LIVE SELECT is one of its differentiating features. There's no path to subscribe to changes on an aggregate or a query. For a tool where one process spawns agents that write while a UI watches, this is the natural reactive seam. Not a small addition (it needs a subscription registry, a snapshot of changeset shapes, hooks into the existing edge index) but its absence is structural.

5. Edge query API is much weaker than entity query API. Workspace.Query.Edges.Restricts.WhereIn(...) exists but there's no inline payload predicate composition the way {Variant}Q would imply (referenced in api.md:511 but the section is sparse). The async variant query family (QueryVariantsOutgoingAsync) doesn't accept a predicate — every payload filter is client-side. A SurrealDB native filter on edge payload is one of the things that would make the "code symbol calls graph with confidence > 0.8" query cheap; today it isn't.

6. No Skip-on-cursor pagination. Start(int) is offset-based, which scales poorly for deep pages. For a tool whose primary use case is "scroll through 50,000 findings," a cursor terminal (last-id-seen) would be the right shape.

## Concurrency / consistency

1. No optimistic-concurrency token. The library leans entirely on SurrealDB's MVCC + SurrealConflictException at COMMIT. That's correct for the canonical "load → mutate → save → retry" pattern, but it has no answer for "I rendered this design at 10:00, user edited until 10:30, then submitted" — there's no @version, no LastModified check, no IfMatch semantics. The substrate-level conflict only fires when two writes are in-flight at the same time; lost-update across sessions held in human time is invisible. An [OptimisticConcurrency] DateTime UpdatedAt or int Version with a check during the emitted UPDATE would close this.

2. No audit columns. No [CreatedAt] / [UpdatedAt] / [CreatedBy] / [UpdatedBy] auto-populated fields. Every model has to wire them by hand in domain methods or OnCreate* hooks. For an internal tool ecosystem with multiple agent writers, "who wrote this finding, when" is universally wanted.

3. Cross-aggregate atomicity is documented as the user's problem. The skill is explicit about two sessions / two transactions / two commits, and notes that a shared tx across sessions is mechanically accepted but not validated. This is a deliberate scoping decision, but in practice the canonical Project Brain case ("write a Review and link it to a Constraint inside Design X") is exactly the cross-aggregate workflow — and the prescribed pattern is two commits with manual cleanup on failure. A Workspace.MultiAggregateUnit(...) or Workspace.SaveAcrossAsync(...) that explicitly coordinates two sessions under one tx would lift this from "users figure it out" to "library has a tested path."

## Operational

1. No bulk-save / bulk-create primitive. SaveAsync(entity, tx) dispatches one CREATE/UPDATE per entity. For "import 5,000 constraints" you do 5,000 round-trips in one transaction. SurrealDB has INSERT INTO table [{...},{...},...] for batched inserts. The skill's "Save in batches inside one transaction" advice papers over the fact that the per-row dispatch is the real cost.

2. No OnLoading / OnLoaded hook on entities. OnCreate* for mandatory references, OnDeleting before delete, but no symmetric "I was just hydrated from a row" — useful for cached derived state, lazy-init of non-persistent fields, instrumentation.

3. No telemetry / tracing seam. CommandLog is documented as diagnostic-only; the SaveContext doesn't expose anything to OpenTelemetry / ActivitySource. Adding using var activity = ... around each dispatch is half a day of work and turns the library from invisible to traceable.

4. No connection-string-free wiring helper. ApplySchemaAsync exists; there's no DI extension (services.AddDisruptorSurface(opts => ...)), no IHostedService. For a tool that spawns .NET services in Docker containers, the boot code is currently ~10 lines of imperative wiring per service. A Microsoft.Extensions.DependencyInjection.Disruptor.Surface package is missing.

5. Edge writes can't carry an explicit id. IRelationVariant ids are auto-minted. There's no path to use a content-addressed id for an edge (the docs mention RecordId.Idempotent("uses") but it doesn't appear to be plumbed into the variant SaveAsync). For replay-replace semantics on a deterministic edge ("the call from MethodA to MethodB always has id calls:<hash>"), the library currently relies on the UNIQUE(in, out) index doing the dedupe, which costs an INSERT-then-reject per replay attempt.

## Prioritised hit list

1. Indexes — [Index] / [Unique] / composite. Without these, every Design Explorer search on a non-trivial corpus becomes a linear scan. Half a day of emitter work.

2. CountAsync + a few more predicates (StartsWith, NotIn, Between) — cheap, removes the "drop to raw SQL" pressure for review-pipeline dashboards.

3. [Assert] and audit columns ([CreatedAt] / [UpdatedAt]) — pushes invariants into the substrate, where they hold against any writer. Critical once multiple agents share the database.

4. Optimistic-concurrency token — closes lost-update for human-scale edit windows. The current substrate conflict only fires for concurrent in-flight writes, which is a much narrower guarantee than most callers assume.

5. Bulk SaveAsync(IEnumerable<T>, tx) — INSERT INTO ... [{},{},...]. Real perf win for any seed/import flow.

6. Migration helper — even just Workspace.SchemaFingerprint + an opt-in Workspace.GenerateDiffAsync(previousFingerprint) would unblock a lot of "the model changed; what now?" pain.

7. Live queries — the structural one. Big lift, but it's the seam the broader Project Brain UI/agent loop naturally wants.

## Half-built primitives

1. RecordId.Idempotent is a sentinel, not a strategy. RecordId.cs:37 defines Idempotent(table) as new(table, "") and ToLiteral renders it as surrealdb::fn::record::generate(table). The docs gesture at it as "deterministic hash of the linkage triple," and preview.49's notes claim INSERT RELATION IGNORE "keep[s] idempotence on the deterministic RecordId.Idempotent edge id." But the empty-string value isn't a deterministic hash of (in, out); it's a substrate-side mint. There's no path I can see from session.SaveAsync(new TVariant { Source = s, Target = t }, tx) to "use a content-addressed id derived from (kind, s, t)." The UNIQUE(in, out) index makes this correct — duplicates fail at the index, which gets absorbed by IGNORE — but it doesn't make replay-replace cheap. If you want "the call from MethodA to MethodB always has id calls:<sha-of-pair>" (the CodeSymbol use case), you'd have to add an [Id] to the variant and feed it manually. An [ContentAddressedFromEndpoints] opt-in on a variant class would round this out.

2. IReferenceRegistry carries a stale comment. ReferenceRegistry.cs:21 mentions CommitPlanner.Build as a consumer, but CommitPlanner has been gone since preview.34 per notes.md:407. The single consumer is now PlanDelete in SurrealSession. Not a feature gap — a docs lag — but it does highlight that the registry is doing exactly one job (incoming reference resolution for delete-cascade planning) and could profitably carry more model metadata (table-name → element type, aggregate-root membership, edge-name → kind type) if the runtime ever needs it.

3. HydrationValue.ReadOrDefault still uses reflection (HydrationValue.cs:73, 175). The notes for the inline-element collection emit (notes.md:112) carefully say "no reflection" — but reflection survives for the POCO/record fallback inside element types. Two concrete consequences: (1) zero AOT/trim story — anything that flows through ReadOrDefault<T> for a record T will break in a published trimmed binary or under NativeAOT; (2) startup overhead per type. For an internal tool running long-lived, neither matters; if Disruptor.Surface ever ends up inside a serverless / per-request agent host, both bite. The fix is to push another generator pass: emit a typed IReadable<T> per record element type and dispatch through that. Worth flagging now because trimming concerns get much more expensive to add later.

## Generator pipeline

1. [Table] inheritance isn't supported. TableExtractor.InheritsFromForwardRelation is the only inheritance walker, and it's looking at attribute-class derivation, not entity-class derivation. There's no path to write public abstract partial class AuditedEntity { [Property] public partial DateTime CreatedAtUtc { get; set; } } and have every concrete [Table] inherit those columns. Every entity has to repeat the column-set by hand. For Project Brain — where Design / Review / Plan domains share Issue-shaped semantics — this is going to show up as 8–12 copy-pasted property declarations across the model. The fix is structural (the extractor walks the base chain like the shared-shape extractor already does for relation variants) but the precedent in preview.55–57 means the machinery exists for the entity side.

2. No multi-assembly composition. CG018 requires exactly one [CompositionRoot] per compilation. Currently the library only works for models in in one assembly, but the planning/execution split naturally wants per-domain assemblies with cross-assembly aggregate roots. The library has no answer for that — Workspace.LoadDesignAsync is emitted into whichever assembly Workspace lives in, so the planning assembly can't add Workspace.LoadPlanAsync. The escape would be a [PartialCompositionRoot] that gets stitched at the consuming assembly, but that's a non-trivial generator change.

3. Diagnostic numbering has holes (CG002–CG007 gone, CG016 gone, CG023 missing, CG034 missing — confirmed against Diagnostics.cs). Fine in itself; flagging because future-added diagnostics will want to slot into those holes (e.g. CG023 for "shared-shape lift conflict" mentioned in preview.57's note as a follow-up). Worth picking a numbering convention now so a CG036 doesn't have to be the cascade-lift-conflict diagnostic just because the next slot was used.

4. No assembly-wide model fingerprint. The schema chunks are emitted as a string[] array on Workspace.Schema. There's no Workspace.ModelFingerprint (a SHA of the model graph) that would let downstream consumers (an MCP design explorer, a code index cache, an event subscriber) detect "the model changed" without diffing the schema text. For Project Brain's review pipeline — where finding stability depends partly on schema stability — this would be the natural cache-invalidation key.

## Query layer depth

1. No FETCH clause modeled. SurrealDB has two ways to pull related data into a result row: nested SELECT … FROM subselects (what the library uses today) and the FETCH field, field clause that follows record-link fields. They're not equivalent: FETCH is the natural shape when you have a [Reference] you want to dereference for one query without restructuring the model. The library only models the nested-SELECT path; [Reference, Inline] is the model-level equivalent. Adding a .With(q => q.Author) form on SurfaceQuery that compiles to FETCH author would be a much lighter shape than today's "make it Inline or load it separately."

2. No IAsyncEnumerable<T>. Every terminal returns IReadOnlyList<T>. For "stream me every finding in a review" — a natural shape for an agent's review pipeline — this materialises the full list in memory before the first row can be processed. SurrealDB does support cursor-style iteration via LIMIT/START paging; the library doesn't expose it as a stream.

3. No "delete-by-query" / "update-by-query." Workspace.Query.TodoItems.Where(...).DeleteAsync(tx) and .UpdateAsync(setter, tx) are missing. The skill mentions "Run-id pruning" as an idiomatic pattern ("save the new run, then delete anything in the run's tables that doesn't match — old data falls out atomically") but the library offers no first-class support — you'd IdsAsync and then loop DeleteAsync per id, or drop to raw SQL. For the review pipeline's "kill all findings from a superseded run" path, this is structurally the wrong shape today.

4. Polymorphic query across shared-shape kinds is out of scope by design (notes.md:283 for preview.55, restated in 56, 57). The reasoning is sound — kinds are distinct edge tables, polymorphism would need a per-kind UNION at the substrate — but it's worth naming explicitly because Project Brain's Calls/References/Inherits symbol relations on CodeSymbol are exactly the shape that wants "give me every outgoing edge of any shared-shape kind from this symbol." Users will hit this and the answer is currently "loop the kinds yourself." A typed shared-shape async query would materially help, even if it's three separate SELECTs under the hood.

## Operational depth

1. No public testing helpers. FakeSurreal is internal to the test assembly. A consumer wanting to test a service that uses SurrealSession has to either stand up a real SurrealDB in their tests (the route the library itself takes via Disruptor.Surface.Sample's harness) or hand-roll the same RecordingConnection pattern. A Disruptor.Surface.Testing package with an InMemorySurreal / capture-and-assert facade would dramatically lower the friction of writing fast unit tests against domain code that uses sessions. Especially for Project Brain's "small number of meaningful integration tests" preference — those tests are fast against an in-memory substitute and slow against a Docker container.

2. No nested transaction / savepoint support. SurrealDB doesn't expose savepoints in the surface SQL that I'm aware of, so this might be a substrate gap rather than a library gap, but worth checking — for the review pipeline's "try a tentative DesignChange in a sub-txn, roll back if it fails validation" shape, savepoints would be the natural primitive.

3. No connection-pooling / retry-with-backoff at the library level. Every example does a single SurrealClient.ConnectAsync and trusts the SDK. For long-running services that need to reconnect across substrate restarts, the library punts entirely to the SDK. Mostly fine, but a Workspace.WithReconnect(...) wrapper would round out the "boot path is two lines" story.

4. No structured logging seam. No Microsoft.Extensions.Logging.ILogger injection, no ILoggerFactory on the session, no per-dispatch log line. CommandLog is the closest thing and it's diagnostic-only, in-memory, and not wired to any logging framework.