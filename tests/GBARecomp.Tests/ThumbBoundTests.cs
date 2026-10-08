using static GBARecomp.Tests.TestCode;

namespace GBARecomp.Tests;

public class ThumbBoundTests
{
    // Authored code with a guarded, indexed Thumb table outside the function.
    private static FunctionAnalysis Analyze(ushort[] prefix, ushort compare = 0x2B01,
        ushort branch = 0xD900, bool taken = true, bool thirdEntry = false)
    {
        Assert.Equal(0, prefix.Length % 2);
        uint offset = (uint)prefix.Length * 2;
        ushort[] code =
        [
            .. prefix,
            compare,                        // +00: cmp index, bound
            branch,                         // +02: accepted edge, or default
            taken ? (ushort)0xE007 : (ushort)0x46C0,
            0x4A05,                         // +06: ldr r2, table literal
            0x009B, 0x58D3, 0x469F,         // +08: scale r3; load entry; mov pc
            0x2011, 0x4770,                 // +0e: case 0
            0x2022, 0x4770,                 // +12: case 1
            0x20FF, 0x4770,                 // +16: default
            0xFFFF,                         // +1a: padding
            (ushort)(offset + 0x20), 0x0800, // +1c: table literal
            (ushort)(offset + 0x0E), 0x0800,
            (ushort)(offset + 0x12), 0x0800,
            .. thirdEntry ? new ushort[] { (ushort)(offset + 0x16), 0x0800 } : [],
        ];
        var context = CreateContext(code, [new Symbol(Base, offset + 0x20, "Switch")]);
        return FunctionAnalysis.Analyze(context, context.Functions[0]);
    }

    [Theory]
    [InlineData(0xD300, true)]  // bcc accepted
    [InlineData(0xD208, false)] // bcs default
    public void ExclusiveUnsignedChecksUseTheBoundAsTheEntryCount(ushort branch, bool taken)
    {
        var analysis = Analyze([], compare: 0x2B02, branch: branch, taken: taken);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x0E, Base + 0x12], analysis.Flows[Base + 0x0C].Targets!);
    }

    [Fact]
    public void AnExclusiveZeroBoundDoesNotAdmitATable()
    {
        Assert.Contains("no range check", Assert.Single(Analyze([], compare: 0x2B00, branch: 0xD300).Errors));
    }

    [Fact]
    public void AnUnmodifiedCopyOfTheIndexCanBeCompared()
    {
        var analysis = Analyze([0x0019, 0x46C0], compare: 0x2901); // movs r1, r3; nop

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Flows[Base + 0x10].Targets!.Length);
    }

    [Theory]
    [InlineData(0x7803)] // ldrb r3, [r0]
    [InlineData(0x8803)] // ldrh r3, [r0]
    [InlineData(0x5643)] // ldrsb r3, [r0, r1]
    [InlineData(0x5E43)] // ldrsh r3, [r0, r1]
    public void AProvenTypedIndexCanBeCheckedThroughANarrowedCopy(ushort load)
    {
        var analysis = Analyze([load, 0x0419, 0x0C09, 0x46C0], compare: 0x2901);

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Flows[Base + 0x14].Targets!.Length);
    }

    [Fact]
    public void AnUnknownFullWidthIndexCannotUseANarrowedCopyAsItsBound()
    {
        var analysis = Analyze([0x0003, 0x0419, 0x0C09, 0x46C0], compare: 0x2901);

        Assert.Contains("no range check", Assert.Single(analysis.Errors));
    }

    [Fact]
    public void APathThatBypassesTheTypedLoadCannotProveTheOriginalIndexRange()
    {
        var analysis = Analyze(
            [
                0x2800, 0xD001, // cmp r0, #0; beq 0x08 (r3 remains unknown)
                0x5E43, 0x46C0, // ldrsh r3, [r0, r1]; nop
                0x0419, 0x0C09, // r1 = (ushort)r3
                0x46C0, 0x46C0,
            ], compare: 0x2901);

        Assert.Contains("no range check", Assert.Single(analysis.Errors));
    }

    [Fact]
    public void DifferentTypedLoadsOnEveryIncomingPathCanStillProveTheRange()
    {
        var analysis = Analyze(
            [
                0x2800, 0xD002, // cmp r0, #0; beq 0x0a
                0x8803, 0xE002, // ldrh r3, [r0]; b 0x0e
                0xFFFF,         // padding
                0x5E43, 0x46C0, // 0a: ldrsh r3, [r0, r1]; nop
                0x46C0,         // 0e: nop
                0x0419, 0x0C09, // r1 = (ushort)r3
                0x46C0, 0x46C0,
            ], compare: 0x2901);

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Flows[Base + 0x24].Targets!.Length);
    }

    [Fact]
    public void AConstantBoundCanBeTracedPastFourInstructions()
    {
        var analysis = Analyze([0x2401, 0x46C0, 0x46C0, 0x46C0, 0x46C0, 0x46C0], compare: 0x42A3);

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Flows[Base + 0x18].Targets!.Length);
    }

    [Fact]
    public void AClobberedConstantCannotBeUsedAsTheBound()
    {
        var analysis = Analyze([0x2101, 0xDF01], compare: 0x428B);

        Assert.Contains("no range check", Assert.Single(analysis.Errors));
    }

    [Fact]
    public void CheckingAndIndexingTheSameNarrowedValueDoesNotNeedAHighBitProof()
    {
        var analysis = Analyze([0x041B, 0x0C1B]); // r3 = (ushort)r3

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Flows[Base + 0x10].Targets!.Length);
    }

    [Fact]
    public void ANarrowedSubtractionBoundsTheCorrespondingOriginalIndex()
    {
        // r1 = (ushort)(r3 - 1), where r3 was loaded unsigned 16-bit.
        var analysis = Analyze([0x8803, 0x0019, 0x3901, 0x0409, 0x0C09, 0x46C0],
            compare: 0x2901, thirdEntry: true);

        Assert.Empty(analysis.Errors);
        Assert.Equal(3, analysis.Flows[Base + 0x18].Targets!.Length);
    }

    [Fact]
    public void AnIndexChangedAfterTheCopyIsNotProvenByTheOldCopy()
    {
        var analysis = Analyze([0x0019, 0x2300], compare: 0x2901);

        Assert.Contains("no range check", Assert.Single(analysis.Errors));
    }

    [Fact]
    public void ARangeProofCanBeRetriedAfterAnotherPredecessorIsDecoded()
    {
        var analysis = Analyze(
            [
                0x5E43, 0x2800, // ldrsh r3, [r0, r1]; cmp r0, #0
                0xD002,         // beq 0x0c (decoded after the switch)
                0xE003,         // b 0x10
                0xFFFF, 0xFFFF, // padding
                0x46C0, 0x46C0, // 0c: nop; nop
                0x0419, 0x0C09, // 10: r1 = (ushort)r3
                0x46C0, 0x46C0,
            ], compare: 0x2901);

        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Flows[Base + 0x24].Targets!.Length);
    }
}
