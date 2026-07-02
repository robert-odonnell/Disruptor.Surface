using System.Globalization;
using Disruptor.Surface.Generator.Emit;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// Naming must be machine-independent. Humanizer 3.x lower-cases (<c>Underscore</c>)
/// and regex-matches (<c>Pluralize</c>/<c>Singularize</c>) with the thread's current
/// culture, so before <see cref="SurrealNaming"/> pinned the invariant culture a tr-TR
/// build machine baked <c>ıssues</c> (dotless ı, U+0131) instead of <c>issues</c> into
/// the schema — generated code still compiled, but the schema was silently incompatible
/// with every other machine's. These tests run naming (and a full generation) under
/// tr-TR and assert ASCII snake_case output.
/// </summary>
public sealed class CultureInvarianceTests
{
    [Fact]
    public void SurrealNaming_UnderTurkishCulture_ProducesAsciiSnakeCase()
    {
        RunUnderCulture("tr-TR", () =>
        {
            // "I" → "ı" under tr-TR casing rules; every name below contains one.
            Assert.Equal("issues", SurrealNaming.ToTableName("Issue"));
            Assert.Equal("issue_kind", SurrealNaming.ToFieldName("IssueKind"));
            Assert.Equal("informs", SurrealNaming.ToEdgeName("InformsAttribute"));
            Assert.Equal("Issues", SurrealNaming.PascalPluralize("Issue"));
            Assert.Equal("Inform", SurrealNaming.Singularize("Informs"));
        });
    }

    [Fact]
    public void Generation_UnderTurkishCulture_BakesAsciiIdentifiersIntoSchema()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table] public partial class Issue {
                [Id] public partial IssueId Id { get; set; }
                [Property] public partial string ImpactSummary { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        RunUnderCulture("tr-TR", () =>
        {
            var (result, _, runDiags, _) = GeneratorHarness.Run(src);
            Assert.DoesNotContain(runDiags, d => d.Severity == DiagnosticSeverity.Error);

            var schema = GeneratorHarness.FindGeneratedFile(result, "Schema.g.cs");
            Assert.NotNull(schema);
            var ddl = schema.ToString();
            Assert.Contains("DEFINE TABLE IF NOT EXISTS issues SCHEMAFULL;", ddl);
            Assert.Contains("DEFINE FIELD IF NOT EXISTS impact_summary ON issues", ddl);
            // The tr-TR failure mode: dotless ı (U+0131) baked into the DDL.
            Assert.DoesNotContain('ı', ddl);

            // Query-root accessor stays a plain ASCII pluralised identifier too.
            var query = GeneratorHarness.FindGeneratedFile(result, "Query.g.cs");
            Assert.NotNull(query);
            Assert.Contains("Issues", query.ToString());
            Assert.DoesNotContain('ı', query.ToString());
        });
    }

    private static void RunUnderCulture(string cultureName, Action body)
    {
        // RS1035 flows in via the Microsoft.CodeAnalysis analyzers; it guards *analyzer*
        // code against culture-dependent behaviour. This is a test that deliberately
        // simulates a non-invariant build machine, so the culture swap is the point.
#pragma warning disable RS1035
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
#pragma warning restore RS1035
    }
}
