using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Static entry point for constructing <see cref="ISurfaceProjection{TRow}"/> instances.
/// The library does not generate projection types — users define the <c>TRow</c> shape
/// (typically a positional record) and pass a materialise lambda that reads each
/// column via <see cref="IProjectionRow.Read{T}"/>:
/// <code>
/// public sealed record SymbolSearchResult(string Name, string QualifiedName, int Line);
///
/// public static class SymbolProjections
/// {
///     public static readonly ISurfaceProjection&lt;SymbolSearchResult&gt; SearchResult =
///         SurfaceProjection.For&lt;SymbolSearchResult&gt;(row =&gt; new SymbolSearchResult(
///             Name:          row.Read(CodeSymbolQ.Name),
///             QualifiedName: row.Read(CodeSymbolQ.QualifiedName),
///             Line:          row.Read(CodeSymbolQ.Line)));
/// }
/// </code>
/// <para>
/// At construction time the lambda runs once with a discovery probe row that captures
/// each <c>Read</c>'d field; that list becomes the SurrealQL SELECT projection. At
/// query time the lambda runs once per result row against the CBOR-decoded
/// <see cref="Disruptor.Surreal.Values.SurrealObjectValue"/>.
/// </para>
/// </summary>
public static class SurfaceProjection
{
    /// <summary>
    /// Build a projection from a materialise lambda. The lambda runs once at
    /// construction time with a probe row to discover the field list; if that probe
    /// throws (typically because the target type's constructor rejects the default
    /// values the probe hands back), the failure surfaces as
    /// <see cref="ProjectionDiscoveryException"/> with hints on how to make the
    /// constructor probe-safe.
    /// <para>
    /// <b>Warning — every <c>row.Read</c> call must be unconditional.</b> The discovery
    /// probe runs the lambda exactly once, handing back <c>default</c> for every read;
    /// only the fields touched on that single pass make it into the SELECT list. A
    /// <c>row.Read</c> behind a branch that the default-valued probe doesn't take (e.g.
    /// <c>flag ? row.Read(Q.Extra) : fallback</c> where <c>flag</c> probes as
    /// <c>false</c>) is silently never discovered — the column is missing from the wire
    /// SQL and every real materialise pass reads <c>default</c> for it. Read all fields
    /// up front, then branch on the values.
    /// </para>
    /// </summary>
    public static ISurfaceProjection<TRow> For<TRow>(Func<IProjectionRow, TRow> materialize)
    {
        ArgumentNullException.ThrowIfNull(materialize);

        var discovery = new DiscoveryProjectionRow();
        try
        {
            _ = materialize(discovery);
        }
        catch (Exception ex)
        {
            throw new ProjectionDiscoveryException(
                $"Failed to discover projection fields for {typeof(TRow).Name}. The materialise " +
                "lambda must run cleanly with default values during the construction-time probe — " +
                "ensure the target type's constructor accepts default/null values without throwing. " +
                "Common cause: ArgumentException.ThrowIfNullOrEmpty(...) in a record constructor.",
                ex);
        }

        if (discovery.Fields.Count == 0)
        {
            throw new InvalidOperationException(
                $"Projection for {typeof(TRow).Name} discovered zero fields. The materialise lambda " +
                "must call IProjectionRow.Read at least once.");
        }

        return new SurfaceProjection<TRow>(discovery.Fields, materialize);
    }
}

/// <summary>
/// Default <see cref="ISurfaceProjection{TRow}"/> implementation: holds the discovered
/// field list and the user's materialise lambda. Each <see cref="Materialise"/> call
/// wraps the row in a <see cref="ValueProjectionRow"/> and runs the lambda again; the
/// lambda's <see cref="IProjectionRow.Read{T}"/> calls hit the decoded response values.
/// </summary>
internal sealed class SurfaceProjection<TRow>(
    IReadOnlyList<string> selectFields,
    Func<IProjectionRow, TRow> materialise) : ISurfaceProjection<TRow>
{
    public IReadOnlyList<string> SelectFields { get; } = selectFields;

    public TRow Materialise(SurrealObjectValue row) => materialise(new ValueProjectionRow(row));
}
