using System.Diagnostics.CodeAnalysis;

namespace Disruptor.Surface.Runtime;

public readonly record struct RecordId(string Table, string Value) : IRecordId, IComparable<RecordId>
{
    public string Table { get; } = Table;
    public string Value { get; } = Value;

    /// <summary>Fresh, ulid-backed record id — used for newly minted Entities.</summary>
    public static RecordId New(string table, string? value = null)
        => new(table, value ?? Ulid.NewUlid().ToString());

    /// <summary>
    /// Deterministic, content-addressed record id. The value is derived from
    /// <paramref name="text"/> via <see cref="RecordIdFormat.HashText"/> — same input
    /// always yields the same id, so this is the natural pick when the record is
    /// keyed by something the caller knows up front (a code symbol's full name, a
    /// canonical message, …) rather than minted at create time. Optional
    /// <paramref name="prefix"/> is a single ASCII lowercase letter for visual
    /// categorisation.
    /// </summary>
    public static RecordId FromText(string table, string text, char? prefix = null)
        => new(table, RecordIdFormat.HashText(text, prefix));

    /// <summary>
    /// Deterministic edge-row id derived from the <c>(source, edge table, target)</c>
    /// triple: the value is <see cref="RecordIdFormat.HashText"/> of
    /// <c>{source}|{edgeTable}|{target}</c>, so the same triple always lands on the
    /// same row. This is the derivation the emitted relation-variant id anchor uses
    /// when the variant has no user-assigned (or hydrated) id at save time — re-saving
    /// the same endpoint pair replays onto the same edge row, so the id the session
    /// records via <c>MarkSaved</c> matches the row the substrate's
    /// <c>UNIQUE (in, out)</c> duplicate path actually updated.
    /// </summary>
    public static RecordId ForEdge(string edgeTable, IRecordId source, IRecordId target)
        => new(edgeTable, RecordIdFormat.HashText($"{From(source)}|{edgeTable}|{From(target)}"));

    /// <summary>
    /// Deferred edge-id strategy: the <c>Value</c> is left empty as a sentinel that a
    /// caller later collapses via <see cref="Resolve"/>. Since the deterministic-edge-id
    /// change (2026-07-02) the emitted relation-variant save path no longer needs the
    /// sentinel — the variant id anchor derives its edge id up front via
    /// <see cref="ForEdge"/> (same hash scheme) when no id was assigned. The sentinel
    /// remains for callers minting content-addressed edge ids by hand; one that reaches
    /// dispatch un-resolved is sent without an explicit id, so the substrate mints a
    /// random row id (i.e. NOT idempotent in practice).
    /// <para>
    /// Only meaningful as the edge id on a relation row — using an idempotent id
    /// elsewhere will produce empty-value strings in rendered SurrealQL.
    /// </para>
    /// </summary>
    public static RecordId Idempotent(string table) => new(table, "");

    /// <summary>
    /// True iff this id is the deferred-idempotent sentinel: <see cref="Table"/> is set
    /// and <see cref="Value"/> is empty. <c>default(RecordId)</c> (both components
    /// <c>null</c> — the in-band "no endpoint" sentinel <see cref="Command.Unrelate"/>
    /// logs) is NOT idempotent, and this property must stay null-safe for it. The empty
    /// value is otherwise unambiguous — every other id form (Ulid, slug, hash) is at
    /// least one character long and is rejected by <see cref="RecordIdFormat.Validate"/>
    /// if empty.
    /// </summary>
    public bool IsIdempotent => Value is { Length: 0 } && !string.IsNullOrEmpty(Table);

    /// <summary>
    /// Resolve a deferred-idempotent edge id against its <paramref name="source"/> and
    /// <paramref name="target"/> endpoints. No-op for already-resolved ids; for the
    /// idempotent sentinel, delegates to <see cref="ForEdge"/> — the value becomes
    /// <see cref="RecordIdFormat.HashText"/> of <c>{source}|{Table}|{target}</c>, so the
    /// same triple always lands on the same row. The emitted relation-variant save path
    /// applies the same derivation automatically (via <see cref="ForEdge"/> in the
    /// variant id anchor); this instance form exists for callers minting
    /// content-addressed edge ids by hand from the <see cref="Idempotent"/> sentinel.
    /// </summary>
    public RecordId Resolve(RecordId source, RecordId target)
        => IsIdempotent
            ? ForEdge(Table, source, target)
            : this;

    /// <summary>
    /// Collapse any <see cref="IRecordId"/> (typed per-table or canonical) to the canonical
    /// <see cref="RecordId"/> form SurrealSession internals key off.
    /// </summary>
    public static RecordId From(IRecordId id) =>
        id is RecordId r ? r : new RecordId(id.Table, id.ToLiteral());

    public static RecordId Parse(string? source)
        => !TryParse(source, out var result)
            ? throw new InvalidOperationException("RecordId.Parse requires input in the format 'table:value'.")
            : result.Value;

    public static bool TryParse(string? source, [NotNullWhen(true)] out RecordId? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }
        var parts = source.Split([':'], 2);
        if (parts.Length != 2)
        {
            return false;
        }
        result = new RecordId(parts[0], parts[1]);
        return true;
    }

    public override string ToString() => $"{Table}:{Value}";

    public int CompareTo(RecordId other)
    {
        var byTable = StringComparer.Ordinal.Compare(Table, other.Table);
        return byTable != 0
            ? byTable
            : StringComparer.Ordinal.Compare(Value, other.Value);
    }

    public bool IsForTable(string table) =>
        StringComparer.Ordinal.Equals(Table, table);

    public string ToLiteral() => Value;
}

