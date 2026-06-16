namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Index attribute occurrence found on a table property. The attribute type names the
/// index; applying the same attribute to multiple properties creates a composite index.
/// </summary>
public sealed record IndexAnnotationModel(
    string AttributeFullName,
    string AttributeName,
    bool IsUnique,
    string DeclarationKey,
    int SourceOrder);

public sealed record IndexFieldModel(
    string PropertyName,
    string FieldName,
    string TypeFullName,
    bool IsNullable);

public sealed record IndexModel(
    string TableFullName,
    string TableName,
    string AttributeFullName,
    string AttributeName,
    string SchemaName,
    bool IsUnique,
    EquatableArray<IndexFieldModel> Fields);

public enum IndexIssueKind
{
    UnsupportedField,
    DuplicateField,
    SplitCompositeDeclaration,
    NullableUniqueField,
    NameCollision,
}

public sealed record IndexIssueModel(
    IndexIssueKind Kind,
    string TableFullName,
    string IndexAttributeFullName,
    string SchemaName,
    string? PropertyName,
    string Detail);
