namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// The root model a generated partial is emitted from. Every collection is stored as
/// <see cref="EquatableArray{T}"/> so the entire record is structurally equatable —
/// the incremental generator needs that to skip downstream stages when nothing changed.
/// <para><paramref name="IsNested"/> flags a <c>[Table]</c> declared inside another
/// type; the linker rejects those with CG045 before they reach any emitter.</para>
/// </summary>
public sealed record TableModel(
    string FullName,
    string Namespace,
    string Name,
    bool IsPartial,
    bool IsNested,
    bool IsAbstract,
    bool IsSealed,
    bool IsAggregateRoot,
    string DeclaredAccessibility,
    EquatableArray<string> TypeParameters,
    EquatableArray<PropertyModel> Properties,
    string FileHintName);