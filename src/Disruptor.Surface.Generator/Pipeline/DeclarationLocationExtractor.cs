using System.Collections.Immutable;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Builds the declaration-location side channel: for every type declaration in the
/// compilation, one <see cref="TypeDeclarationLocations"/> mapping the type's
/// <c>NormaliseFullName</c>-format key (and each attributed property member) to the
/// identifier token's <see cref="LocationInfo"/>.
/// <para>
/// This provider is position-sensitive BY DESIGN — its output changes on every edit that
/// shifts a declaration. That is only safe because it feeds the diagnostics-only
/// <c>RegisterSourceOutput</c> in <c>ModelGenerator</c> (combined with the
/// position-independent pending-diagnostics node), never the emitters' input. Do not
/// combine it into the <c>ModelGraph</c> node: that would make every emitter
/// position-sensitive — the exact regression the incremental-generator contract warns
/// about (see the tranche-2 <c>IndexAnnotationModel</c> fix in <c>docs/notes.md</c>).
/// </para>
/// <para>
/// Both predicate and transform are syntax-only (no semantic model): the full-name key is
/// reconstructed from the namespace / containing-type ancestor chain, matching
/// <see cref="TableExtractor.NormaliseFullName"/> (metadata arity suffix included) so
/// model full names resolve against it.
/// </para>
/// </summary>
internal static class DeclarationLocationExtractor
{
    /// <summary>Syntactic predicate — any class / record / struct / interface declaration.</summary>
    public static bool IsTypeDeclaration(SyntaxNode node, CancellationToken _)
        => node is TypeDeclarationSyntax;

    public static TypeDeclarationLocations? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.Node is not TypeDeclarationSyntax decl)
        {
            return null;
        }

        var typeLocation = LocationInfo.FromToken(decl.Identifier);
        if (typeLocation is null)
        {
            return null;
        }

        var members = ImmutableArray.CreateBuilder<MemberLocation>();
        foreach (var member in decl.Members)
        {
            // Only attributed properties participate: every member-level CG diagnostic
            // targets a model-annotated property, and skipping bare members keeps the
            // side channel small.
            if (member is not PropertyDeclarationSyntax { AttributeLists.Count: > 0 } property)
            {
                continue;
            }

            if (LocationInfo.FromToken(property.Identifier) is { } memberLocation)
            {
                members.Add(new MemberLocation(property.Identifier.ValueText, memberLocation));
            }
        }

        return new TypeDeclarationLocations(
            TypeKey: BuildTypeKey(decl),
            Location: typeLocation,
            Members: new EquatableArray<MemberLocation>(members.ToImmutable()));
    }

    /// <summary>
    /// Reconstructs <see cref="TableExtractor.NormaliseFullName"/> from syntax:
    /// namespace parts (outer-to-inner, each possibly dotted) + containing type
    /// metadata names (identifier plus <c>`n</c> arity suffix) joined with dots.
    /// </summary>
    private static string BuildTypeKey(TypeDeclarationSyntax decl)
    {
        var parts = new Stack<string>();
        SyntaxNode? current = decl;
        while (current is not null)
        {
            switch (current)
            {
                case TypeDeclarationSyntax type:
                    parts.Push(MetadataName(type));
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Push(ns.Name.ToString());
                    break;
            }

            current = current.Parent;
        }

        return string.Join(".", parts);
    }

    private static string MetadataName(TypeDeclarationSyntax type)
    {
        var arity = type.TypeParameterList?.Parameters.Count ?? 0;
        return arity == 0 ? type.Identifier.ValueText : $"{type.Identifier.ValueText}`{arity}";
    }
}
