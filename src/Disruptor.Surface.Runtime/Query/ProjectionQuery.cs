using Disruptor.Surreal;
using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// A query that has been bound to an <see cref="ISurfaceProjection{TRow}"/>: chain
/// <see cref="ExecuteAsync"/> to compile to <c>SELECT field1, field2 FROM …</c> via
/// <see cref="SurfaceQueryCompiler.CompileProjection"/> and materialise each row through the
/// projection's lambda. Returns immutable result rows; no entity hydration, no session.
/// <para>
/// All chain methods on the original <see cref="SurfaceQuery{T}"/> (Where, OrderBy, Limit,
/// Start, WithId) are mirrored here so the projection can be added at any point in the
/// fluent chain. <see cref="WithInclude"/> is intentionally absent — projections are
/// flat by definition.
/// </para>
/// </summary>
public sealed class ProjectionQuery<T, TRow>
    where T : class, IEntity, new()
{
    /// <summary>The underlying entity-shape query — predicates, ordering, paging, pinned id.</summary>
    public SurfaceQuery<T> SurfaceQuery { get; }

    /// <summary>The bound projection — owns the SELECT list and per-row materialiser.</summary>
    public ISurfaceProjection<TRow> Projection { get; }

    internal ProjectionQuery(SurfaceQuery<T> query, ISurfaceProjection<TRow> projection)
    {
        SurfaceQuery = query;
        Projection = projection;
    }

    /// <summary>Append a predicate to the underlying query. AND-merged with any existing filter.</summary>
    public ProjectionQuery<T, TRow> Where(IPredicate predicate)
        => new(SurfaceQuery.Where(predicate), Projection);

    /// <summary>Pin the underlying query to a single record id.</summary>
    public ProjectionQuery<T, TRow> WithId(IRecordId id)
        => new(SurfaceQuery.WithId(id), Projection);

    /// <summary>Order by a column. Renders as <c>ORDER BY field ASC|DESC</c>.</summary>
    public ProjectionQuery<T, TRow> OrderBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending)
        => new(SurfaceQuery.OrderBy(property, direction), Projection);

    /// <summary>Tie-break on a secondary column.</summary>
    public ProjectionQuery<T, TRow> ThenBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending)
        => new(SurfaceQuery.ThenBy(property, direction), Projection);

    /// <summary>Cap the result set at <paramref name="count"/> rows server-side.</summary>
    public ProjectionQuery<T, TRow> Limit(int count) => new(SurfaceQuery.Limit(count), Projection);

    /// <summary>Skip the first <paramref name="count"/> rows server-side.</summary>
    public ProjectionQuery<T, TRow> Start(int count) => new(SurfaceQuery.Start(count), Projection);

    /// <summary>
    /// Compile this projection query to a single <c>SELECT</c> statement against the
    /// projection's field list. Useful for inspection or splicing; the runtime calls
    /// this from <see cref="ExecuteAsync"/>.
    /// </summary>
    public (string Sql, SurrealObject Bindings) Compile()
    {
        if (SurfaceQuery.Includes.Count > 0)
        {
            throw new InvalidOperationException(
                "Projections do not support Include* calls. Drop the includes, or use ExecuteAsync on the underlying Query<T> if you need traversal.");
        }
        return SurfaceQueryCompiler.CompileProjection(
            SurfaceQuery.Table, Projection.SelectFields, SurfaceQuery.Filter, SurfaceQuery.PinnedId,
            SurfaceQuery.OrderClauses, SurfaceQuery.LimitCount, SurfaceQuery.StartAt);
    }

    /// <summary>
    /// Compile, execute, and materialise each row through the projection. Returns the
    /// rows as immutable instances of <typeparamref name="TRow"/>; no entity hydration,
    /// no session.
    /// </summary>
    public Task<IReadOnlyList<TRow>> ExecuteAsync(SurrealClient db, CancellationToken ct = default)
        => ExecuteAsync(db.QueryAsync, ct);

    /// <inheritdoc cref="ExecuteAsync(SurrealClient, CancellationToken)"/>
    public Task<IReadOnlyList<TRow>> ExecuteAsync(SurrealTransaction tx, CancellationToken ct = default)
        => ExecuteAsync(tx.QueryAsync, ct);

    private async Task<IReadOnlyList<TRow>> ExecuteAsync(
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
    {
        var (sql, bindings) = Compile();
        var response = await queryFn(sql, bindings, ct);
        var rows = response.Count > 0 ? response.Take(0) : null;

        var list = new List<TRow>();
        if (rows is SurrealListValue arr)
        {
            foreach (var row in arr.List)
            {
                if (row is SurrealObjectValue obj)
                {
                    list.Add(Projection.Materialise(obj));
                }
            }
        }
        else if (rows is SurrealObjectValue single)
        {
            list.Add(Projection.Materialise(single));
        }
        return list;
    }

    /// <summary>
    /// Execute the projection capped at one row and return the first materialised row,
    /// or <c>default(TRow)</c> (<c>null</c> for reference-type rows) when nothing
    /// matches. Compiles with <c>LIMIT 1</c>, overriding any user <see cref="Limit"/>;
    /// <see cref="Start"/> and <see cref="OrderBy"/> still apply.
    /// </summary>
    public Task<TRow?> FirstOrDefaultAsync(SurrealClient db, CancellationToken ct = default)
        => FirstOrDefaultAsync(db.QueryAsync, ct);

    /// <inheritdoc cref="FirstOrDefaultAsync(SurrealClient, CancellationToken)"/>
    public Task<TRow?> FirstOrDefaultAsync(SurrealTransaction tx, CancellationToken ct = default)
        => FirstOrDefaultAsync(tx.QueryAsync, ct);

    private async Task<TRow?> FirstOrDefaultAsync(
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
    {
        var rows = await Limit(1).ExecuteAsync(queryFn, ct);
        return rows.Count > 0 ? rows[0] : default;
    }

    /// <summary>
    /// Execute the projection expecting exactly one match and return it. Compiles with
    /// <c>LIMIT 2</c> (enough to detect ambiguity without pulling the full result set;
    /// overrides any user <see cref="Limit"/>). Throws
    /// <see cref="InvalidOperationException"/> when the query matches no rows or more
    /// than one row.
    /// </summary>
    public Task<TRow> SingleAsync(SurrealClient db, CancellationToken ct = default)
        => SingleAsync(db.QueryAsync, ct);

    /// <inheritdoc cref="SingleAsync(SurrealClient, CancellationToken)"/>
    public Task<TRow> SingleAsync(SurrealTransaction tx, CancellationToken ct = default)
        => SingleAsync(tx.QueryAsync, ct);

    private async Task<TRow> SingleAsync(
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
    {
        var rows = await Limit(2).ExecuteAsync(queryFn, ct);
        ThrowIfMoreThanOne(rows);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                $"Single query on '{SurfaceQuery.Table}' matched no rows. Use SingleOrDefaultAsync if an empty result is a legal state.");
        }
        return rows[0];
    }

    /// <summary>
    /// As <see cref="SingleAsync(SurrealClient, CancellationToken)"/> but returns
    /// <c>default(TRow)</c> when the query matches no rows. Still throws
    /// <see cref="InvalidOperationException"/> on more than one match.
    /// </summary>
    public Task<TRow?> SingleOrDefaultAsync(SurrealClient db, CancellationToken ct = default)
        => SingleOrDefaultAsync(db.QueryAsync, ct);

    /// <inheritdoc cref="SingleOrDefaultAsync(SurrealClient, CancellationToken)"/>
    public Task<TRow?> SingleOrDefaultAsync(SurrealTransaction tx, CancellationToken ct = default)
        => SingleOrDefaultAsync(tx.QueryAsync, ct);

    private async Task<TRow?> SingleOrDefaultAsync(
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
    {
        var rows = await Limit(2).ExecuteAsync(queryFn, ct);
        ThrowIfMoreThanOne(rows);
        return rows.Count > 0 ? rows[0] : default;
    }

    private void ThrowIfMoreThanOne(IReadOnlyList<TRow> rows)
    {
        if (rows.Count > 1)
        {
            throw new InvalidOperationException(
                $"Single query on '{SurfaceQuery.Table}' matched more than one row. Narrow the predicate, or use FirstOrDefaultAsync if any match will do.");
        }
    }
}
