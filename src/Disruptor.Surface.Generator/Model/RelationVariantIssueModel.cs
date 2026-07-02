namespace Disruptor.Surface.Generator.Model;

/// <summary>Which malformed-variant shape pulled a relation variant out of the graph.</summary>
public enum RelationVariantIssueKind
{
    /// <summary>CG046 — the variant declares two or more members carrying the same singular role ([In]/[Out]/[Id]).</summary>
    DuplicateRole,

    /// <summary>CG047 — the variant's [In]/[Out] endpoints stayed unresolved after the shared-shape lift.</summary>
    MissingEndpoints,
}

/// <summary>
/// A relation variant declaration that was pulled out of the model instead of being
/// emitted (mirrors <see cref="NestedTypeIssueModel"/> for CG045). The extractor / linker
/// flag the shape, <c>RelationLinker.Build</c> filters the variant into this issue list,
/// and <c>ModelGenerator.Emit</c> reports CG046/CG047 — previously these shapes were
/// dropped silently and the user got a CS9248 wall (missing partial implementations)
/// with no CG error explaining why.
/// </summary>
/// <param name="FullName">The variant class's full name.</param>
/// <param name="Kind">Which malformed shape was detected.</param>
/// <param name="Detail">Kind-specific message fragment: the duplicated role name ("In"/"Out"/"Id") for <see cref="RelationVariantIssueKind.DuplicateRole"/>, the missing-endpoint description for <see cref="RelationVariantIssueKind.MissingEndpoints"/>.</param>
public sealed record RelationVariantIssueModel(
    string FullName,
    RelationVariantIssueKind Kind,
    string Detail);
