namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Describes one failed merge while lifting annotated members from a shared-shape
/// interface into a relation variant.
/// </summary>
public sealed record SharedShapeLiftConflict(
    string VariantFullName,
    string InterfaceFullName,
    RelationVariantPropertyRole ExistingRole,
    string ExistingName,
    string ExistingTypeFullName,
    bool ExistingNullable,
    RelationVariantPropertyRole LiftedRole,
    string LiftedName,
    string LiftedTypeFullName,
    bool LiftedNullable);
