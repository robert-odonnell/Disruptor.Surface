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
        var autoValue = ResolveAutoValueKinds(attrs);
        if (kinds == PropertyKind.None && role == RelationRole.None && indexes.Count == 0
            && autoValue == AutoValueKind.None)
        {
            return null;
        }

        // Reference delete behavior — only meaningful for [Reference] members; default
        // is Reject (per spec §10.6: nullable shape never implies Unset). For non-Reference
        // members we still capture explicit-vs-default and multiplicity bits so CG015
        // (delete behavior on [Parent]) can fire.
        var (deletePolicy, hasExplicit, hasMultiple) = ResolveReferenceDelete(attrs);
        var isInline = HasAttribute(attrs, AnnotationsMetadata.Inline);
        var (inlineMembers, inlineConstruction) = ResolveInlineMembers(p.Type);

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
            InlineMembers: inlineMembers,
            InlineConstruction: inlineConstruction,
            Indexes: indexes,
            IsInline: isInline,
            AutoValue: autoValue);
    }

    /// <summary>
    /// Resolves the library-managed value overlays ([CreatedAt] / [UpdatedAt] /
    /// [Version]) carried by the property. Collected as flags — a property carrying
    /// more than one is malformed and fails closed with CG055 at validation.
    /// </summary>
    private static AutoValueKind ResolveAutoValueKinds(ImmutableArray<AttributeData> attrs)
    {
        var kinds = AutoValueKind.None;
        foreach (var attr in attrs)
        {
            kinds |= AttributeFullName(attr) switch
            {
                AnnotationsMetadata.CreatedAt => AutoValueKind.CreatedAt,
                AnnotationsMetadata.UpdatedAt => AutoValueKind.UpdatedAt,
                AnnotationsMetadata.Version   => AutoValueKind.Version,
                _ => AutoValueKind.None,
            };
        }
        return kinds;
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
    /// <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c>) where <c>T</c> is a record / POCO,
    /// resolves the persistable members of <c>T</c> plus the construction shape the
    /// generated Hydrate body uses to rebuild elements, so the schema emitter can produce
    /// <c>scenarios.*.kind</c>-style sub-field DDL and <see cref="Emit.PartialEmitter"/>
    /// can emit typed Hydrate / Save bodies.
    /// <para>
    /// Persistable members are public, instance, non-indexer, readable properties with a
    /// SurrealDB-mappable scalar type (this excludes <c>string</c>'s <c>Length</c>-style
    /// helpers only via the primitive short-circuit below; on element records it excludes
    /// indexers and lets get-only computed members of unmappable types pass as ignored).
    /// The member set must then be constructible: either a public constructor matches it
    /// positionally (name + type per parameter — the positional-record shape), or the
    /// type has a public parameterless constructor and every member is settable (POCO
    /// shape). Anything else returns empty/<see cref="InlineConstructionKind.None"/> and
    /// is rejected with CG050 at validation — previously the naive member walk emitted
    /// un-compilable construction code (CS0443/CS1739/CS1503 in the <c>.g.cs</c>).
    /// </para>
    /// <para>
    /// Primitive/scalar element types (<c>IReadOnlyList&lt;string&gt;</c>,
    /// <c>List&lt;int&gt;</c>, …) return empty with <see cref="InlineConstructionKind.None"/>:
    /// they take the direct <c>array&lt;T&gt;</c> schema/save/hydrate path instead of the
    /// per-member inline expansion.
    /// </para>
    /// </summary>
    private static (EquatableArray<InlineMember> Members, InlineConstructionKind Construction) ResolveInlineMembers(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { Arity: 1 } named)
        {
            return ([], InlineConstructionKind.None);
        }

        var def = named.ConstructedFrom;
        var ns = def.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var isCollection = ns == "System.Collections.Generic" && def.Name is "IReadOnlyList" or "IList" or "List";
        if (!isCollection)
        {
            return ([], InlineConstructionKind.None);
        }

        var element = named.TypeArguments[0];

        // Primitive-element collections take the array<T> direct path — walking string's
        // members here used to produce `new string(this[]: …, Length: …)` (CS0443).
        if (Emit.SchemaEmitter.IsMappableScalar(TypeRefBuilder.Build(element)))
        {
            return ([], InlineConstructionKind.None);
        }

        if (element is not INamedTypeSymbol elementNamed)
        {
            return ([], InlineConstructionKind.None);
        }

        var members = new List<IPropertySymbol>();
        foreach (var member in element.GetMembers())
        {
            // Public instance properties only — covers both classic class properties
            // and record positional parameters (Roslyn synthesises a property per param).
            if (member is not IPropertySymbol prop)
            {
                continue;
            }

            if (prop.IsIndexer || prop.IsStatic || prop.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (prop.GetMethod is null)
            {
                // Set-only property — can't be read at save time, so the element type
                // can't round-trip. Fail closed (CG050 fires at validation).
                return ([], InlineConstructionKind.None);
            }

            if (!Emit.SchemaEmitter.IsMappableScalar(TypeRefBuilder.Build(prop.Type)))
            {
                if (prop.SetMethod is not null)
                {
                    // A writable member we can't persist — saving would silently drop
                    // data on round-trip. Fail closed (CG050).
                    return ([], InlineConstructionKind.None);
                }

                // Get-only computed member of an unmappable type (e.g. `Uri Link =>`)
                // — safely ignorable; it's derived, not stored.
                continue;
            }

            members.Add(prop);
        }

        if (members.Count == 0)
        {
            return ([], InlineConstructionKind.None);
        }

        // Constructibility. Preferred shape: a public constructor whose parameters match
        // the member set positionally by (name, type) — what a positional record gives us.
        foreach (var ctor in elementNamed.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.Parameters.Length != members.Count)
            {
                continue;
            }

            var ordered = new List<InlineMember>(members.Count);
            var allMatched = true;
            foreach (var param in ctor.Parameters)
            {
                var match = members.Find(m =>
                    string.Equals(m.Name, param.Name, StringComparison.Ordinal)
                    && SymbolEqualityComparer.Default.Equals(m.Type, param.Type));
                if (match is null)
                {
                    allMatched = false;
                    break;
                }

                ordered.Add(new InlineMember(match.Name, TypeRefBuilder.Build(match.Type)));
            }

            if (allMatched)
            {
                return (ordered.ToEquatableArray(), InlineConstructionKind.PositionalConstructor);
            }
        }

        // Fallback shape: parameterless public constructor + every member settable
        // (set or init) — hydrate uses an object initializer.
        var hasParameterlessCtor = elementNamed.InstanceConstructors.Any(c =>
            c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0);
        if (hasParameterlessCtor
            && members.All(m => m.SetMethod is { DeclaredAccessibility: Accessibility.Public }))
        {
            var declared = members
                .Select(m => new InlineMember(m.Name, TypeRefBuilder.Build(m.Type)))
                .ToEquatableArray();
            return (declared, InlineConstructionKind.ObjectInitializer);
        }

        return ([], InlineConstructionKind.None);
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

    /// <summary>
    /// Resolves the position-independent identity the index grouping in
    /// <c>RelationLinker.ComputeIndexes</c> relies on:
    /// <list type="bullet">
    ///   <item><c>DeclarationKey</c> — which partial type declaration the property lives
    ///         in, as the declaration's ordinal within the containing type symbol's
    ///         <see cref="ISymbol.DeclaringSyntaxReferences"/>. Composite indexes must
    ///         keep all fields in one declaration (CG038), and the ordinal distinguishes
    ///         partial declarations without embedding a file path.</item>
    ///   <item><c>SourceOrder</c> — the property's member ordinal within that type
    ///         declaration, giving composite indexes their column order (= property
    ///         declaration order).</item>
    /// </list>
    /// Both values are stable under edits elsewhere in the file. The previous encoding
    /// (<c>"{filePath}:{typeDeclSpanStart}"</c> + property <c>SpanStart</c>) used absolute
    /// positions, so ANY edit above an index-annotated property (a comment, a using)
    /// changed model equality and re-emitted every file — the exact cache regression the
    /// incremental-generator contract warns about — and was machine-path-sensitive.
    /// </summary>
    private static (string DeclarationKey, int SourceOrder) ResolveSourceLocation(IPropertySymbol property)
    {
        var syntaxRef = property.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef?.GetSyntax() is not PropertyDeclarationSyntax declaration)
        {
            return (string.Empty, int.MaxValue);
        }

        var typeDeclaration = declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration is null)
        {
            return (string.Empty, int.MaxValue);
        }

        var declarationOrdinal = 0;
        var containingType = property.ContainingType;
        if (containingType is not null)
        {
            var references = containingType.DeclaringSyntaxReferences;
            for (var i = 0; i < references.Length; i++)
            {
                if (references[i].SyntaxTree == typeDeclaration.SyntaxTree
                    && references[i].Span == typeDeclaration.Span)
                {
                    declarationOrdinal = i;
                    break;
                }
            }
        }

        var memberOrdinal = typeDeclaration.Members.IndexOf(declaration);
        return ($"decl:{declarationOrdinal}", memberOrdinal < 0 ? int.MaxValue : memberOrdinal);
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
