using System.Collections;
using System.Text;
using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Compiles a predicate AST + traversal AST + optional pinned id into a SurrealQL
/// <c>SELECT</c> string plus a typed-CBOR <see cref="SurrealObject"/> bindings dict.
/// Every leaf value (predicate operand, pinned id, IN list element) is allocated a
/// <c>$_pN</c> placeholder and pushed into bindings as the appropriate
/// <see cref="SurrealValue"/> variant. The wire path stays end-to-end CBOR — no
/// SurrealQL string literals for user values, no escape rules, no JSON.
/// <para>
/// Traversals expand to nested <c>(SELECT … FROM child WHERE parent_field = $parent.id) AS child</c>
/// subselects. Identifiers (table names, field names, edge names, slice keys) stay
/// inlined in the SQL — they're trusted, regex-validated by
/// <see cref="SurrealFormatter.Identifier"/>. <c>LIMIT</c> / <c>START</c> integers also
/// stay inlined (no escape concern; SurrealQL parses them directly).
/// </para>
/// </summary>
internal static class SurfaceQueryCompiler
{
    /// <summary>Build the SurrealQL + bindings for a (possibly nested) <c>SELECT</c>.</summary>
    public static (string Sql, SurrealObject Bindings) Compile(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<IIncludeNode> includes,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(b.BuildProjection(includes)).Append(" FROM ").Append(table.Identifier());
        b.AppendWhereOrderLimitStart(sb, filter, pinnedId, orderClauses, limit, start);
        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Build the SurrealQL + bindings for a projection selection:
    /// <c>SELECT field1, field2 FROM table …</c>. Includes are not supported on this path —
    /// projections are flat by definition.
    /// <para>
    /// When <paramref name="orderClauses"/> reference fields outside
    /// <paramref name="selectFields"/>, those field names are appended to the projection —
    /// the SurrealDB 3.x parser rejects <c>ORDER BY x</c> when <c>x</c> isn't in the
    /// selection with <c>Missing order idiom `x` in statement selection</c> (same
    /// workaround as <see cref="CompileIdsOnly"/> and the edge compiler). The reader path
    /// (<c>ValueProjectionRow</c>) looks fields up by name, so the extra columns are
    /// wire-only and never surface in the materialised rows.
    /// </para>
    /// </summary>
    public static (string Sql, SurrealObject Bindings) CompileProjection(
        string table,
        IReadOnlyList<string> selectFields,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        if (selectFields.Count == 0)
        {
            throw new ArgumentException("Projection requires at least one field.", nameof(selectFields));
        }

        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT ");
        var projected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < selectFields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(selectFields[i].Identifier());
            projected.Add(selectFields[i]);
        }
        if (orderClauses is { Count: > 0 })
        {
            foreach (var clause in orderClauses)
            {
                if (projected.Add(clause.Field))
                {
                    sb.Append(", ").Append(clause.Field.Identifier());
                }
            }
        }
        sb.Append(" FROM ").Append(table.Identifier());
        b.AppendWhereOrderLimitStart(sb, filter, pinnedId, orderClauses, limit, start);
        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Build the SurrealQL + bindings for an id-only selection:
    /// <c>SELECT id FROM table …</c>. Includes are not supported (flat by definition).
    /// <para>
    /// When <paramref name="orderClauses"/> reference fields other than <c>id</c>, those
    /// field names are added to the projection (<c>SELECT id, name, … FROM …</c>) — the
    /// SurrealDB 3.x parser rejects <c>ORDER BY x</c> when <c>x</c> isn't in the
    /// selection with <c>Missing order idiom `x` in statement selection</c>. The reader
    /// path (<c>{Table}QueryIds.IdsAsync</c>) only consumes the <c>id</c> field from
    /// each row, so the extra columns are wire-only and do not surface in the typed
    /// result list.
    /// </para>
    /// </summary>
    public static (string Sql, SurrealObject Bindings) CompileIdsOnly(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT id");
        if (orderClauses is { Count: > 0 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { "id" };
            foreach (var clause in orderClauses)
            {
                if (seen.Add(clause.Field))
                {
                    sb.Append(", ").Append(clause.Field.Identifier());
                }
            }
        }
        sb.Append(" FROM ").Append(table.Identifier());
        b.AppendWhereOrderLimitStart(sb, filter, pinnedId, orderClauses, limit, start);
        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Build the SurrealQL + bindings for a matching-row count. Count is a flat terminal:
    /// traversal includes and projection shape are intentionally outside this compiler path.
    /// </summary>
    public static (string Sql, SurrealObject Bindings) CompileCount(
        string table,
        IPredicate? filter,
        RecordId? pinnedId)
    {
        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT count() AS count FROM ").Append(table.Identifier());
        b.AppendWhere(sb, filter, pinnedId);
        sb.Append(" GROUP ALL;");
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Per-compile mutable state: the bindings accumulator + a monotonic counter that
    /// names each placeholder. Keeps the <see cref="SurfaceQueryCompiler"/> static surface clean
    /// while letting the recursive subselect / predicate walk share the binding stream.
    /// </summary>
    internal sealed class Builder
    {
        public SurrealObject Bindings { get; } = [];
        private int counter;

        /// <summary>
        /// Allocate a fresh <c>$_pN</c>, push <paramref name="value"/> into bindings as
        /// the right <see cref="SurrealValue"/> variant, and return the placeholder text.
        /// Typed-CBOR end-to-end: no string formatting, no escape rules.
        /// </summary>
        public string Allocate(object? value)
        {
            var name = $"_p{counter++}";
            Bindings[name] = WrapAsSurrealValue(value);
            return "$" + name;
        }

        public void AppendWhereOrderLimitStart(
            StringBuilder sb,
            IPredicate? filter,
            RecordId? pinnedId,
            IReadOnlyList<OrderClause>? orderClauses,
            int? limit,
            int? start)
        {
            AppendWhere(sb, filter, pinnedId);

            // SurrealQL clause order is fixed: ORDER BY → LIMIT → START. Stable order
            // matches the docs and avoids "why doesn't this paginate" surprises.
            AppendOrderBy(sb, orderClauses);
            AppendLimit(sb, limit);
            AppendStart(sb, start);
        }

        public void AppendWhere(StringBuilder sb, IPredicate? filter, RecordId? pinnedId)
        {
            var hasWhere = false;
            if (pinnedId is { } id)
            {
                sb.Append(" WHERE id = ").Append(Allocate(id));
                hasWhere = true;
            }
            if (filter is not null)
            {
                sb.Append(hasWhere ? " AND " : " WHERE ").Append(CompilePredicate(filter));
            }
        }

        private static void AppendOrderBy(StringBuilder sb, IReadOnlyList<OrderClause>? clauses)
        {
            if (clauses is null || clauses.Count == 0)
            {
                return;
            }

            sb.Append(" ORDER BY ");
            for (var i = 0; i < clauses.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var c = clauses[i];
                sb.Append(c.Field.Identifier()).Append(c.Direction == OrderDirection.Descending ? " DESC" : " ASC");
            }
        }

        private static void AppendLimit(StringBuilder sb, int? limit)
        {
            // LIMIT/START are integer literals — no escape concern, no value typing
            // benefit from binding. Inline directly.
            if (limit is { } n && n > 0)
            {
                sb.Append(" LIMIT ").Append(n);
            }
        }

        private static void AppendStart(StringBuilder sb, int? start)
        {
            if (start is { } n && n > 0)
            {
                sb.Append(" START ").Append(n);
            }
        }

        /// <summary>
        /// Compose a level's projection list: starts with <c>*</c>, then adds
        /// <c>field.*</c> per inline-ref include, then adds the rendered subselect per
        /// children-include / relation-include. Order is stable: inline-refs precede
        /// subselects so the SELECT's scalar + inline-record half stays adjacent.
        /// </summary>
        public string BuildProjection(IReadOnlyList<IIncludeNode> includes)
        {
            if (includes.Count == 0)
            {
                return "*";
            }

            ValidateAliasUniqueness(includes);

            var sb = new StringBuilder("*");
            foreach (var node in includes)
            {
                if (node is IncludeInlineRefNode inline)
                {
                    sb.Append(", ").Append(inline.Field.Identifier()).Append(".*");
                }
            }
            foreach (var node in includes)
            {
                if (node is IncludeChildrenNode children)
                {
                    sb.Append(", ").Append(BuildChildSubselect(children));
                }
                else if (node is IncludeRelationNode relation)
                {
                    sb.Append(", ").Append(BuildRelationSubselect(relation));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reject include lists where two subselect nodes at the same level share a
        /// response alias (<see cref="IncludeChildrenNode.ChildTable"/> /
        /// <see cref="IncludeRelationNode.ParentSliceKey"/>). SurrealDB keeps only one
        /// <c>AS alias</c> column per name, so a collision would let one subselect
        /// silently clobber the other in the response — fail loudly at compile time
        /// instead. Inline-ref nodes project as <c>field.*</c> (no alias) and are exempt.
        /// </summary>
        private static void ValidateAliasUniqueness(IReadOnlyList<IIncludeNode> includes)
        {
            HashSet<string>? aliases = null;
            foreach (var node in includes)
            {
                var alias = node switch
                {
                    IncludeChildrenNode children => children.ChildTable,
                    IncludeRelationNode relation => relation.ParentSliceKey,
                    _ => null,
                };
                if (alias is null)
                {
                    continue;
                }

                aliases ??= new HashSet<string>(StringComparer.Ordinal);
                if (!aliases.Add(alias))
                {
                    throw new InvalidOperationException(
                        $"Include alias collision: two includes at the same query level both project as '{alias}'. "
                        + "SurrealDB keeps only one column per alias, so one subselect would silently clobber the "
                        + "other in the response. Drop one of the includes, or rename the property so the slices "
                        + "project under distinct aliases.");
                }
            }
        }

        /// <summary>
        /// Render an <see cref="IncludeRelationNode"/> as a SurrealQL projection element.
        /// </summary>
        private string BuildRelationSubselect(IncludeRelationNode node)
        {
            var alias = node.ParentSliceKey.Identifier();
            var edge = node.EdgeName.Identifier();

            if (node.IdsOnly)
            {
                // Cross-aggregate: edge subselect with id/in/out so the session's edges
                // dict can be populated without target hydration.
                var sideField = node.IsOutgoing ? "in" : "out";
                return $"(SELECT id, in, out FROM {edge} WHERE {sideField} = $parent.id) AS {alias}";
            }

            // Within-aggregate: graph traversal. `?` matches any target type when the
            // relation has multiple members; single concrete target narrows to it.
            var arrow = node.IsOutgoing ? "->" : "<-";
            var step = node.SingleTargetTable is { } target ? target.Identifier() : "?";

            if (node.Nested.Count == 0)
            {
                var filterClause = node.Filter is null ? "" : $"[WHERE {CompilePredicate(node.Filter)}]";
                return $"({arrow}{edge}{arrow}{step}{filterClause}.*) AS {alias}";
            }

            var inner = BuildProjection(node.Nested);
            var traversal = $"{arrow}{edge}{arrow}{step}";
            var where = node.Filter is null ? "" : $" WHERE {CompilePredicate(node.Filter)}";
            return $"(SELECT {inner} FROM {traversal}{where}) AS {alias}";
        }

        /// <summary>
        /// Render a single <see cref="IncludeChildrenNode"/> as a parenthesised subselect
        /// aliased to the child table name.
        /// </summary>
        private string BuildChildSubselect(IncludeChildrenNode node)
        {
            var childTable = node.ChildTable.Identifier();
            var parentField = node.ParentField.Identifier();
            var innerProjection = BuildProjection(node.Nested);

            var where = $"{parentField} = $parent.id";
            if (node.Filter is not null)
            {
                where = $"{where} AND {CompilePredicate(node.Filter)}";
            }

            return $"(SELECT {innerProjection} FROM {childTable} WHERE {where}) AS {childTable}";
        }

        public string CompilePredicate(IPredicate p) => p switch
        {
            // Eq(null) can't bind: the write path omits keys for null values, so the
            // stored state is NONE (absent field) — and `field = $p` with $p = NULL is
            // false against NONE in SurrealDB. Compile to the unset test instead so
            // Eq(null) matches library-written data; Not(Eq(null)) negates the whole
            // parenthesised group below, which is the correct complement.
            EqPredicate { Value: null } eq => Unset(eq.Field),
            EqPredicate eq        => $"{eq.Field.Identifier()} = {Allocate(eq.Value)}",
            IsNonePredicate np    => Unset(np.Field),
            IsNotNonePredicate nn => NotUnset(nn.Field),
            RangePredicate rp     => $"{rp.Field.Identifier()} {RangeOpText(rp.Op)} {Allocate(rp.Value)}",
            InPredicate ip        => $"{ip.Field.Identifier()} IN {Allocate(ip.Values)}",
            NotInPredicate nip    => $"{nip.Field.Identifier()} NOT IN {Allocate(nip.Values)}",
            BetweenPredicate bp   => $"({bp.Field.Identifier()} >= {Allocate(bp.Lower)} AND {bp.Field.Identifier()} <= {Allocate(bp.Upper)})",
            // The string:: functions are guarded with `field != NONE` — SurrealDB's
            // strict function typing fails the whole SELECT when a single row has the
            // field unset (option<string> with no value), rather than skipping the row.
            ContainsPredicate cp  => $"({cp.Field.Identifier()} != NONE AND string::contains({cp.Field.Identifier()}, {Allocate(cp.Substring)}))",
            StartsWithPredicate sp => $"({sp.Field.Identifier()} != NONE AND string::starts_with({sp.Field.Identifier()}, {Allocate(sp.Prefix)}))",
            EndsWithPredicate ep   => $"({ep.Field.Identifier()} != NONE AND string::ends_with({ep.Field.Identifier()}, {Allocate(ep.Suffix)}))",
            // The IgnoreCase family folds the field via string::lowercase and lowercases
            // the operand invariantly in C# — host-locale-independent on both sides.
            // Same NONE guard as the plain string functions above.
            ContainsIgnoreCasePredicate cip => $"({cip.Field.Identifier()} != NONE AND string::contains(string::lowercase({cip.Field.Identifier()}), {Allocate(cip.Substring.ToLowerInvariant())}))",
            StartsWithIgnoreCasePredicate sip => $"({sip.Field.Identifier()} != NONE AND string::starts_with(string::lowercase({sip.Field.Identifier()}), {Allocate(sip.Prefix.ToLowerInvariant())}))",
            EndsWithIgnoreCasePredicate eip => $"({eip.Field.Identifier()} != NONE AND string::ends_with(string::lowercase({eip.Field.Identifier()}), {Allocate(eip.Suffix.ToLowerInvariant())}))",
            // string::matches is SurrealDB's regex primitive and takes the pattern as an
            // ordinary (bindable) string argument. The ~ / ?~ / *~ operators are fuzzy
            // match (edit distance), not regex — intentionally not used here.
            MatchesPredicate mp => $"({mp.Field.Identifier()} != NONE AND string::matches({mp.Field.Identifier()}, {Allocate(mp.Pattern)}))",
            IsNullOrEmptyPredicate nep => $"({nep.Field.Identifier()} IS NONE OR {nep.Field.Identifier()} IS NULL OR {nep.Field.Identifier()} = '')",
            IsNotNullOrEmptyPredicate nnep => $"({nnep.Field.Identifier()} IS NOT NONE AND {nnep.Field.Identifier()} IS NOT NULL AND {nnep.Field.Identifier()} != '')",
            AndPredicate a        => $"({string.Join(" AND ", a.Operands.Select(CompilePredicate))})",
            OrPredicate  o        => $"({string.Join(" OR ", o.Operands.Select(CompilePredicate))})",
            NotPredicate n        => $"!({CompilePredicate(n.Operand)})",
            _ => throw new NotSupportedException($"Predicate type {p.GetType().FullName} is not supported by QueryCompiler.")
        };

        /// <summary>
        /// The "field is unset" test: <c>(field IS NONE OR field IS NULL)</c>. SurrealDB
        /// parses <c>IS</c>/<c>IS NOT</c> as exact aliases of <c>=</c>/<c>!=</c>; the SDK
        /// has no NONE-comparison precedent of its own, so the alias form is chosen for
        /// readability of sentinel checks in transport logs. NONE and NULL are both
        /// covered: the library's write path stores unset optionals as NONE (key omitted),
        /// but rows written by other tools may hold an explicit NULL.
        /// </summary>
        private static string Unset(string field)
            => $"({field.Identifier()} IS NONE OR {field.Identifier()} IS NULL)";

        /// <summary>De Morgan complement of <see cref="Unset"/>: <c>(field IS NOT NONE AND field IS NOT NULL)</c>.</summary>
        private static string NotUnset(string field)
            => $"({field.Identifier()} IS NOT NONE AND {field.Identifier()} IS NOT NULL)";
    }

    private static string RangeOpText(RangeOp op) => op switch
    {
        RangeOp.Lt => "<",
        RangeOp.Le => "<=",
        RangeOp.Gt => ">",
        RangeOp.Ge => ">=",
        _ => throw new NotSupportedException($"Unknown RangeOp: {op}")
    };

    /// <summary>
    /// Wrap a CLR value as the right <see cref="SurrealValue"/> variant. Typed-CBOR all
    /// the way down: typed record ids land as <see cref="SurrealRecordIdValue"/>
    /// (preserves Thing typing); IN lists land as <see cref="SurrealListValue"/>; null
    /// becomes <see cref="SurrealValue.Null"/>. Throws for anything we don't recognise
    /// — better a build-time-visible failure than a silent string-fallback.
    /// </summary>
    internal static SurrealValue WrapAsSurrealValue(object? value) => value switch
    {
        null => SurrealValue.Null,
        bool b => b,
        sbyte sb => (long)sb,
        byte by => (long)by,
        short s => (long)s,
        ushort us => (long)us,
        int i => i,
        uint ui => (long)ui,
        long l => l,
        // checked: SurrealDB integers are i64 — a ulong above long.MaxValue must fail
        // loudly (OverflowException) rather than wrap negative and match wrong rows.
        ulong ul => checked((long)ul),
        float f => (double)f,
        double d => d,
        decimal m => m,
        // strings are IEnumerable<char>; the explicit case stops the IEnumerable arm
        // from decomposing them into character lists.
        string str => str,
        // Guid binds as its canonical "D" string, mirroring ContentValue.Set — the
        // schema maps Guid to TYPE string, so a CBOR uuid binding would never compare
        // equal to the stored string.
        Guid g => new StringSurrealValue(g.ToString("D")),
        Ulid u => new StringSurrealValue(u.ToString()),
        DateTime dt => ContentValue.ToInstant(dt),
        DateTimeOffset dto => dto,
        TimeSpan ts => ts,
        Enum e => new StringSurrealValue(e.ToString()),
        RecordId rid => new SurrealRecordIdValue(rid.ToSdk()),
        IRecordId irid => new SurrealRecordIdValue(RecordId.From(irid).ToSdk()),
        // byte[] is IEnumerable too — the explicit case keeps binary data as a typed
        // bytes value instead of decomposing into a list of int64s.
        byte[] bytes => new SurrealBytesValue(bytes),
        IEnumerable e => new SurrealListValue(WrapEnumerable(e)),
        _ => throw new NotSupportedException(
            $"Cannot wrap value of type {value.GetType().FullName} as a SurrealValue. "
            + "Add a case to QueryCompiler.WrapAsSurrealValue if a new type needs binding support.")
    };

    private static SurrealList WrapEnumerable(IEnumerable e)
    {
        var list = new SurrealList();
        foreach (var item in e) list.Add(WrapAsSurrealValue(item));
        return list;
    }
}
