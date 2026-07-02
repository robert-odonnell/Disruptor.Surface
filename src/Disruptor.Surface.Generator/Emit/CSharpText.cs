using Microsoft.CodeAnalysis.CSharp;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Shared text-shaping helpers for emitters that interpolate model names / string
/// payloads into generated C#.
/// <list type="bullet">
///   <item><see cref="Identifier"/> — <c>@</c>-escapes C# keywords. Roslyn strips the
///         <c>@</c> from <c>ISymbol.Name</c> (a property declared <c>@class</c> arrives
///         as <c>"class"</c>), so any bare-name interpolation into identifier position
///         must re-escape or the emitted partial fails with a CS1519 cascade. Contextual
///         keywords are escaped too — harmless when unnecessary, and it keeps names like
///         <c>value</c>/<c>async</c> from colliding inside accessor bodies.</item>
///   <item><see cref="Quote"/> / <see cref="EscapeForString"/> — the one correct
///         two-step escape (backslash first, then quote) for baking arbitrary text into
///         a C# string literal. Previously three drifting copies existed; two escaped
///         only the quote.</item>
/// </list>
/// </summary>
internal static class CSharpText
{
    /// <summary>Returns <paramref name="name"/> with a leading <c>@</c> when it is a C# (contextual) keyword; otherwise unchanged.</summary>
    public static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
           || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;

    /// <summary>
    /// Builds a <c>global::</c>-qualified type reference from a namespace + simple name,
    /// <c>@</c>-escaping the name (and each namespace segment) so keyword-named types
    /// (<c>[Table] class @event</c>) stay referenceable from generated code. Use this
    /// instead of hand-interpolating <c>$"global::{ns}.{name}"</c>.
    /// </summary>
    public static string GlobalName(string ns, string name)
    {
        var escaped = Identifier(name);
        if (string.IsNullOrEmpty(ns))
        {
            return $"global::{escaped}";
        }

        var segments = ns.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Identifier(segments[i]);
        }

        return $"global::{string.Join(".", segments)}.{escaped}";
    }

    /// <summary>
    /// Builds a <c>global::</c>-qualified type reference from a full name (dot-separated,
    /// no <c>global::</c> prefix — the <c>TableModel.FullName</c> shape), <c>@</c>-escaping
    /// each segment. Use instead of hand-interpolating <c>$"global::{x.FullName}"</c>.
    /// </summary>
    public static string GlobalType(string fullName)
    {
        var segments = fullName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Identifier(segments[i]);
        }

        return "global::" + string.Join(".", segments);
    }

    /// <summary>Renders <paramref name="s"/> as a quoted C# string literal (backslash-then-quote escape).</summary>
    public static string Quote(string s) => $"\"{EscapeForString(s)}\"";

    /// <summary>Escapes <paramref name="s"/> for inclusion inside a C# string literal (backslash first, then quote).</summary>
    public static string EscapeForString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
