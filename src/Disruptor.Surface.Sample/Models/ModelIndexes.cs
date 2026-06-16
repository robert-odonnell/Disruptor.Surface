using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Models;

public sealed class RepositoryRootAttribute : UniqueIndexAttribute;

public sealed class ByFeaturePersonaAttribute : IndexAttribute;

public sealed class ByReviewSeverityAttribute : IndexAttribute;

public sealed class ByReviewKindAttribute : IndexAttribute;
