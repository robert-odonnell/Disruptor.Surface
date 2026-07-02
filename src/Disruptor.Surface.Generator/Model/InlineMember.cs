namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One member of an inline-record element type used inside an inline-element collection
/// <c>[Property]</c> (<c>IReadOnlyList&lt;T&gt;</c> / <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c> of records).
/// Captured at extraction time so <see cref="Disruptor.Surface.Generator.Emit.SchemaEmitter"/>
/// can emit per-member sub-field DDL (e.g. <c>scenarios.*.kind</c>, <c>scenarios.*.description</c>)
/// and so <see cref="Disruptor.Surface.Generator.Emit.PartialEmitter"/> can emit typed
/// Hydrate / Save bodies without re-resolving the type at emit time.
/// </summary>
public sealed record InlineMember(string Name, TypeRef Type);

/// <summary>
/// How the generated Hydrate body reconstructs an inline-collection element from a row.
/// Decided at extraction time (<c>TableExtractor.ResolveInlineMembers</c>) because it
/// needs constructor symbols; the emitters only replay the decision.
/// </summary>
public enum InlineConstructionKind
{
    /// <summary>Not an inline-record collection (scalar property, primitive-element collection, or an unsupported element shape rejected via CG050).</summary>
    None,

    /// <summary>Element type has a public constructor whose parameters match the mappable members by name and type (positional record shape) — hydrate emits <c>new T(Name: …, …)</c>.</summary>
    PositionalConstructor,

    /// <summary>Element type has a public parameterless constructor and every mappable member is publicly settable (POCO shape) — hydrate emits <c>new T { Name = …, … }</c>.</summary>
    ObjectInitializer,
}
