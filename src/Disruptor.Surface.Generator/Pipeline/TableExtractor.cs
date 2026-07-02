using System.Collections.Immutable;
using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

internal static class TableExtractor
{
    public static TableModel? TryExtract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        // Records are admitted (the FAWMN predicate matches record declarations) so the
        // linker can reject them with CG048 — previously `[Table] partial record X`
        // compiled clean and generated nothing. Non-record structs/interfaces can't
        // carry [Table] (AttributeTargets.Class), so the class-or-record guard is
        // purely defensive.
        if (type.TypeKind != TypeKind.Class && !type.IsRecord)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var isPartial = PartialDeclaration.IsDeclared(type, ct);
        var isAggregateRoot = HasAttribute(type, AnnotationsMetadata.AggregateRoot);

        var typeParameters = type.TypeParameters.Select(p => p.Name).ToEquatableArray();

        var propertiesBuilder = ImmutableArray.CreateBuilder<PropertyModel>();

        foreach (var member in type.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is IPropertySymbol p && TryBuildProperty(p) is { } pm)
            {
                propertiesBuilder.Add(pm);
            }
        }

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        // NormaliseFullName keeps containing types in the name (Ns.Outer.Inner) so a
        // nested [Table] — rejected with CG045 by the linker — is reported under its
        // real name. For namespace-scoped types it's identical to {ns}.{MetadataName}.
        var fullName = NormaliseFullName(type);
        var hint = $"{fullName}.g.cs";

        return new TableModel(
            FullName: fullName,
            Namespace: ns,
            Name: type.Name,
            IsPartial: isPartial,
            IsNested: type.ContainingType is not null,
            IsRecord: type.IsRecord,
            IsAbstract: type.IsAbstract,
            IsSealed: type.IsSealed,
            IsAggregateRoot: isAggregateRoot,
            DeclaredAccessibility: type.DeclaredAccessibility.ToString(),
            TypeParameters: typeParameters,
            Properties: propertiesBuilder.ToImmutable(),
            FileHintName: hint);
    }

    private static PropertyModel? TryBuildProperty(IPropertySymbol p)
    {
        var attrs = p.GetAttributes();
        var kinds = ResolvePropertyKinds(attrs);
        var (role, kindFullName) = ResolveRelationRole(attrs);
        var indexes = ResolveIndexAnnotations(attrs, p);
        if (kinds == PropertyKind.None && role == RelationRole.None && indexes.Count == 0)
        {
            return null;
        }

        // Reference delete behavior — only meaningful for [Reference] members; default
        // is Reject (per spec §10.6: nullable shape never implies Unset). For non-Reference
        // members we still capture explicit-vs-default and multiplicity bits so CG015
        // (delete behavior on [Parent]) can fire.
        var (deletePolicy, hasExplicit, hasMultiple) = ResolveReferenceDelete(attrs);
        var isInline = HasAttribute(attrs, AnnotationsMetadata.Inline);

        return new PropertyModel(
            Name: p.Name,
            Type: TypeRefBuilder.Build(p.Type),
            Kinds: kinds,
            RelationRole: role,
            RelationKindFullName: kindFullName,
            ReferenceDelete: deletePolicy,
            HasExplicitDeleteBehavior: hasExplicit,
            HasMultipleDeleteBehaviors: hasMultiple,
            HasGetter: p.GetMethod is not null,
            HasSetter: p.SetMethod is { IsInitOnly: false },
            HasInitOnlySetter: p.SetMethod is { IsInitOnly: true },
            IsPartial: PartialDeclaration.IsMember(p),
            IsStatic: p.IsStatic,
            DeclaredAccessibility: p.DeclaredAccessibility.ToString(),
            InlineMembers: ResolveInlineMembers(p.Type),
            Indexes: indexes,
            IsInline: isInline);
    }

    private static bool HasAttribute(ImmutableArray<AttributeData> attrs, string fullName)
    {
        foreach (var attr in attrs)
        {
            if (AttributeFullName(attr) == fullName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// For an inline-element collection property (<c>IReadOnlyList&lt;T&gt;</c> /
    /// <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c>) where <c>T</c> is a record / POCO with
    /// public instance properties, walks <c>T</c>'s members so the schema emitter can
    /// produce <c>scenarios.*.kind</c>-style sub-field DDL and <see cref="PartialEmitter"/>
    /// can emit typed Hydrate / Save bodies. Returns empty for primitive-element
    /// collections (<c>IReadOnlyList&lt;int&gt;</c>) and anything that isn't a
    /// supported collection shape.
    /// </summary>
    private static EquatableArray<InlineMember> ResolveInlineMembers(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Arity: 1 } named)
        {
            return [];
        }

        var def = named.ConstructedFrom;
        var ns = def.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var isCollection = ns == "System.Collections.Generic" && def.Name is "IReadOnlyList" or "IList" or "List";
        if (!isCollection)
        {
            return [];
        }

        var element = named.TypeArguments[0];
        var members = new List<InlineMember>();
        foreach (var member in element.GetMembers())
        {
            // Public instance properties only — covers both classic class properties
            // and record positional parameters (Roslyn synthesises a property per param).
            if (member is not IPropertySymbol prop)
            {
                continue;
            }

            if (prop.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (prop.IsStatic)
            {
                continue;
            }

            if (prop.GetMethod is null)
            {
                continue;
            }

            members.Add(new InlineMember(prop.Name, TypeRefBuilder.Build(prop.Type)));
        }
        return members.ToEquatableArray();
    }

    private static (ReferenceDeletePolicy Policy, bool HasExplicit, bool HasMultiple) ResolveReferenceDelete(ImmutableArray<AttributeData> attrs)
    {
        var found = new List<ReferenceDeletePolicy>();
        foreach (var attr in attrs)
        {
            var fqn = AttributeFullName(attr);
            switch (fqn)
            {
                case null: continue;
                case AnnotationsMetadata.Reject: found.Add(ReferenceDeletePolicy.Reject); break;
                case AnnotationsMetadata.Unset: found.Add(ReferenceDeletePolicy.Unset); break;
                case AnnotationsMetadata.Cascade: found.Add(ReferenceDeletePolicy.Cascade); break;
                case AnnotationsMetadata.Ignore: found.Add(ReferenceDeletePolicy.Ignore); break;
            }
        }
        var policy = found.Count > 0 ? found[0] : ReferenceDeletePolicy.Reject;
        return (policy, found.Count > 0, found.Count > 1);
    }

    private static PropertyKind ResolvePropertyKinds(ImmutableArray<AttributeData> attrs)
    {
        var kinds = PropertyKind.None;
        foreach (var attr in attrs)
        {
            var fqn = AttributeFullName(attr);
            if (fqn is null)
            {
                continue;
            }

            kinds |= fqn switch
            {
                AnnotationsMetadata.Id         => PropertyKind.Id,
                AnnotationsMetadata.Property   => PropertyKind.Property,
                AnnotationsMetadata.Parent     => PropertyKind.Parent,
                AnnotationsMetadata.Children   => PropertyKind.Children,
                AnnotationsMetadata.Reference  => PropertyKind.Reference,
                _ => PropertyKind.None,
            };
        }
        return kinds;
    }

    /// <summary>
    /// A property joins the relation side of the model when one of its attributes derives
    /// from <c>ForwardRelation</c> or <c>InverseRelation&lt;T&gt;</c>.
    /// Returns the role plus the fully-qualified attribute name so the linker can pair
    /// forward/inverse kinds later.
    /// </summary>
    private static (RelationRole Role, string? KindFullName) ResolveRelationRole(ImmutableArray<AttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            var cls = attr.AttributeClass;
            if (cls is null)
            {
                continue;
            }

            if (InheritsFromForwardRelation(cls))
            {
                return (RelationRole.ForwardRelation, AttributeFullName(attr));
            }

            if (InheritsFromInverseRelation(cls))
            {
                return (RelationRole.InverseRelation, AttributeFullName(attr));
            }
        }
        return (RelationRole.None, null);
    }

    private static EquatableArray<IndexAnnotationModel> ResolveIndexAnnotations(
        ImmutableArray<AttributeData> attrs,
        IPropertySymbol property)
    {
        var (declarationKey, sourceOrder) = ResolveSourceLocation(property);
        var indexes = ImmutableArray.CreateBuilder<IndexAnnotationModel>();

        foreach (var attr in attrs)
        {
            var cls = attr.AttributeClass;
            if (cls is null)
            {
                continue;
            }

            if (!InheritsFrom(cls, AnnotationsMetadata.Index))
            {
                continue;
            }

            var fullName = AttributeFullName(attr);
            if (fullName is null)
            {
                continue;
            }

            indexes.Add(new IndexAnnotationModel(
                AttributeFullName: fullName,
                AttributeName: cls.Name,
                IsUnique: InheritsFrom(cls, AnnotationsMetadata.UniqueIndex),
                DeclarationKey: declarationKey,
                SourceOrder: sourceOrder));
        }

        return new EquatableArray<IndexAnnotationModel>(indexes.ToImmutable());
    }

    private static (string DeclarationKey, int SourceOrder) ResolveSourceLocation(IPropertySymbol property)
    {
        var syntaxRef = property.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef?.GetSyntax() is not PropertyDeclarationSyntax declaration)
        {
            return (string.Empty, int.MaxValue);
        }

        var typeDeclaration = declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var filePath = declaration.SyntaxTree.FilePath ?? string.Empty;
        var declarationStart = typeDeclaration?.SpanStart ?? 0;
        return ($"{filePath}:{declarationStart}", declaration.SpanStart);
    }

    internal static bool InheritsFromForwardRelation(INamedTypeSymbol cls)
    {
        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (NormaliseFullName(current) == AnnotationsMetadata.ForwardRelation)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool InheritsFromInverseRelation(INamedTypeSymbol cls)
    {
        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType &&
                NormaliseFullName(current.ConstructedFrom) == AnnotationsMetadata.InverseRelation)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool InheritsFrom(INamedTypeSymbol cls, string metadataName)
    {
        for (INamedTypeSymbol? current = cls; current is not null; current = current.BaseType)
        {
            if (NormaliseFullName(current) == metadataName)
            {
                return true;
            }
        }

        return false;
    }

    internal static string? AttributeFullName(AttributeData attr) => attr.AttributeClass is null ? null : NormaliseFullName(attr.AttributeClass);

    internal static string NormaliseFullName(INamedTypeSymbol symbol)
    {
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var parts = new Stack<string>();
        for (INamedTypeSymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            parts.Push(current.MetadataName);
        }

        var name = string.Join(".", parts);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static bool HasAttribute(INamedTypeSymbol type, string attributeFullMetadataName)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass is null)
            {
                continue;
            }

            if (NormaliseFullName(attr.AttributeClass) == attributeFullMetadataName)
            {
                return true;
            }
        }
        return false;
    }

}
