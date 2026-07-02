using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Relations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Models;

[Table]
[AggregateRoot]
public partial class Design
{
    [Id] public partial DesignId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [RepositoryRoot, Property] public partial string RepositoryRoot { get; set; }
    [Property] public partial string Description { get; set; }

    // Library-managed columns: audit stamps + optimistic-concurrency counter. Get-only —
    // the emitted SaveAsync writes the backing fields itself.
    [CreatedAt, Property] public partial DateTimeOffset CreatedAtUtc { get; }
    [UpdatedAt, Property] public partial DateTimeOffset UpdatedAtUtc { get; }
    [Version, Property] public partial int Version { get; }

    [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
    [Children] public partial IReadOnlyCollection<Epic> Epics { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }

    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
    [AssessedBy] public partial IReadOnlyCollection<IRecordId> Assessments { get; }
}
