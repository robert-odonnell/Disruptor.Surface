using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Value-equatable snapshot of a source location — file path, absolute text span, and
/// line/character span. Pipeline models must never retain <see cref="Location"/> /
/// <c>ISymbol</c> / <c>SyntaxNode</c> instances (they root entire compilations and are
/// reference-equal across runs, busting the incremental cache); this record captures the
/// primitives <see cref="Location.Create(string, TextSpan, LinePositionSpan)"/> needs so
/// a real location can be rehydrated at diagnostic-report time.
/// <para>
/// <b>Caching caveat — read before adding this to a model.</b> A LocationInfo is
/// position-sensitive: any edit above the captured declaration shifts the span and
/// changes record equality, which re-runs every downstream pipeline stage. It is only
/// safe in two places: (a) issue models that represent an already-failing build (the
/// model exists only when a CG error is about to fire, so re-runs cost nothing on the
/// healthy path — keep the field <c>null</c> on healthy models), and (b) the dedicated
/// declaration-location map that feeds the diagnostics-only output (never the emitters'
/// input). See the "Diagnostic source locations" section in <c>docs/architecture.md</c>.
/// </para>
/// </summary>
public sealed record LocationInfo(
    string FilePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter)
{
    /// <summary>
    /// Captures a <see cref="LocationInfo"/> from a Roslyn <see cref="Location"/>.
    /// Returns <c>null</c> for null / non-source locations (there is nothing stable to
    /// point at).
    /// </summary>
    public static LocationInfo? FromLocation(Location? location)
    {
        if (location is null || location.SourceTree is null)
        {
            return null;
        }

        var lineSpan = location.GetLineSpan();
        return new LocationInfo(
            FilePath: lineSpan.Path ?? string.Empty,
            Start: location.SourceSpan.Start,
            Length: location.SourceSpan.Length,
            StartLine: lineSpan.StartLinePosition.Line,
            StartCharacter: lineSpan.StartLinePosition.Character,
            EndLine: lineSpan.EndLinePosition.Line,
            EndCharacter: lineSpan.EndLinePosition.Character);
    }

    public static LocationInfo? FromToken(SyntaxToken token) => FromLocation(token.GetLocation());

    public static LocationInfo? FromSyntax(SyntaxNode? node) => node is null ? null : FromLocation(node.GetLocation());

    /// <summary>
    /// Rehydrates a reportable <see cref="Location"/>. This is an external-file location
    /// (no syntax tree), which the generator driver accepts without requiring the tree to
    /// be part of the compilation; IDEs navigate it via path + line span.
    /// </summary>
    public Location ToLocation()
        => Location.Create(
            FilePath,
            new TextSpan(Start, Length),
            new LinePositionSpan(
                new LinePosition(StartLine, StartCharacter),
                new LinePosition(EndLine, EndCharacter)));
}
