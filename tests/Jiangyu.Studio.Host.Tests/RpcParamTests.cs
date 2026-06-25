using System.Text.Json;
using Jiangyu.Studio.Rpc;

namespace Jiangyu.Studio.Host.Tests;

public class RpcParamTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TryGetBool_is_true_only_for_a_json_true()
        => Assert.True(RpcHelpers.TryGetBool(Params("{\"release\":true}"), "release"));

    [Fact]
    public void TryGetBool_is_false_for_false_missing_null_or_non_bool()
    {
        Assert.False(RpcHelpers.TryGetBool(Params("{\"release\":false}"), "release"));
        Assert.False(RpcHelpers.TryGetBool(Params("{}"), "release"));
        Assert.False(RpcHelpers.TryGetBool(Params("{\"release\":\"true\"}"), "release"));
        Assert.False(RpcHelpers.TryGetBool((JsonElement?)null, "release"));
    }
}
