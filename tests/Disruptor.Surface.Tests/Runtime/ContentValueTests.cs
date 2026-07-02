using Disruptor.Surface.Runtime;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// ContentValue is the write side of the typed-CBOR wire path — these tests pin the
/// value-shaping invariants that SCHEMAFULL tables and host-timezone independence
/// depend on:
/// <list type="bullet">
///   <item>DateTime: the stored instant is deterministic regardless of host timezone.
///         Unspecified is treated as UTC (never machine-local); Utc maps directly;
///         Local converts instant-preserving. Round-trip through HydrationValue
///         (which returns UtcDateTime) is lossless in instant terms.</item>
///   <item>Guid: serialises as its canonical "D" format string, matching the schema's
///         <c>TYPE string</c> mapping (a raw CBOR uuid would be rejected).</item>
///   <item>Nullable overloads omit the key when null, so SurrealDB stores NONE.</item>
/// </list>
/// </summary>
public sealed class ContentValueTests
{
    // ─────────────────────── DateTime instants ───────────────────────

    [Fact]
    public void Set_DefaultDateTime_DoesNotThrow_AndStoresMinInstant()
    {
        // default(DateTime) has Kind.Unspecified; the old machine-local cast threw
        // ArgumentOutOfRangeException mid-save on any UTC+N machine.
        var obj = new SurrealObject();

        obj.Set("at", default(DateTime));

        var stored = Assert.IsType<SurrealDateTimeValue>(obj["at"]);
        Assert.Equal(DateTimeOffset.MinValue, stored.SurrealDateTime.ToDateTimeOffset());
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void Set_UnspecifiedAndUtcDateTime_StoreTheSameWallClockInstant(DateTimeKind kind)
    {
        var obj = new SurrealObject();

        obj.Set("at", new DateTime(2026, 7, 2, 10, 30, 15, kind));

        var stored = Assert.IsType<SurrealDateTimeValue>(obj["at"]);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 2, 10, 30, 15, TimeSpan.Zero),
            stored.SurrealDateTime.ToDateTimeOffset());
    }

    [Fact]
    public void Set_LocalDateTime_StoresInstantPreservingConversion()
    {
        // Local is the one Kind that legitimately reads the host zone — and there the
        // conversion preserves the instant (matches ToUniversalTime).
        var obj = new SurrealObject();
        var value = new DateTime(2026, 7, 2, 10, 30, 15, DateTimeKind.Local);

        obj.Set("at", value);

        var stored = Assert.IsType<SurrealDateTimeValue>(obj["at"]);
        Assert.Equal(value.ToUniversalTime(), stored.SurrealDateTime.ToDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public void Set_NullableDateTime_WithValue_StoresInstant_NullOmitsKey()
    {
        var obj = new SurrealObject();

        obj.Set("set", (DateTime?)new DateTime(2026, 7, 2, 10, 30, 15, DateTimeKind.Unspecified));
        obj.Set("unset", (DateTime?)null);

        Assert.True(obj.ContainsKey("set"));
        Assert.False(obj.ContainsKey("unset"));
    }

    [Fact]
    public void Set_UnspecifiedDateTime_RoundTripsLosslesslyThroughHydration()
    {
        // Hydration returns UtcDateTime — with Unspecified-as-UTC on the write side the
        // round-trip preserves both the instant and the wall clock.
        var obj = new SurrealObject();
        var written = new DateTime(2026, 7, 2, 10, 30, 15, DateTimeKind.Unspecified);

        obj.Set("at", written);
        var read = HydrationValue.ReadOrDefault<DateTime>(new SurrealObjectValue(obj), "at");

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(written, DateTime.SpecifyKind(read, DateTimeKind.Unspecified));
    }

    // ─────────────────────── Guid as string ───────────────────────

    [Fact]
    public void Set_Guid_StoresCanonicalDFormatString()
    {
        var obj = new SurrealObject();

        obj.Set("external_id", Guid.Parse("8f7f9de2-3c5a-4b7e-9f24-0a1b2c3d4e5f"));

        Assert.Equal(new StringSurrealValue("8f7f9de2-3c5a-4b7e-9f24-0a1b2c3d4e5f"), obj["external_id"]);
    }

    [Fact]
    public void Set_NullableGuid_WithValue_StoresString_NullOmitsKey()
    {
        var obj = new SurrealObject();
        var guid = Guid.Parse("8f7f9de2-3c5a-4b7e-9f24-0a1b2c3d4e5f");

        obj.Set("set", (Guid?)guid);
        obj.Set("unset", (Guid?)null);

        Assert.Equal(new StringSurrealValue("8f7f9de2-3c5a-4b7e-9f24-0a1b2c3d4e5f"), obj["set"]);
        Assert.False(obj.ContainsKey("unset"));
    }

    [Fact]
    public void Set_Guid_RoundTripsThroughHydration()
    {
        var obj = new SurrealObject();
        var written = Guid.NewGuid();

        obj.Set("external_id", written);
        var read = HydrationValue.ReadOrDefault<Guid>(new SurrealObjectValue(obj), "external_id");

        Assert.Equal(written, read);
    }

    [Fact]
    public void Hydration_ReadsGuid_FromBothStringAndUuidPayloads()
    {
        // The read side stays tolerant of both shapes: strings (what this library
        // writes) and CBOR uuids (rows written by other tools).
        var guid = Guid.Parse("8f7f9de2-3c5a-4b7e-9f24-0a1b2c3d4e5f");
        var row = new SurrealObjectValue(new SurrealObject
        {
            ["as_string"] = guid.ToString("D"),
            ["as_uuid"] = new SurrealUuidValue(guid),
        });

        Assert.Equal(guid, HydrationValue.ReadOrDefault<Guid>(row, "as_string"));
        Assert.Equal(guid, HydrationValue.ReadOrDefault<Guid>(row, "as_uuid"));
    }
}
