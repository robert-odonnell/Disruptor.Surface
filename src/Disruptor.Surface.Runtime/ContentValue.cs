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
