using Jiangyu.Loader.Sdk.Patches;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the byte-pattern test that decides whether a patch target's native code is
/// small enough for the game's compiler to have folded it with other methods. The
/// vectors are the first block of real MENACE v0.7.15 functions: a field getter whose
/// code five unrelated methods share, a 16-byte forward that is unique, and an ordinary
/// body.
/// </summary>
public class NativeTargetGuardTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void FieldGetterEndingInRetIsTiny()
    {
        // BlackMarketUIScreen.GetSellItemList: mov rax, [rcx+0xA8]; ret
        Assert.True(NativeTargetGuard.IsTinyBody(Hex("48 8b 81 a8 00 00 00 c3 cc cc cc cc cc cc cc cc")));
    }

    [Fact]
    public void ShortGetterWithLongPaddingIsTiny()
    {
        // Roster.GetHirableLeaders: mov rax, [rcx+0x18]; ret
        Assert.True(NativeTargetGuard.IsTinyBody(Hex("48 8b 41 18 c3 cc cc cc cc cc cc cc cc cc cc cc")));
    }

    [Fact]
    public void ForwardEndingInJmpRel32IsTiny()
    {
        // UnitWindow.Refresh: mov rdx, [rcx+0x4B8]; xor r8d, r8d; jmp rel32
        Assert.True(NativeTargetGuard.IsTinyBody(Hex("48 8b 91 b8 04 00 00 45 33 c0 e9 01 00 00 00 cc")));
    }

    [Fact]
    public void ForwardEndingInJmpRel8IsTiny()
    {
        Assert.True(NativeTargetGuard.IsTinyBody(Hex("33 c0 eb 10 cc cc cc cc cc cc cc cc cc cc cc cc")));
    }

    [Fact]
    public void BodyWithNoTerminatorInTheBlockIsNotTiny()
    {
        // AddItemEffect.OnAdd: push rbx; sub rsp, 0x50; cmp byte [rip+...], 0; mov rbx, rcx
        Assert.False(NativeTargetGuard.IsTinyBody(Hex("40 53 48 83 ec 50 80 3d b0 ce 4a 03 00 48 8b d9")));
    }

    [Fact]
    public void TerminatorFollowedByCodeIsNotTiny()
    {
        // A ret byte inside the block that more code follows (an early return).
        Assert.False(NativeTargetGuard.IsTinyBody(Hex("48 85 c9 75 01 c3 48 8b 41 18 48 85 c0 74 05 90")));
    }

    [Fact]
    public void TerminatorEndingOnTheBoundaryIsNotTiny()
    {
        Assert.False(NativeTargetGuard.IsTinyBody(Hex("48 8b 81 a8 00 00 00 48 8b 80 10 00 00 00 90 c3")));
    }

    [Fact]
    public void UnalignedStartReadsOnlyToTheBoundary()
    {
        // A function starting 8 bytes into a block: mov eax, 1; ret; padding.
        Assert.True(NativeTargetGuard.IsTinyBody(Hex("b8 01 00 00 00 c3 cc cc")));
    }

    [Fact]
    public void FunctionAlreadyDetouredNeedsTheSharingCheck()
    {
        // An absolute jump written over the start by another patcher hides the body size.
        Assert.True(NativeTargetGuard.NeedsSharingCheck(Hex("ff 25 00 00 00 00 10 20 30 40 50 60 70 80 cc cc")));
        Assert.True(NativeTargetGuard.NeedsSharingCheck(Hex("e9 10 20 30 40 00 00 c3 cc cc cc cc cc cc cc cc")));
    }

    [Fact]
    public void OrdinaryBodyNeedsNoSharingCheck()
    {
        Assert.False(NativeTargetGuard.NeedsSharingCheck(Hex("40 53 48 83 ec 50 80 3d b0 ce 4a 03 00 48 8b d9")));
    }
}
