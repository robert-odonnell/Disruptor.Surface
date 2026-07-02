using Microsoft.CodeAnalysis;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// Each test feeds a tiny fixture that violates one rule and asserts that the
/// corresponding CGxxx diagnostic fires. Catches accidental tightening / loosening
/// of the diagnostics surface from generator refactors.
/// </summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public void CG001_TableMustBePartial()
    {
        const string src = """
                           using Disruptor.Surface.Annotations;
                           namespace M;
                           [Table] public class NotPartial {
                               [Id] public partial NotPartialId Id { get; set; }
                           }
                           """;
        _ = GeneratorHarness.Run(src);
        // CG001 reports against `compileDiags` (Errors), not run-time diagnostics.
        // Actually wait — CG001 fires from ModelGenerator.Emit, so it shows up in run diagnostics.
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG001");
    }

    [Fact]
    public void NoIdAttribute_IsLegal_AnchorIsAlwaysEmitted()
    {
        // [Id] is optional — without it, the entity has no public-facing Id surface, but
        // the runtime still works because PartialEmitter unconditionally emits the _id
        // anchor and IEntity.Id, and IdEmitter unconditionally emits the {Name}Id struct.
        // CG007 was retired alongside this change.
        const string src = """
                           using Disruptor.Surface.Annotations;
                           namespace M;
                           [Table] public partial class NoId {
                               [Property] public partial string Name { get; set; }
                           }
                           """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG007");
        Assert.Empty(runDiags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void CG008_TableHasMultipleIds()
    {
        const string src = """
                           using Disruptor.Surface.Annotations;
                           namespace M;
                           [Table] public partial class TwoIds {
                               [Id] public partial TwoIdsId IdA { get; set; }
                               [Id] public partial TwoIdsId IdB { get; set; }
                           }
                           """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG008");
    }

    [Fact]
    public void CG024_PropertyAndReference_OnSameMember_Conflict()
    {
        // The five role attributes each select a distinct emit shape; PartialEmitter
        // and SchemaEmitter previously disagreed on precedence ([Property] before
        // [Reference] in the CLR emit, [Reference] before [Property] in schema), so
        // the combo silently produced a scalar CLR setter writing into a record<>
        // schema column. CG024 rejects the combo at the model boundary.
        const string src = """
                           using Disruptor.Surface.Annotations;
                           namespace M;

                           [Table] public partial class Owner {
                               [Id] public partial OwnerId Id { get; set; }
                               // Conflict: Property + Reference are mutually-exclusive role attributes.
                               [Property, Reference] public partial Target Both { get; }
                           }

                           [Table] public partial class Target {
                               [Id] public partial TargetId Id { get; set; }
                           }
                           """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG024");
        // The message names both offending attributes so the user knows which to drop.
        var diag = runDiags.First(d => d.Id == "CG024");
        Assert.Contains("Property", diag.GetMessage());
        Assert.Contains("Reference", diag.GetMessage());
    }

    [Fact]
    public void CG022_AnnotatedMember_MustBePartial()
    {
        // The generator emits the implementation half of every annotated property
        // (backing field, getter/setter, hydrate body). A non-partial declaration
        // means the user-side and generator-side can't combine, so EmitHydrate
        // would write `_someField = ...` for storage that never got emitted (CS0103).
        // CG022 catches this with a clear message before bad .g.cs is produced.
        const string src = """
                           using Disruptor.Surface.Annotations;
                           namespace M;
                           [Table] public partial class HasNonPartialProperty {
                               [Id] public partial HasNonPartialPropertyId Id { get; set; }
                               [Property] public string Description { get; set; }
                           }
                           """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG022");
    }

    [Fact]
    public void CG011_EntityReachable_FromTwoAggregateRoots()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class A {
                [Id] public partial AId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Shared> Items { get; }
            }

            [Table, AggregateRoot] public partial class B {
                [Id] public partial BId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Shared> Items { get; }
            }

            [Table] public partial class Shared {
                [Id] public partial SharedId Id { get; set; }
                [Parent] public partial A ParentA { get; set; }
                [Parent] public partial B ParentB { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG011");
    }

    [Fact]
    public void CG018_MultipleCompositionRoots()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [CompositionRoot] public partial class WorkspaceA { }
            [CompositionRoot] public partial class WorkspaceB { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG018");
    }

    [Fact]
    public void CG019_CompositionRoot_NotPartial()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [CompositionRoot] public class NotPartialWorkspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG019");
    }

    [Fact]
    public void CG020_Children_Without_MatchingParent_Path()
    {
        // `Lonely` is listed as Design's Children member but has no [Parent] back to
        // Design — the loader's $parent.id-scoped query has nothing to anchor on.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Lonely> Lonelies { get; }
            }

            [Table] public partial class Lonely {
                [Id] public partial LonelyId Id { get; set; }
                // No [Parent] property pointing at Design.
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG020");
    }

    [Fact]
    public void CG021_Reference_CrossesAggregateBoundary()
    {
        // Constraint is in the Design aggregate (via [Children]); Finding is in the
        // Review aggregate. A [Reference] from Constraint to Finding crosses
        // aggregate boundaries — should fire CG021.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
            }

            [Table, AggregateRoot] public partial class Review {
                [Id] public partial ReviewId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Finding> Findings { get; }
            }

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Reference] public partial Finding? OffendingFinding { get; set; }   // crosses aggregate
            }

            [Table] public partial class Finding {
                [Id] public partial FindingId Id { get; set; }
                [Parent] public partial Review Review { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG021");
    }

    [Fact]
    public void CG021_Allows_Reference_ToSharedRecord_NotInAnyAggregate()
    {
        // Details is referenced from Design but isn't a member of any aggregate
        // (no [Parent] back to a root). Shared/foreign records like this are fine.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Reference] public partial Details? Details { get; set; }
            }

            [Table] public partial class Details {
                [Id] public partial DetailsId Id { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG021");
    }

    [Fact]
    public void HappyPath_ProducesNoDiagnostics()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Property] public partial string Description { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
            }

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Property] public partial string Description { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        // No CGxxx errors. (Warnings — like CG017 — are allowed; the model has none here.)
        Assert.DoesNotContain(runDiags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void CG025_PropertyTypeNotMappable()
    {
        // Uri has no SurrealDB scalar mapping. Without the diagnostic, SchemaEmitter
        // would silently comment out the field and reads/writes would fail at the DB.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Property] public partial Uri Endpoint { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG025");
    }

    [Fact]
    public void CG025_DoesNotFire_OnElementCollectionProperty()
    {
        // Element-collection [Property] (List<T> / IList<T> / IReadOnlyList<T> of records)
        // goes through the array<object> + sub-field schema path; it's not a scalar but
        // it's mappable — CG025 must not flag it.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;
            public sealed record Scenario(string Kind, string Description);
            [Table] public partial class Holder {
                [Id] public partial HolderId Id { get; set; }
                [Property] public partial IReadOnlyList<Scenario> Scenarios { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG025");
    }

    [Fact]
    public void CG026_ChildrenElementMustBeTable()
    {
        // [Children]<string> — concrete element, but not a [Table]. Without the
        // diagnostic, the emitted Session.QueryChildren<string>(...) would surface
        // as a generic-constraint CS error in generated code (T : IEntity, new()).
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Children] public partial IReadOnlyCollection<string> Tags { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG026");
    }

    [Fact]
    public void CG027_ParentMustTargetTable()
    {
        // [Parent] target must be a [Table] — pointing at a plain class would emit
        // Session.GetParent<T>(this) where T isn't IEntity.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            public sealed class NotATable { }
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Parent] public partial NotATable Parent { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG027");
    }

    [Fact]
    public void CG028_AnnotatedMemberMustNotBeStatic()
    {
        // Static partial properties don't fit the per-instance emit shape (backing
        // field, _session, identity-map). The diagnostic catches the mistake at the
        // model boundary instead of letting confusing CS errors surface from the
        // generated partial.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Property] public static partial string Greeting { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG028");
    }

    [Fact]
    public void CG037_IndexRequiresPersistedEntityField()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public sealed class LookupAttribute : IndexAttribute;

            [Table]
            public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Lookup] public partial string ExternalKey { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG037");
    }

    [Fact]
    public void CG038_CompositeIndexFieldsMustStayInSamePartialDeclaration()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public sealed class ByExternalAndStatusAttribute : IndexAttribute;

            [Table]
            public partial class Story {
                [Id] public partial StoryId Id { get; set; }
                [ByExternalAndStatus, Property] public partial string ExternalKey { get; set; }
            }

            public partial class Story {
                [ByExternalAndStatus, Property] public partial string Status { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG038");
    }

    [Fact]
    public void CG039_UniqueIndexRejectsNullableFields()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public sealed class SlugAttribute : UniqueIndexAttribute;

            [Table]
            public partial class Story {
                [Id] public partial StoryId Id { get; set; }
                [Slug, Property] public partial string? Slug { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG039");
    }

    [Fact]
    public void CG040_IndexSchemaNameCollision()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public static class A {
                public sealed class LookupAttribute : IndexAttribute;
            }

            public static class B {
                public sealed class LookupAttribute : IndexAttribute;
            }

            [Table]
            public partial class Story {
                [Id] public partial StoryId Id { get; set; }
                [A.Lookup, Property] public partial string ExternalKey { get; set; }
                [B.Lookup, Property] public partial string Slug { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG040");
    }

    [Fact]
    public void CG041_IndexAttributeCanOnlyAppearOncePerField()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public sealed class LookupAttribute : IndexAttribute;

            [Table]
            public partial class Story {
                [Id] public partial StoryId Id { get; set; }
                [Lookup, Lookup, Property] public partial string ExternalKey { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG041");
    }

    [Fact]
    public void CG042_TwoTables_SameSimpleName_AcrossNamespaces_CollideOnTableName()
    {
        // A.Design and B.Design both map to physical table `designs`. Before CG042 the
        // schema silently interleaved both field sets onto one table and the generated
        // query/hydration roots collided with CS0102/CS0111.
        const string src = """
            using Disruptor.Surface.Annotations;

            namespace A {
                [Table] public partial class Design {
                    [Property] public partial string Name { get; set; }
                }
            }

            namespace B {
                [Table] public partial class Design {
                    [Property] public partial string Title { get; set; }
                }
            }

            namespace M {
                [CompositionRoot] public partial class Workspace { }
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG042");

        // Message names both fully-qualified classes and the colliding table name.
        var message = runDiags.First(d => d.Id == "CG042").GetMessage();
        Assert.Contains("A.Design", message);
        Assert.Contains("B.Design", message);
        Assert.Contains("'designs'", message);

        // Fail-closed: no generator crash, and the colliding duplicate is not emitted —
        // the schema defines `designs` exactly once.
        Assert.All(result.Results, r => Assert.Null(r.Exception));
        var schema = GeneratorHarness.FindGeneratedFile(result, "Schema.g.cs");
        Assert.NotNull(schema);
        var defines = schema.ToString().Split("DEFINE TABLE IF NOT EXISTS designs").Length - 1;
        Assert.Equal(1, defines);
    }

    [Fact]
    public void CG042_TwoTables_DistinctNames_PluralizeToSameTableName()
    {
        // Item and Items both pluralise to `items` — distinct simple names, same
        // physical table name.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table] public partial class Item {
                [Property] public partial string Name { get; set; }
            }

            [Table] public partial class Items {
                [Property] public partial string Name { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG042");

        var message = runDiags.First(d => d.Id == "CG042").GetMessage();
        Assert.Contains("M.Item", message);
        Assert.Contains("M.Items", message);
        Assert.Contains("'items'", message);
    }

    [Fact]
    public void CG043_TwoForwardKinds_SameSimpleName_CollideOnEdgeName()
    {
        // Two ReferencesAttribute kinds in different namespaces both derive edge table
        // `references` — the exact shape the shipped sample used to have. Before CG043
        // the generated schema defined the edge table twice with disagreeing FROM/TO.
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;

            namespace A {
                public sealed class ReferencesAttribute : ForwardRelation;
            }

            namespace B {
                public sealed class ReferencesAttribute : ForwardRelation;
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG043");

        var message = runDiags.First(d => d.Id == "CG043").GetMessage();
        Assert.Contains("A.ReferencesAttribute", message);
        Assert.Contains("B.ReferencesAttribute", message);
        Assert.Contains("'references'", message);
        Assert.All(result.Results, r => Assert.Null(r.Exception));
    }

    [Fact]
    public void CG044_TwoAggregateRoots_SameSimpleName_CollideOnLoaderNames()
    {
        // Aggregate loader classes, Load{Name}Async members, and their AddSource hints
        // all key on the root's simple name. Before CG044 this crashed the whole
        // generator with a duplicate-hint ArgumentException (CS8785) and zero CG
        // diagnostics.
        const string src = """
            using Disruptor.Surface.Annotations;

            namespace A {
                [Table, AggregateRoot] public partial class Design {
                    [Id] public partial DesignId Id { get; set; }
                }
            }

            namespace B {
                [Table, AggregateRoot] public partial class Design {
                    [Id] public partial DesignId Id { get; set; }
                }
            }

            namespace M {
                [CompositionRoot] public partial class Workspace { }
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG044");

        var message = runDiags.First(d => d.Id == "CG044").GetMessage();
        Assert.Contains("A.Design", message);
        Assert.Contains("B.Design", message);
        Assert.Contains("'Design'", message);

        // The duplicate simple name also collides on the physical table name.
        Assert.Contains(runDiags, d => d.Id == "CG042");

        // Fail-closed: no duplicate-hint crash — exactly one aggregate loader emitted.
        Assert.All(result.Results, r => Assert.Null(r.Exception));
        var loaders = result.GeneratedTrees
            .Where(t => t.FilePath.Contains("DesignAggregateLoader", StringComparison.Ordinal))
            .ToList();
        Assert.Single(loaders);
    }

    [Fact]
    public void CG045_NestedTable_IsRejected()
    {
        // TableExtractor used to build FullName as {namespace}.{MetadataName}, dropping
        // containing types — a nested [Table] emitted an orphan namespace-scoped partial
        // (CS9248/CS9249 storm). CG045 rejects the shape and skips emission.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public partial class Outer {
                [Table] public partial class Inner {
                    [Property] public partial string Name { get; set; }
                }
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG045");

        var message = runDiags.First(d => d.Id == "CG045").GetMessage();
        Assert.Contains("M.Outer.Inner", message);
        Assert.Contains("[Table]", message);

        // Skipped emission: no partial fragment for the nested table.
        Assert.All(result.Results, r => Assert.Null(r.Exception));
        Assert.DoesNotContain(result.GeneratedTrees,
            t => t.FilePath.Contains("Outer.Inner", StringComparison.Ordinal));
    }

    [Fact]
    public void CG045_NestedCompositionRoot_IsRejected()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public partial class Outer {
                [CompositionRoot] public partial class Workspace { }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG045");
        var message = runDiags.First(d => d.Id == "CG045").GetMessage();
        Assert.Contains("M.Outer.Workspace", message);
        Assert.Contains("[CompositionRoot]", message);
    }

    [Fact]
    public void CG046_VariantWithDuplicateInRole_IsRejected()
    {
        // Two [In] members on one variant used to return null from the extractor with a
        // comment promising a diagnostic "in a later phase" that could never fire — the
        // user got a CS9248 wall (their partial props had no implementation half) and
        // zero CG errors. Now the variant is filtered and CG046 names variant + role.
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }

            [Table] public partial class UserStory {
                [Id] public partial UserStoryId Id { get; set; }
            }

            [Restricts]
            public partial class TwoIns {
                [In] public partial Constraint SourceA { get; set; }
                [In] public partial Constraint SourceB { get; set; }
                [Out] public partial UserStory Target { get; set; }
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG046");

        var message = runDiags.First(d => d.Id == "CG046").GetMessage();
        Assert.Contains("M.TwoIns", message);
        Assert.Contains("[In]", message);

        // Fail-closed: no generator crash and no emission for the malformed variant.
        Assert.All(result.Results, r => Assert.Null(r.Exception));
        Assert.DoesNotContain(result.GeneratedTrees,
            t => t.FilePath.Contains("TwoIns", StringComparison.Ordinal));
    }

    [Fact]
    public void CG046_VariantWithDuplicateIdRole_IsRejected()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }

            [Restricts]
            public partial class TwoIds {
                [Id] public partial RestrictsId IdA { get; set; }
                [Id] public partial RestrictsId IdB { get; set; }
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Constraint Target { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        var diag = runDiags.FirstOrDefault(d => d.Id == "CG046");
        Assert.NotNull(diag);
        Assert.Contains("M.TwoIds", diag!.GetMessage());
        Assert.Contains("[Id]", diag.GetMessage());
    }

    [Fact]
    public void CG047_VariantWithNoResolvableEndpoints_IsRejected()
    {
        // A variant with a missing [In] is allowed through extraction (the linker may
        // lift endpoints from an annotated shared-shape interface), but when no such
        // interface exists the lift resolves nothing — previously the variant was
        // dropped silently at RelationLinker.LiftVariantsFromSharedShape.
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;

            [Table] public partial class UserStory {
                [Id] public partial UserStoryId Id { get; set; }
            }

            [Restricts]
            public partial class NoSource {
                [Out] public partial UserStory Target { get; set; }
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG047");

        var message = runDiags.First(d => d.Id == "CG047").GetMessage();
        Assert.Contains("M.NoSource", message);
        Assert.Contains("[In]", message);

        Assert.All(result.Results, r => Assert.Null(r.Exception));
    }

    [Fact]
    public void CG046_CG047_DoNotFire_OnWellFormedVariant()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }

            [Table] public partial class UserStory {
                [Id] public partial UserStoryId Id { get; set; }
            }

            [Restricts]
            public partial class ConstraintRestrictsUserStory {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial UserStory Target { get; set; }
                [Property] public partial string Reason { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id is "CG046" or "CG047");
    }

    [Fact]
    public void CG048_TableOnRecord_IsRejected()
    {
        // AttributeTargets.Class admits records, but the old FAWMN predicate only
        // matched ClassDeclarationSyntax — `[Table] partial record X` compiled clean
        // and generated nothing. The predicate now admits records and CG048 rejects.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table] public partial record Snapshot {
                [Property] public partial string Name { get; set; }
            }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG048");

        var message = runDiags.First(d => d.Id == "CG048").GetMessage();
        Assert.Contains("M.Snapshot", message);
        Assert.Contains("[Table]", message);

        // Fail-closed: nothing emitted for the record.
        Assert.All(result.Results, r => Assert.Null(r.Exception));
        Assert.DoesNotContain(result.GeneratedTrees,
            t => t.FilePath.Contains("Snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void CG048_CompositionRootOnRecord_IsRejected()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [CompositionRoot] public partial record Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        var diag = runDiags.FirstOrDefault(d => d.Id == "CG048");
        Assert.NotNull(diag);
        Assert.Contains("M.Workspace", diag!.GetMessage());
        Assert.Contains("[CompositionRoot]", diag.GetMessage());
    }

    [Fact]
    public void CG048_RelationKindOnRecordVariant_IsRejected()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }

            [Restricts]
            public partial record RecordVariant {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Constraint Target { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        var diag = runDiags.FirstOrDefault(d => d.Id == "CG048");
        Assert.NotNull(diag);
        Assert.Contains("M.RecordVariant", diag!.GetMessage());
        Assert.Contains("[Restricts]", diag.GetMessage());
    }

    [Fact]
    public void CG049_GenericTable_IsRejected()
    {
        // Generic [Table] was half-supported: the partial declaration compiled but the
        // physical table name ignored type arguments (Foo<int> and Foo<string> would
        // share one table), FullName (Ns.Foo`1) never matched any use-site lookup, and
        // the generated query/hydration roots referenced the open generic (CS0305).
        // Rejected fail-closed with CG049.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table] public partial class Box<T> {
                [Property] public partial string Label { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG049");

        var message = runDiags.First(d => d.Id == "CG049").GetMessage();
        Assert.Contains("M.Box`1", message);
        Assert.Contains("<T>", message);

        // Fail-closed: no partial fragment, no query-root accessor for the generic.
        Assert.All(result.Results, r => Assert.Null(r.Exception));
        Assert.DoesNotContain(result.GeneratedTrees,
            t => t.FilePath.Contains("Box", StringComparison.Ordinal));
    }

    [Fact]
    public void CG049_DoesNotFire_OnNonGenericTable()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table] public partial class Plain {
                [Property] public partial string Name { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG049");
    }

    [Fact]
    public void CG050_ElementCollectionOfUnmappableElement_IsRejected()
    {
        // List<Uri> used to fall to the scalar save path (no ContentValue.Set overload
        // → CS1503 in the .g.cs) with no diagnostic. CG050 rejects at the model boundary.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System;
            using System.Collections.Generic;
            namespace M;

            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Property] public partial IReadOnlyList<Uri> Links { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        var diag = runDiags.FirstOrDefault(d => d.Id == "CG050");
        Assert.NotNull(diag);
        Assert.Contains("M.Bad", diag!.GetMessage());
        Assert.Contains("Links", diag.GetMessage());
        Assert.Contains("Uri", diag.GetMessage());
    }

    [Fact]
    public void CG050_NullableElementCollection_IsRejected()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Property] public partial IReadOnlyList<int?> Counts { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        var diag = runDiags.FirstOrDefault(d => d.Id == "CG050");
        Assert.NotNull(diag);
        Assert.Contains("nullable element types", diag!.GetMessage());
    }

    [Fact]
    public void CG050_UnconstructibleElementType_IsRejected()
    {
        // The element type has a writable member the schema can't persist (Uri) —
        // saving would silently drop data on round-trip, so extraction refuses to
        // resolve inline members and CG050 fires (previously: broken construction
        // code in the .g.cs).
        const string src = """
            using Disruptor.Surface.Annotations;
            using System;
            using System.Collections.Generic;
            namespace M;

            public sealed class Attachment {
                public string Name { get; set; } = "";
                public Uri? Target { get; set; }
            }

            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Property] public partial IReadOnlyList<Attachment> Attachments { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG050");
    }

    [Fact]
    public void CG050_DoesNotFire_OnSupportedElementShapes()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed record Scenario(string Kind, string Description);

            [Table] public partial class Holder {
                [Id] public partial HolderId Id { get; set; }
                [Property] public partial IReadOnlyList<Scenario> Scenarios { get; }
                [Property] public partial IReadOnlyList<string> Tags { get; }
                [Property] public partial List<int> Weights { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id is "CG050" or "CG051");
    }

    [Fact]
    public void CG051_ElementCollectionWithSetter_IsRejected()
    {
        // The emitted impl is a getter-only view over a generated backing list, so a
        // user-declared setter used to surface as a bare CS9252 in the .g.cs.
        const string src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Property] public partial IReadOnlyList<string> Tags { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        var diag = runDiags.FirstOrDefault(d => d.Id == "CG051");
        Assert.NotNull(diag);
        Assert.Contains("M.Bad", diag!.GetMessage());
        Assert.Contains("Tags", diag.GetMessage());
        Assert.Contains("AddTag", diag.GetMessage());
    }

    [Fact]
    public void CG048_DoesNotFire_OnClassDeclarations()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table] public partial class Plain {
                [Property] public partial string Name { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG048");
    }

    [Fact]
    public void CG042_CG043_CG044_CG045_DoNotFire_OnNormalShapes()
    {
        // Distinct table names, one relation kind pair, one aggregate root, everything
        // namespace-scoped — none of the uniqueness diagnostics may fire.
        const string src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            using System.Collections.Generic;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
                [RestrictedBy] public partial IReadOnlyCollection<IRecordId> Restrictions { get; }
            }

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Restricts] public partial IReadOnlyCollection<IRecordId> Restrictions { get; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id is "CG042" or "CG043" or "CG044" or "CG045");
    }

    [Fact]
    public void CG052_AuditMarkerWithoutProperty()
    {
        // [CreatedAt] without [Property] — the marker overlays a persisted scalar
        // column, so a bare marker has no field to stamp.
        const string src = """
            using System;
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [CreatedAt] public partial DateTimeOffset CreatedAtUtc { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG052");
    }

    [Fact]
    public void CG052_FiresWhenMarkerCombinedWithNonPropertyRole()
    {
        // [Version, Reference] — the marker only composes with a scalar [Property].
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Target {
                [Id] public partial TargetId Id { get; set; }
            }
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Version, Reference] public partial Target? Ref { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG052");
    }

    [Fact]
    public void CG053_AuditMarkerOnNonDateTimeProperty()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [CreatedAt, Property] public partial string CreatedLabel { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG053");
    }

    [Fact]
    public void CG053_VersionOnNonIntegerProperty()
    {
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Version, Property] public partial string Revision { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG053");
    }

    [Fact]
    public void CG053_VersionOnNullableInt()
    {
        // Nullable is rejected: the guarded UPDATE's `version + 1` arithmetic and the
        // `WHERE version = $expected` binding need a definite operand.
        const string src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [Version, Property] public partial int? Revision { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG053");
    }

    [Fact]
    public void CG054_DuplicateMarkerOnTable()
    {
        const string src = """
            using System;
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [CreatedAt, Property] public partial DateTimeOffset CreatedA { get; }
                [CreatedAt, Property] public partial DateTimeOffset CreatedB { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG054");
    }

    [Fact]
    public void CG055_MultipleMarkersOnOneProperty()
    {
        // [CreatedAt, UpdatedAt] on one field is contradictory: stamp-on-CREATE-only
        // vs stamp-every-save can't both hold for the same column.
        const string src = """
            using System;
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Bad {
                [Id] public partial BadId Id { get; set; }
                [CreatedAt, UpdatedAt, Property] public partial DateTimeOffset Stamp { get; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG055");
    }

    [Fact]
    public void CG052_CG053_CG054_CG055_DoNotFire_OnWellFormedAuditModel()
    {
        // The canonical shape: one [CreatedAt], one [UpdatedAt], one [Version], all
        // combined with [Property], DateTimeOffset / DateTime / int / long typed,
        // get-only. Long + DateTime variants covered too.
        const string src = """
            using System;
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Doc {
                [Id] public partial DocId Id { get; set; }
                [CreatedAt, Property] public partial DateTime Created { get; }
                [UpdatedAt, Property] public partial DateTimeOffset Updated { get; }
                [Version, Property] public partial long Version { get; }
            }
            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, compileDiags) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id is "CG052" or "CG053" or "CG054" or "CG055");
        Assert.DoesNotContain(runDiags, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(compileDiags);
    }

}
