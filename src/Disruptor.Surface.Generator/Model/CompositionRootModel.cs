namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// The user's <c>[CompositionRoot]</c>-tagged class. Holds just enough metadata to emit
/// a partial declaration that grafts the per-aggregate <c>Load{Root}Async</c> instance
/// methods onto it. The class itself is the user's domain — accessibility, ctors,
/// dependencies are entirely their concern; the generator only contributes the load
/// methods.
/// <para><paramref name="IssueLocation"/> is captured ONLY for declarations the linker
/// will reject (nested — CG045, record — CG048) and MUST stay <c>null</c> otherwise;
/// see <see cref="TableModel.IssueLocation"/> for the caching rationale.</para>
/// </summary>
public sealed record CompositionRootModel(
    string FullName,
    string Namespace,
    string Name,
    string DeclaredAccessibility,
    bool IsPartial,
    bool IsNested,
    bool IsRecord,
    LocationInfo? IssueLocation);
