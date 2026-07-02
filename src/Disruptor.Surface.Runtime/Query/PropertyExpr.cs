namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Typed accessor for a single SurrealDB column on a <c>[Table]</c>. The generator emits
/// one <c>readonly PropertyExpr&lt;T&gt;</c> per <c>[Property]</c>/<c>[Id]</c> on the
/// table's <c>{Name}Q</c> static class — e.g. <c>ConstraintQ.Description</c> is
/// <c>PropertyExpr&lt;string&gt;("description")</c>.
/// <para>
/// The struct's role is to keep the compile-time type of the value flowing into the
/// predicate — <c>Eq(T value)</c> rejects mismatched types at the call site instead of
/// silently boxing them. Once a predicate node is constructed the type tag is dropped:
/// the AST stores <c>object?</c> values; the transport's RPC layer serialises each
/// binding into the JSON-RPC payload and SurrealDB binds them server-side.
/// </para>
/// </summary>
public readonly record struct PropertyExpr<T>(string Field)
{
    /// <summary>Equality predicate: <c>field = value</c>.</summary>
    public IPredicate Eq(T value) => new EqPredicate(Field, value);

    /// <summary>Less-than: <c>field &lt; value</c>.</summary>
    public IPredicate Lt(T value) => new RangePredicate(Field, RangeOp.Lt, value);

    /// <summary>Less-than-or-equal: <c>field &lt;= value</c>.</summary>
    public IPredicate Le(T value) => new RangePredicate(Field, RangeOp.Le, value);

    /// <summary>Greater-than: <c>field &gt; value</c>.</summary>
    public IPredicate Gt(T value) => new RangePredicate(Field, RangeOp.Gt, value);

    /// <summary>Greater-than-or-equal: <c>field &gt;= value</c>.</summary>
    public IPredicate Ge(T value) => new RangePredicate(Field, RangeOp.Ge, value);

    /// <summary>Set membership: <c>field IN [v0, v1, …]</c>.</summary>
    public IPredicate In(params T[] values) => new InPredicate(Field, ToObjectArray(values));

    /// <summary>Set membership: <c>field IN [v0, v1, …]</c> from any enumerable source.</summary>
    public IPredicate In(IEnumerable<T> values) => new InPredicate(Field, ToObjectArray(values));

    /// <summary>Negative set membership: <c>field NOT IN [v0, v1, ...]</c>.</summary>
    public IPredicate NotIn(params T[] values) => new NotInPredicate(Field, ToObjectArray(values));

    /// <summary>Negative set membership from any enumerable source.</summary>
    public IPredicate NotIn(IEnumerable<T> values) => new NotInPredicate(Field, ToObjectArray(values));

    /// <summary>Inclusive range: <c>field &gt;= lower AND field &lt;= upper</c>.</summary>
    public IPredicate Between(T lower, T upper) => new BetweenPredicate(Field, lower, upper);

    /// <summary>
    /// Field-unset test: <c>(field IS NONE OR field IS NULL)</c>. The write path omits
    /// keys for null values, so an optional field with no value is stored as NONE
    /// (absent) — this is the predicate that matches it. NULL is accepted too, for rows
    /// written by tools that store explicit nulls. Same compiled shape as <c>Eq(null)</c>;
    /// this is the discoverable, intent-naming form.
    /// </summary>
    public IPredicate IsNone() => new IsNonePredicate(Field);

    /// <summary>Field-set test: <c>(field IS NOT NONE AND field IS NOT NULL)</c> — the complement of <see cref="IsNone"/>.</summary>
    public IPredicate IsNotNone() => new IsNotNonePredicate(Field);

    private static object?[] ToObjectArray(IEnumerable<T> values)
    {
        if (values is not ICollection<T> col)
        {
            return [..values.Cast<object?>()];
        }
        var arr = new object?[col.Count];
        var i = 0;
        foreach (var v in col)
        {
            arr[i++] = v;
        }
        return arr;
    }
}

