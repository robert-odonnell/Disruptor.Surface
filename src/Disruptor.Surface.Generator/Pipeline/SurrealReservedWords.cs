using System.Collections.Generic;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// SurrealQL reserved identifiers, split by failure mode. Sourced from the
/// <c>RESERVED_KEYWORD</c> set in <c>surrealdb/crates/core/src/syn/lexer/keywords.rs</c>
/// @ tag <c>v3.1.4</c> — the exact set SurrealDB's own <c>EscapeIdent</c> serializer
/// backtick-quotes. Compared against the rendered (snake_cased, lowercased) identifier.
/// If the pinned SurrealDB version is bumped, re-pull that file — the set grows across releases.
/// </summary>
internal static class SurrealReservedWords
{
    /// <summary>Value literals intercepted before the identifier fallback in
    /// <c>parse_prime_expr</c>: a bare occurrence silently becomes the literal, not a
    /// field reference (no error). CG058 (Error).</summary>
    public static readonly HashSet<string> ValueLiterals = new(System.StringComparer.Ordinal)
    {
        "none", "null", "true", "false",
    };

    /// <summary>The remaining 40 words of <c>RESERVED_KEYWORD</c>: emitted bare they fail
    /// <em>loudly</em> (parse/apply error, caught at dev/apply time) rather than corrupting
    /// silently — hence a warning, not an error. Backtick-quoting rescues them <em>per word</em>,
    /// not uniformly: statement keywords like <c>select</c> still throw in DML even quoted
    /// (see <c>docs/live-validation-2026-07-03.md</c> §3, B2.18–B2.20). CG059 (Warning).</summary>
    public static readonly HashSet<string> ReservedKeywords = new(System.StringComparer.Ordinal)
    {
        "after", "all", "alter", "before", "begin", "break", "by", "cancel", "commit",
        "continue", "create", "define", "delete", "diff", "for", "function", "if", "info",
        "insert", "kill", "let", "live", "option", "rand", "rebuild", "relate", "remove",
        "return", "select", "sequence", "show", "sleep", "table", "tb", "throw", "update",
        "upsert", "use", "value", "where",
    };
}
