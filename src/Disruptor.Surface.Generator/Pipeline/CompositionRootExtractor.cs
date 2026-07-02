using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Extracts the user's <c>[CompositionRoot]</c>-tagged class. Only one is allowed per
/// compilation; collection-time deduplication happens in <see cref="RelationLinker"/> /
/// <see cref="ModelGenerator"/> where the count can be diagnosed (CG018).
/// </summary>
internal static class CompositionRootExtractor
{
    public static CompositionRootModel? TryExtract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        // Records are admitted so the linker can reject them with CG048 (mirrors
        // TableExtractor) — a [CompositionRoot] record would otherwise be silently
        // ignored by the class-only predicate.
        if (type.TypeKind != TypeKind.Class && !type.IsRecord)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        // NormaliseFullName keeps containing types in the name so a nested
        // [CompositionRoot] — rejected with CG045 by the linker — is reported under
        // its real name. Identical to {ns}.{MetadataName} for namespace-scoped types.
        var fullName = TableExtractor.NormaliseFullName(type);

        return new CompositionRootModel(
            FullName: fullName,
            Namespace: ns,
            Name: type.Name,
            DeclaredAccessibility: type.DeclaredAccessibility.ToString(),
            IsPartial: PartialDeclaration.IsDeclared(type, ct),
            IsNested: type.ContainingType is not null,
            IsRecord: type.IsRecord);
    }
}
