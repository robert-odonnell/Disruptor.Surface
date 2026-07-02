using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

public sealed class RecordIdTests
{
    [Fact]
    public void Parse_Splits_TableAndValue_OnFirstColon()
    {
        var id = RecordId.Parse("constraints:01HX");
        Assert.Equal("constraints", id.Table);
        Assert.Equal("01HX", id.Value);
    }

    [Fact]
    public void Parse_Keeps_AllSubsequentColons_InValue()
    {
        // SurrealDB allows colons in record-id values (e.g. compound keys, rare).
        // Parse splits on the FIRST colon; everything after is the value verbatim.
        var id = RecordId.Parse("compound:a:b:c");
        Assert.Equal("compound", id.Table);
        Assert.Equal("a:b:c", id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("missing-colon")]
    public void TryParse_RejectsMalformed_AndReturnsFalse(string? source)
    {
        Assert.False(RecordId.TryParse(source, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Parse_Throws_OnMalformedInput()
    {
        Assert.Throws<InvalidOperationException>(() => RecordId.Parse("no-colon"));
    }

    [Fact]
    public void ToString_RoundTrips_ThroughParse()
    {
        var original = new RecordId("designs", "01ABC");
        var roundTripped = RecordId.Parse(original.ToString());
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void From_PassThrough_ForCanonicalRecordId()
    {
        var canonical = new RecordId("epics", "x");
        // Concrete struct passed through IRecordId — From should not allocate a new one,
        // and the value should compare equal.
        IRecordId boxed = canonical;
        Assert.Equal(canonical, RecordId.From(boxed));
    }

    [Fact]
    public void New_GeneratesUlidValue_ForGivenTable()
    {
        var id = RecordId.New("constraints");
        Assert.Equal("constraints", id.Table);
        // Ulids are 26 chars Crockford base32 — sanity check the shape rather than the
        // literal value, since it's nondeterministic.
        Assert.Equal(26, id.Value.Length);
    }

    [Fact]
    public void IsForTable_IsCaseSensitive()
    {
        var id = new RecordId("designs", "v");
        Assert.True(id.IsForTable("designs"));
        Assert.False(id.IsForTable("Designs"));
    }

    [Fact]
    public void FromText_ProducesDeterministicIdForGivenTable()
    {
        // Same table+text always yields the same RecordId — the contract that makes
        // this useful for content-addressed records like code-index entries.
        var a = RecordId.FromText("symbols", "Disruptor.Surface.Runtime.SurrealSession");
        var b = RecordId.FromText("symbols", "Disruptor.Surface.Runtime.SurrealSession");

        Assert.Equal(a, b);
        Assert.Equal("symbols", a.Table);
        Assert.Equal(RecordIdFormat.HashLength, a.Value.Length);
    }

    [Fact]
    public void FromText_WithPrefix_EmbedsPrefixInValue()
    {
        var id = RecordId.FromText("symbols", "Foo", prefix: 'm');

        Assert.Equal("symbols", id.Table);
        Assert.StartsWith("m_", id.Value);
        Assert.Equal(RecordIdFormat.PrefixedHashLength, id.Value.Length);
    }

    [Fact]
    public void IsIdempotent_OnDefaultRecordId_IsFalse_NotNre()
    {
        // default(RecordId) has both components null — it's the in-band "no endpoint"
        // sentinel the library itself logs for bulk Unrelate (Command.Unrelate). Reading
        // IsIdempotent on it used to NRE on Value.Length; it must simply be false.
        Assert.False(default(RecordId).IsIdempotent);
    }

    [Fact]
    public void IsIdempotent_TrueForSentinel_FalseForRegularIds()
    {
        Assert.True(RecordId.Idempotent("restricts").IsIdempotent);
        Assert.False(RecordId.New("designs").IsIdempotent);
    }

    [Fact]
    public void ForEdge_IsDeterministic_AndProducesValidHashFormValue()
    {
        // The derivation the emitted variant id anchor uses (2026-07-02): same
        // (source, edge, target) triple always yields the same edge id, and the value
        // is the hash form the {Kind}Id ctor's RecordIdFormat validation accepts.
        var src = new RecordId("constraints", "01H0000000000000000000000A");
        var tgt = new RecordId("epics", "01H0000000000000000000000B");

        var a = RecordId.ForEdge("restricts", src, tgt);
        var b = RecordId.ForEdge("restricts", src, tgt);

        Assert.Equal(a, b);
        Assert.Equal("restricts", a.Table);
        Assert.True(RecordIdFormat.IsValid(a.Value));
        Assert.Equal(RecordIdFormat.HashLength, a.Value.Length);
    }

    [Fact]
    public void ForEdge_DiffersByAnyTripleComponent()
    {
        var src = new RecordId("constraints", "01H0000000000000000000000A");
        var tgt = new RecordId("epics", "01H0000000000000000000000B");
        var baseline = RecordId.ForEdge("restricts", src, tgt);

        Assert.NotEqual(baseline.Value, RecordId.ForEdge("validates", src, tgt).Value);
        Assert.NotEqual(baseline, RecordId.ForEdge("restricts", tgt, src));
        Assert.NotEqual(baseline, RecordId.ForEdge("restricts", src, new RecordId("epics", "01H0000000000000000000000C")));
    }

    [Fact]
    public void ForEdge_MatchesResolveOnTheIdempotentSentinel()
    {
        // Resolve delegates to ForEdge — one derivation scheme, whether the caller
        // goes through the sentinel by hand or the variant save path derives directly.
        var src = new RecordId("constraints", "01H0000000000000000000000A");
        var tgt = new RecordId("epics", "01H0000000000000000000000B");

        Assert.Equal(
            RecordId.Idempotent("restricts").Resolve(src, tgt),
            RecordId.ForEdge("restricts", src, tgt));
    }
}
