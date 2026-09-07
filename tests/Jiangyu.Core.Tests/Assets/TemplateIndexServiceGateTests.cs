using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public class TemplateIndexServiceGateTests
{
    [Theory]
    [InlineData(8852, 8852, 0, false)]
    [InlineData(8852, 8851, 1, false)]
    [InlineData(8852, 8700, 152, false)]
    [InlineData(8852, 8600, 252, true)]
    [InlineData(100, 0, 100, true)]
    [InlineData(1, 0, 1, true)]
    // Nothing extracted and nothing counted as skipped is still a failure.
    [InlineData(100, 0, 0, true)]
    // An empty game index has nothing to extract and nothing to fail.
    [InlineData(0, 0, 0, false)]
    public void ValuesExtractionFailed_TripsOnNothingExtractedOrMoreThanOneInFifty(int instances, int extracted, int skipped, bool failed)
    {
        Assert.Equal(failed, TemplateIndexService.ValuesExtractionFailed(instances, extracted, skipped));
    }
}
