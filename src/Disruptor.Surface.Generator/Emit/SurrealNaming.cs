using System.Globalization;
using Humanizer;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Translates C# type and member names into SurrealDB naming conventions.
/// <list type="bullet">
///   <item><c>ToFieldName</c> — lower_snake_case for table fields and reference/parent
///         column names (driven off C# property/method logical names).</item>
///   <item><c>ToTableName</c> — lower_snake_case + pluralised, for SurrealDB table
///         names (driven off the entity class name).</item>
///   <item><c>ToEdgeName</c> — lower_snake_case after stripping the trailing
///         <c>Attribute</c> suffix, for forward/inverse relation kind names.</item>
/// </list>
/// All work is at codegen time — string literals are baked into the emitted source so
/// the runtime never needs to know about C# type names. Type-parameter dispatch
/// (e.g. <c>typeof(T).Name</c>) is left to callers because the only such case
/// (generic <c>[Children]</c>) is blocked by CG009.
/// <para>
/// Every Humanizer call runs under the invariant culture: Humanizer 3.x lower-cases
/// (<c>Underscore</c>) and regex-matches (<c>Pluralize</c>/<c>Singularize</c>) with the
/// thread's current culture, so a tr-TR build machine would bake <c>ıssues</c> (dotless
/// ı, U+0131) instead of <c>issues</c> into the schema — generated code still compiles,
/// but the schema is silently incompatible with everyone else's.
/// </para>
/// </summary>
internal static class SurrealNaming
{
    public static string ToFieldName(string memberName) =>
        Invariant(memberName, static s => s.Underscore());

    /// <summary>
    /// <c>Pluralize(inputIsKnownToBeSingular: false)</c> tells Humanizer to detect
    /// already-plural input and leave it alone — covers <c>Details</c> (stays
    /// <c>details</c>) and <c>AcceptanceCriteria</c> (stays <c>acceptance_criteria</c>)
    /// without us hand-rolling the rules.
    /// </summary>
    public static string ToTableName(string typeName) =>
        Invariant(typeName, static s => s.Pluralize(inputIsKnownToBeSingular: false).Underscore());

    public static string ToEdgeName(string attributeClassName) =>
        Invariant(StripAttributeSuffix(attributeClassName), static s => s.Underscore());

    /// <summary>
    /// Pascal-cased pluralisation of a C# type name (e.g. <c>Constraint</c> →
    /// <c>Constraints</c>, <c>Details</c> stays <c>Details</c>). What
    /// <see cref="ToTableName"/> does minus the snake-casing — used for generated member
    /// names (query/hydration root accessors) that must stay valid C# identifiers.
    /// </summary>
    public static string PascalPluralize(string typeName) =>
        Invariant(typeName, static s => s.Pluralize(inputIsKnownToBeSingular: false));

    /// <summary>
    /// English singularizer — used for derived names where a forward relation attribute
    /// (a verb in third-person singular form, like <c>Restricts</c>) needs to be turned
    /// into a noun-ish marker name (<c>IRestrict</c>). Past-participle names like
    /// <c>RestrictedBy</c> are already singular and pass through untouched.
    /// </summary>
    public static string Singularize(string word) =>
        Invariant(word, static s => s.Singularize(inputIsKnownToBePlural: false));

    /// <summary>
    /// Reduces a fully-qualified type name (with optional <c>global::</c> prefix,
    /// generic args, and trailing nullability marker) to its bare type identifier.
    /// </summary>
    public static string SimpleName(string fullyQualifiedName)
    {
        var name = fullyQualifiedName;
        var lt = name.IndexOf('<');
        if (lt >= 0)
        {
            name = name[..lt];
        }

        if (name.EndsWith("?", StringComparison.Ordinal))
        {
            name = name[..^1];
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        return name;
    }

    public static string StripAttributeSuffix(string s) =>
        s.EndsWith("Attribute", StringComparison.Ordinal) ? s[..^"Attribute".Length] : s;

    /// <summary>
    /// Runs <paramref name="transform"/> with <see cref="CultureInfo.CurrentCulture"/>
    /// swapped to the invariant culture, restoring the caller's culture afterwards.
    /// Keeps every baked identifier machine-independent regardless of build locale.
    /// </summary>
    /// <remarks>
    /// RS1035 bans <c>CultureInfo.CurrentCulture</c> in analyzers because culture-
    /// <em>dependent</em> behaviour breaks build determinism. This helper exists to do
    /// the exact opposite — Humanizer offers no culture parameter, so pinning the
    /// thread's culture to invariant for the duration of the call is the only way to
    /// make its output deterministic. The original culture is always restored.
    /// </remarks>
    private static string Invariant(string input, Func<string, string> transform)
    {
#pragma warning disable RS1035 // Deliberate: pins CurrentCulture to invariant around culture-sensitive Humanizer calls.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return transform(input);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
#pragma warning restore RS1035
    }
}
