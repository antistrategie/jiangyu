using Jiangyu.Core.Compile;
using Xunit;

namespace Jiangyu.Core.Tests.Compile;

public sealed class AnimationClipRestorationTests
{
    // The regression: the game names FBX sub-asset clips "model|clip", the
    // AssetRipper rip in the mod's bundle carries the pipe as an underscore,
    // and the exact-name lookup left the clip hollow (Sextans' directional
    // hit reactions).
    [Fact]
    public void TryGetClip_ResolvesRippedNameOfPipeCarryingGameClip()
    {
        var payload = new byte[] { 1, 2, 3 };
        var index = new AnimationClipRestoration.GameClipIndex(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["AR_rmc_scout|AR_rmc_scout_getHitFront"] = payload,
            });

        var found = index.TryGetClip("AR_rmc_scout_AR_rmc_scout_getHitFront", out var data, out var matchedName);

        Assert.True(found);
        Assert.Same(payload, data);
        Assert.Equal("AR_rmc_scout|AR_rmc_scout_getHitFront", matchedName);
    }

    [Fact]
    public void TryGetClip_PrefersExactMatchOverAlias()
    {
        var exact = new byte[] { 1 };
        var piped = new byte[] { 2 };
        var index = new AnimationClipRestoration.GameClipIndex(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["walk_cycle"] = exact,
                ["walk|cycle"] = piped,
            });

        Assert.True(index.TryGetClip("walk_cycle", out var data, out var matchedName));
        Assert.Same(exact, data);
        Assert.Equal("walk_cycle", matchedName);
    }

    [Fact]
    public void TryGetClip_MissesUnknownName()
    {
        var index = new AnimationClipRestoration.GameClipIndex(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["idle"] = new byte[] { 1 },
            });

        Assert.False(index.TryGetClip("sprint", out _, out _));
    }

    [Fact]
    public void TryGetClip_CollidingAliasesAreMarkedAmbiguous()
    {
        var index = new AnimationClipRestoration.GameClipIndex(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["run|fast"] = new byte[] { 1 },
                ["run:fast"] = new byte[] { 2 },
            });

        Assert.True(index.TryGetClip("run_fast", out _, out _));
        Assert.Contains("run_fast", index.AmbiguousNames);
    }

    [Theory]
    [InlineData("AR_rmc_scout|AR_rmc_scout_getHitFront", "AR_rmc_scout_AR_rmc_scout_getHitFront")]
    [InlineData("a/b\\c:d*e?f\"g<h>i", "a_b_c_d_e_f_g_h_i")]
    [InlineData("plain_name", "plain_name")]
    public void SanitiseAsRippedFileName_MatchesAssetRipperBehaviour(string gameName, string rippedName)
    {
        Assert.Equal(rippedName, AnimationClipRestoration.SanitiseAsRippedFileName(gameName));
    }
}
