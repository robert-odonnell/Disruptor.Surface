namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One attributed property member inside a <see cref="TypeDeclarationLocations"/> entry —
/// the member's name plus the location of its identifier token.
/// </summary>
public sealed record MemberLocation(
    string Name,
    LocationInfo Location);

/// <summary>
/// Per-type-declaration location map entry produced by
/// <c>Pipeline.DeclarationLocationExtractor</c> — the syntax-only side channel that lets
/// the diagnostics output point healthy-model diagnostics (CG001, CG011, CG022, …) at
/// the offending declaration WITHOUT putting position-sensitive data on
/// <c>TableModel</c> / <c>PropertyModel</c> (which would bust the emitters' incremental
/// cache on every edit).
/// </summary>
/// <param name="TypeKey">The declaration's full name in <c>TableExtractor.NormaliseFullName</c>
/// format (<c>Ns.Outer.Inner</c>, metadata arity suffix for generics) so diagnostics keyed
/// by model full names resolve against it.</param>
/// <param name="Location">Location of the type declaration's identifier token.</param>
/// <param name="Members">Identifier locations of the declaration's attributed properties,
/// for member-level diagnostics (CG022, CG025, CG037, CG052, …).</param>
public sealed record TypeDeclarationLocations(
    string TypeKey,
    LocationInfo Location,
    EquatableArray<MemberLocation> Members);