/// <summary>
/// String-only operators on <see cref="PropertyExpr{T}"/>. <c>Contains</c> doesn't fit on
/// the generic struct itself — it's string-shaped, not <c>T</c>-shaped — so it lives here.
/// One overload covers both <c>PropertyExpr&lt;string&gt;</c> and
/// <c>PropertyExpr&lt;string?&gt;</c> because nullable annotations on reference type
/// arguments are erased at the CLR level: both materialise as the same generic
/// instantiation, so a single <c>this PropertyExpr&lt;string&gt;</c> extension binds to
/// either at the call site (with a nullable-warning only when the annotated form differs).
/// </summary>
public static class PropertyExprStringExtensions
{
    /// <summary>Substring containment: <c>string::contains(field, $substring)</c>.</summary>
    public static IPredicate Contains(this PropertyExpr<string> expr, string substring)
        => new ContainsPredicate(expr.Field, substring);

    /// <summary>Prefix match: <c>string::starts_with(field, $prefix)</c>.</summary>
    public static IPredicate StartsWith(this PropertyExpr<string> expr, string prefix)
        => new StartsWithPredicate(expr.Field, prefix);

    /// <summary>Suffix match: <c>string::ends_with(field, $suffix)</c>.</summary>
    public static IPredicate EndsWith(this PropertyExpr<string> expr, string suffix)
        => new EndsWithPredicate(expr.Field, suffix);

    /// <summary>
    /// Null-or-empty test: <c>(field IS NONE OR field IS NULL OR field = '')</c>.
    /// NONE-aware like <see cref="PropertyExpr{T}.IsNone"/> — the write path stores
    /// unset optionals as NONE (absent key) — with the empty-string arm on top, matching
    /// C#'s <c>string.IsNullOrEmpty</c> semantics server-side.
    /// </summary>
    public static IPredicate IsNullOrEmpty(this PropertyExpr<string> expr)
        => new IsNullOrEmptyPredicate(expr.Field);

    /// <summary>
    /// Has-content test: <c>(field IS NOT NONE AND field IS NOT NULL AND field != '')</c>
    /// — the exact complement of <see cref="IsNullOrEmpty"/>.
    /// </summary>
    public static IPredicate IsNotNullOrEmpty(this PropertyExpr<string> expr)
        => new IsNotNullOrEmptyPredicate(expr.Field);

    /// <summary>
    /// Regex match: <c>string::matches(field, $pattern)</c>. The pattern travels as a
    /// bound parameter like every other operand. SurrealQL's <c>~</c> / <c>?~</c>
    /// operators are <i>fuzzy</i>-match (edit-distance) operators, not regex —
    /// <c>string::matches</c> is the regex primitive, hence the function form here.
    /// </summary>
    public static IPredicate Matches(this PropertyExpr<string> expr, string pattern)
        => new MatchesPredicate(expr.Field, pattern);

    /// <summary>
    /// Case-insensitive substring match:
    /// <c>string::contains(string::lowercase(field), $substring)</c>. The operand is
    /// lowercased with the invariant culture at compile time so the comparison is
    /// host-locale-independent.
    /// </summary>
    public static IPredicate ContainsIgnoreCase(this PropertyExpr<string> expr, string substring)
        => new ContainsIgnoreCasePredicate(expr.Field, substring);

    /// <summary>Case-insensitive prefix match: <c>string::starts_with(string::lowercase(field), $prefix)</c>.</summary>
    public static IPredicate StartsWithIgnoreCase(this PropertyExpr<string> expr, string prefix)
        => new StartsWithIgnoreCasePredicate(expr.Field, prefix);

    /// <summary>Case-insensitive suffix match: <c>string::ends_with(string::lowercase(field), $suffix)</c>.</summary>
    public static IPredicate EndsWithIgnoreCase(this PropertyExpr<string> expr, string suffix)
        => new EndsWithIgnoreCasePredicate(expr.Field, suffix);
}
