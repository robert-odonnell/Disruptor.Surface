namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// The root model a generated partial is emitted from. Every collection is stored as
/// <see cref="EquatableArray{T}"/> so the entire record is structurally equatable —
/// the incremental generator needs that to skip downstream stages when nothing changed.
/// <para><paramref name="IsNested"/> flags a <c>[Table]</c> declared inside another
/// type; the linker rejects those with CG045 before they reach any emitter.
/// <paramref name="IsRecord"/> flags a <c>[Table]</c> on a record declaration; the
/// linker rejects those with CG048.</para>
/// <para><paramref name="IssueLocation"/> is the declaration's identifier location,
/// captured ONLY when the extractor already knows the table will be rejected
/// (nested / record / generic — CG045 / CG048 / CG049). It MUST stay <c>null</c> on
/// healthy tables: a populated <see cref="LocationInfo"/> makes model equality
/// position-sensitive, which is acceptable on an already-failing build but would bust
/// the incremental cache for every consumer edit otherwise.</para>
/// </summary>
public sealed record TableModel(
    string FullName,
    string Namespace,
    string Name,
    bool IsPartial,
    bool IsNested,
    bool IsRecord,
    bool IsAbstract,
    bool IsSealed,
    bool IsAggregateRoot,
    string DeclaredAccessibility,
    EquatableArray<string> TypeParameters,
    EquatableArray<PropertyModel> Properties,
    string FileHintName,
    LocationInfo? IssueLocation);