namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Marker for a node in the predicate AST. Compiled by <see cref="SurfaceQueryCompiler"/> into a
/// SurrealQL <c>WHERE</c> fragment plus a parameter map. Strings (field names) are baked
/// in at the factory layer (<see cref="PropertyExpr{T}"/>) — this surface is intentionally
/// untyped so the compiler stays narrow and the same AST shape can serve every operator
/// the factory layer exposes.
/// </summary>
public interface IPredicate;

/// <summary>
/// Equality test: <c>field = $param</c>. A <c>null</c> <see cref="Value"/> compiles to
/// the unset test <c>(field IS NONE OR field IS NULL)</c> instead of a binding — the
/// write path omits keys for null values, so stored nulls are NONE (absent fields) and
/// <c>field = NULL</c> would never match them.
/// </summary>
public sealed record EqPredicate(string Field, object? Value) : IPredicate;

/// <summary>Field-unset test: <c>(field IS NONE OR field IS NULL)</c>. The named form of <c>Eq(null)</c>.</summary>
public sealed record IsNonePredicate(string Field) : IPredicate;

/// <summary>Field-set test: <c>(field IS NOT NONE AND field IS NOT NULL)</c>. The complement of <see cref="IsNonePredicate"/>.</summary>
public sealed record IsNotNonePredicate(string Field) : IPredicate;

/// <summary>Comparison operators emitted as <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>.</summary>
public enum RangeOp
{
    Lt,
    Le,
    Gt,
    Ge
}

/// <summary>Range comparison: <c>field {op} $param</c>.</summary>
public sealed record RangePredicate(string Field, RangeOp Op, object? Value) : IPredicate;

/// <summary>
/// Set membership: <c>field IN $param</c>. The parameter renderer expands a collection
/// value into <c>[v0, v1, …]</c>, so a single bound parameter holds the whole array.
/// </summary>
public sealed record InPredicate(string Field, IReadOnlyList<object?> Values) : IPredicate;

/// <summary>
/// Negative set membership: <c>field NOT IN $param</c>. The parameter shape matches
/// <see cref="InPredicate"/>.
/// </summary>
public sealed record NotInPredicate(string Field, IReadOnlyList<object?> Values) : IPredicate;

/// <summary>
/// Inclusive range: <c>field &gt;= $lower AND field &lt;= $upper</c>. Kept as one AST node
/// so callers can express the common "between" shape without hand-composing two ranges.
/// </summary>
public sealed record BetweenPredicate(string Field, object? Lower, object? Upper) : IPredicate;

/// <summary>
/// Substring match for string fields. Compiles to <c>string::contains(field, $param)</c>.
/// Case-sensitive — SurrealDB's <c>string::contains</c> doesn't fold case; pre-lower the
/// substring or wrap in a case-insensitive variant if the schema needs one.
/// </summary>
public sealed record ContainsPredicate(string Field, string Substring) : IPredicate;

/// <summary>String prefix match: <c>string::starts_with(field, $param)</c>.</summary>
public sealed record StartsWithPredicate(string Field, string Prefix) : IPredicate;

/// <summary>String suffix match: <c>string::ends_with(field, $param)</c>.</summary>
public sealed record EndsWithPredicate(string Field, string Suffix) : IPredicate;

/// <summary>
/// String null-or-empty test: <c>(field IS NONE OR field IS NULL OR field = '')</c>.
/// NONE-aware for the same reason as <see cref="IsNonePredicate"/> — the write path
/// stores unset optionals as NONE (absent key) — with the empty-string arm on top so
/// the predicate matches C#'s <c>string.IsNullOrEmpty</c> semantics.
/// </summary>
public sealed record IsNullOrEmptyPredicate(string Field) : IPredicate;

/// <summary>
/// String has-content test: <c>(field IS NOT NONE AND field IS NOT NULL AND field != '')</c>
/// — the exact complement of <see cref="IsNullOrEmptyPredicate"/>.
/// </summary>
public sealed record IsNotNullOrEmptyPredicate(string Field) : IPredicate;

/// <summary>
/// Regex match for string fields. Compiles to <c>string::matches(field, $param)</c> —
/// SurrealDB's regex primitive that accepts the pattern as a bound string parameter.
/// (The <c>~</c> / <c>?~</c> / <c>*~</c> operators are <i>fuzzy</i>-match operators in
/// SurrealQL, not regex, so they are deliberately not used here.)
/// </summary>
public sealed record MatchesPredicate(string Field, string Pattern) : IPredicate;

/// <summary>
/// Case-insensitive substring match:
/// <c>string::contains(string::lowercase(field), $param)</c> with the operand
/// lowercased invariantly at compile time.
/// </summary>
public sealed record ContainsIgnoreCasePredicate(string Field, string Substring) : IPredicate;

/// <summary>Case-insensitive prefix match: <c>string::starts_with(string::lowercase(field), $param)</c>.</summary>
public sealed record StartsWithIgnoreCasePredicate(string Field, string Prefix) : IPredicate;

/// <summary>Case-insensitive suffix match: <c>string::ends_with(string::lowercase(field), $param)</c>.</summary>
public sealed record EndsWithIgnoreCasePredicate(string Field, string Suffix) : IPredicate;

/// <summary>Logical conjunction. Operands are AND-merged in compile order.</summary>
public sealed record AndPredicate(IReadOnlyList<IPredicate> Operands) : IPredicate;

/// <summary>Logical disjunction. Operands are OR-merged in compile order.</summary>
public sealed record OrPredicate(IReadOnlyList<IPredicate> Operands) : IPredicate;

/// <summary>Logical negation.</summary>
public sealed record NotPredicate(IPredicate Operand) : IPredicate;
