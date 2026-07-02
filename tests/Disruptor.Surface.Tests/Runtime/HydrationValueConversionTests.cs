using Disruptor.Surface.Runtime;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Materialise-side conversions for value shapes that were previously bindable in
/// <c>Where</c> but unreadable at hydration time: <see cref="TimeSpan"/> (from a
/// SurrealDB duration) and enums. Enums aren't schema-mappable today, so stored enum
/// values only arrive from rows written by other tools / hand-written statements —
/// both wire shapes those produce are accepted: the member name as a string (matching
/// what the query binder emits for enum operands; parsed case-insensitively) and the
/// numeric value as an int64.
/// </summary>
public sealed class HydrationValueConversionTests
{
    private enum Status
    {
        Open = 0,
        Acknowledged = 3,
    }

    [Fact]
    public void ReadOrDefault_TimeSpan_FromDurationValue_RoundsTrip()
    {
        var span = new TimeSpan(days: 1, hours: 2, minutes: 3, seconds: 4, milliseconds: 500);
        var obj = new SurrealObjectValue(new SurrealObject
        {
            ["elapsed"] = new SurrealDurationValue(new SurrealDuration(span)),
        });

        Assert.Equal(span, HydrationValue.ReadOrDefault<TimeSpan>(obj, "elapsed"));
    }

    [Fact]
    public void ReadOrDefault_NullableTimeSpan_FromNullAndFromValue()
    {
        var span = TimeSpan.FromMinutes(90);
        var obj = new SurrealObjectValue(new SurrealObject
        {
            ["a"] = SurrealValue.Null,
            ["b"] = new SurrealDurationValue(new SurrealDuration(span)),
        });

        Assert.Null(HydrationValue.ReadOrDefault<TimeSpan?>(obj, "a"));
        Assert.Equal(span, HydrationValue.ReadOrDefault<TimeSpan?>(obj, "b"));
    }

    [Fact]
    public void ReadOrDefault_Enum_FromMemberNameString_Parses()
    {
        // The exact shape the query binder writes for an enum operand: e.ToString().
        var obj = new SurrealObjectValue(new SurrealObject { ["status"] = "Acknowledged" });

        Assert.Equal(Status.Acknowledged, HydrationValue.ReadOrDefault<Status>(obj, "status"));
    }

    [Fact]
    public void ReadOrDefault_Enum_FromLowercaseString_ParsesCaseInsensitively()
    {
        // Hand-written rows commonly store snake/lowercase names; parsing is
        // case-insensitive so they still materialise.
        var obj = new SurrealObjectValue(new SurrealObject { ["status"] = "acknowledged" });

        Assert.Equal(Status.Acknowledged, HydrationValue.ReadOrDefault<Status>(obj, "status"));
    }

    [Fact]
    public void ReadOrDefault_Enum_FromNumber_ConvertsViaUnderlyingValue()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["status"] = 3 });

        Assert.Equal(Status.Acknowledged, HydrationValue.ReadOrDefault<Status>(obj, "status"));
    }

    [Fact]
    public void ReadOrDefault_NullableEnum_FromNull_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["status"] = SurrealValue.Null });

        Assert.Null(HydrationValue.ReadOrDefault<Status?>(obj, "status"));
    }

    [Fact]
    public void ReadOrDefault_NullableEnum_FromString_ReturnsValue()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["status"] = "Open" });

        Assert.Equal(Status.Open, HydrationValue.ReadOrDefault<Status?>(obj, "status"));
    }

    [Fact]
    public void ReadOrDefault_Enum_FromUnknownName_ThrowsLoudly()
    {
        // Fail-closed: an unrecognised member name must not silently collapse to the
        // zero member.
        var obj = new SurrealObjectValue(new SurrealObject { ["status"] = "nonsense" });

        Assert.ThrowsAny<ArgumentException>(() => HydrationValue.ReadOrDefault<Status>(obj, "status"));
    }
}
