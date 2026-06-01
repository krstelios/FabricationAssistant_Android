using Xunit;

namespace FabricationAssistant.App.Android.Tests;

// EdgeSnapService configures snapping via static mutable properties. xUnit runs
// test classes in parallel by default, which races those statics. Classes that
// touch EdgeSnapService config share this collection so they run sequentially.
[CollectionDefinition("EdgeSnapState", DisableParallelization = true)]
public sealed class EdgeSnapStateCollection
{
}
