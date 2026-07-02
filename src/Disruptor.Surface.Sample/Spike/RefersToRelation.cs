using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Spike;

[RefersTo]
public partial class RefersToRelation : ICodeSymbolEdge
{
    [In]       public partial CodeSymbolId Source { get; set; }
    [Out]      public partial CodeSymbolId Target { get; set; }
    [Property] public partial string Confidence { get; set; }
}
