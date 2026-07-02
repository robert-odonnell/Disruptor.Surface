using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Typed-content builders for per-entity <see cref="IEntity.SaveAsync"/> emission. The
/// generator emits <c>__content.Set("title", _title)</c>-shaped lines per persisted
/// field; the helpers here apply the right <see cref="SurrealValue"/> wrapping for
/// each scalar shape and omit the field when the value is <c>null</c> (so SurrealDB's
/// schema-level <c>DEFAULT</c> applies). The mirror of <see cref="HydrationValue"/>:
/// HydrationValue is the read side, <see cref="ContentValue"/> the write side. Both
/// keep the wire path typed-and-CBOR — no JSON, no SurrealQL string formatting.
/// </summary>
public static class ContentValue
{
    extension(SurrealObject obj)
    {
        public void Set(string key, string? value)
        {
            if (value is not null)
            {
                obj[key] = value;
            }
        }

        public void Set(string key, bool value) => obj[key] = value;

        public void Set(string key, bool? value)
        {
            if (value is { } v)
            {
                obj[key] = v;
            }
        }

        public void Set(string key, int value) => obj[key] = value;

        public void Set(string key, int? value)
        {
            if (value is { } v)
            {
                obj[key] = v;
            }
        }

        public void Set(string key, long value) => obj[key] = value;

        public void Set(string key, long? value)
        {
            if (value is { } v)
            {
                obj[key] = v;
            }
        }

        public void Set(string key, double value) => obj[key] = value;

        public void Set(string key, double? value)
        {
            if (value is { } v)
            {
                obj[key] = v;
            }
        }

        public void Set(string key, decimal value) => obj[key] = value;

        public void Set(string key, decimal? value)
        {
            if (value is { } v)
            {
                obj[key] = v;
            }
        }

        public void Set(string key, DateTime value) => obj[key] = ToInstant(value);

        public void Set(string key, DateTime? value)
        {
            if (value is { } v)
            {
                obj[key] = ToInstant(v);
            }
        }

        public void Set(string key, DateTimeOffset value) => obj[key] = value;

        public void Set(string key, DateTimeOffset? value)
        {
            if (value is { } v)
            {
                obj[key] = v;
            }
        }

        /// <summary>Guid serialises as its canonical 36-char "D" format string (matches SurrealDB's <c>TYPE string</c> mapping in <c>SchemaEmitter</c> — the raw CBOR uuid would be rejected by SCHEMAFULL tables).</summary>
        public void Set(string key, Guid value) => obj[key] = value.ToString("D");

        public void Set(string key, Guid? value)
        {
            if (value is { } v)
            {
                obj[key] = v.ToString("D");
            }
        }

        /// <summary>Ulid serialises as its 26-char Crockford base32 string (matches SurrealDB's <c>TYPE string</c> mapping in <c>SchemaEmitter</c>).</summary>
        public void Set(string key, Ulid value) => obj[key] = value.ToString();

        public void Set(string key, Ulid? value)
        {
            if (value is { } v)
            {
                obj[key] = v.ToString();
            }
        }

        /// <summary>Writes a typed FK as a <see cref="SurrealRecordIdValue"/> — preserves Thing typing through CBOR.</summary>
        public void SetRef(string key, RecordId? value)
        {
            if (value is { } v)
            {
                obj[key] = new SurrealRecordIdValue(v.ToSdk());
            }
        }

        // ── Primitive-element collections ─────────────────────────────────────────
        //
        // The generator's element-collection [Property] save path calls the same
        // `ContentValue.Set(__content, field, _backing)` shape it uses for scalars;
        // these overloads catch the List<T> backing fields (via IReadOnlyList<T>) and
        // wrap them as SurrealListValue with the same per-scalar conversions as the
        // scalar Set overloads (DateTime → deterministic instant, Guid → "D" string,
        // Ulid → Crockford base32 string). The list itself is always written (an empty
        // collection round-trips as [], matching the schema's `array<T> DEFAULT []`).
        // Null string elements are skipped — the generator only admits non-nullable
        // element types (CG050), so a null element is already a nullability violation
        // and skipping mirrors how nullable scalar Sets omit null values.

        public void Set(string key, IReadOnlyList<string> values)
        {
            var list = new SurrealList(values.Count);
            foreach (var v in values)
            {
                if (v is not null)
                {
                    list.Add(v);
                }
            }

            obj[key] = new SurrealListValue(list);
        }

        public void Set(string key, IReadOnlyList<bool> values) => obj[key] = ToListValue(values, static v => v);

        public void Set(string key, IReadOnlyList<int> values) => obj[key] = ToListValue(values, static v => v);

        public void Set(string key, IReadOnlyList<long> values) => obj[key] = ToListValue(values, static v => v);

        public void Set(string key, IReadOnlyList<float> values) => obj[key] = ToListValue(values, static v => (double)v);

        public void Set(string key, IReadOnlyList<double> values) => obj[key] = ToListValue(values, static v => v);

        public void Set(string key, IReadOnlyList<decimal> values) => obj[key] = ToListValue(values, static v => v);

        public void Set(string key, IReadOnlyList<DateTime> values) => obj[key] = ToListValue(values, static v => ToInstant(v));

        public void Set(string key, IReadOnlyList<DateTimeOffset> values) => obj[key] = ToListValue(values, static v => v);

        /// <summary>Guid elements serialise as canonical 36-char "D" strings — same rationale as the scalar overload.</summary>
        public void Set(string key, IReadOnlyList<Guid> values) => obj[key] = ToListValue(values, static v => v.ToString("D"));

        /// <summary>Ulid elements serialise as 26-char Crockford base32 strings — same rationale as the scalar overload.</summary>
        public void Set(string key, IReadOnlyList<Ulid> values) => obj[key] = ToListValue(values, static v => v.ToString());
    }

    private static SurrealListValue ToListValue<T>(IReadOnlyList<T> values, Func<T, SurrealValue> convert)
    {
        var list = new SurrealList(values.Count);
        foreach (var v in values)
        {
            list.Add(convert(v));
        }

        return new SurrealListValue(list);
    }

    /// <summary>
    /// <see cref="DateTime"/> → instant conversion for the write and query-binding paths.
    /// Invariant: the stored instant is deterministic regardless of host timezone.
    /// <see cref="DateTimeKind.Unspecified"/> is treated as UTC — never machine-local:
    /// the naive <c>(DateTimeOffset)</c> cast throws <see cref="ArgumentOutOfRangeException"/>
    /// for <c>default(DateTime)</c> on any UTC+N machine and silently shifts wall-clock
    /// values per host zone. <see cref="DateTimeKind.Utc"/> maps directly;
    /// <see cref="DateTimeKind.Local"/> converts via the CLR's instant-preserving cast
    /// (the one case that legitimately reads the host zone — the instant survives).
    /// Hydration reads back <c>UtcDateTime</c> (<see cref="HydrationValue"/>), so
    /// Unspecified and Utc values round-trip losslessly in instant terms.
    /// </summary>
    internal static DateTimeOffset ToInstant(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : (DateTimeOffset)value;
}
