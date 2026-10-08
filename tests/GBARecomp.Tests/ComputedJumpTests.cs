using GBARecomp.ARM;
using static GBARecomp.Tests.TestCode;

namespace GBARecomp.Tests;

public class ComputedJumpTests
{
    private static readonly ushort[] DispatchCode =
    [
        0xF100, 0xE08F, // 00: add pc, pc, r0, lsl #2
        0xFFFF, 0xFFFF, // 04: unreachable padding
        0x0011, 0xE3A0, // 08: mov r0, #0x11
        0xFF1E, 0xE12F, // 0c: bx lr
        0x0022, 0xE3A0, // 10: mov r0, #0x22
        0xFF1E, 0xE12F, // 14: bx lr
    ];

    private static Context Dispatch(InputConfig input) =>
        CreateContext(DispatchCode, [new Symbol(Base, 24, "Dispatch", IsThumb: false)], input);

    private static ComputedJump Jump(params uint[] targets) =>
        new() { Func = "Dispatch", TargetOffsets = [.. targets] };

    private static Context ThumbDispatch(InputConfig input) => CreateContext(
        [0x46C0, 0x469F, 0x2011, 0x4770, 0x2022, 0x4770],
        [new Symbol(Base, 12, "Dispatch", IsThumb: true)], input);

    private static ComputedJump ThumbJump(uint offset = 2, params uint[] targets) =>
        new() { Func = "Dispatch", Offset = offset, TargetOffsets = [.. targets] };

    [Fact]
    public void ThumbFiniteDispatchAcceptsHalfwordAlignmentAndDeduplicatesCases()
    {
        var context = ThumbDispatch(new InputConfig { ComputedJumps = [ThumbJump(2, 4, 8, 8)] });
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 4, Base + 8], analysis.Flows[Base + 2].Targets!);
        Assert.False(analysis.Flows[Base + 2].PCRelativeTargets);
        Assert.Equal(6, analysis.Instructions.Count);
    }

    [Fact]
    public void AThumbTargetCanBeTheFinalHalfwordOfTheFunction()
    {
        var context = ThumbDispatch(new InputConfig { ComputedJumps = [ThumbJump(2, 10)] });
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 10], analysis.Flows[Base + 2].Targets!);
    }

    [Theory]
    [InlineData(1u, 4u)]
    [InlineData(12u, 4u)]
    [InlineData(uint.MaxValue, 4u)]
    [InlineData(2u, 1u)]
    [InlineData(2u, 12u)]
    [InlineData(2u, uint.MaxValue)]
    public void InvalidThumbInstructionOrTargetOffsetsAreRejected(uint offset, uint target)
    {
        Assert.Throws<InvalidDataException>(() => ThumbDispatch(new InputConfig { ComputedJumps = [ThumbJump(offset, target)] }));
    }

    [Fact]
    public void AThumbDeclarationMustDescribeARegisterMoveToPC()
    {
        var context = ThumbDispatch(new InputConfig { ComputedJumps = [ThumbJump(0, 4)] });
        Assert.Contains(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors, error => error.Contains("not an ARM ADD"));

        context = ThumbDispatch(new InputConfig { ComputedJumps = [ThumbJump(2, 4)] });
        context.ROM.Write(Base + 2, 0x46FF, 2); // mov pc, pc is not a finite register dispatch
        Assert.Contains(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors, error => error.Contains("not an ARM ADD"));
    }

    [Fact]
    public void CopiedThumbDispatchDoesNotRelocateAbsoluteRegisterTargets()
    {
        var context = ThumbDispatch(new InputConfig { ComputedJumps = [ThumbJump(2, 4, 8)], RAMFuncs = ["Dispatch"] });
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        string method = new CSharpGenerator(context).Generate(analysis);

        Assert.Empty(analysis.Errors);
        Assert.Contains("switch (target & ~1u)", method);
        Assert.DoesNotContain("switch ((target & ~1u) - Recomp.CopyOffset)", method);
        Assert.Contains("default: throw Recomp.SwitchError", method);
    }

    [Fact]
    public void FiniteDispatchVisitsBothCasesWithoutDecodingPadding()
    {
        var context = Dispatch(new InputConfig { ComputedJumps = [Jump(8, 16)] });
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 8, Base + 16], analysis.Flows[Base].Targets!);
        Assert.Equal(5, analysis.Instructions.Count);
        Assert.DoesNotContain(analysis.Instructions, instruction => instruction.Address == Base + 4);
    }

    [Fact]
    public void CopiedDispatchNormalizesItsPCBeforeSelectingALocalCase()
    {
        var context = Dispatch(new InputConfig { ComputedJumps = [Jump(8, 16)], RAMFuncs = ["Dispatch"] });
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        string method = new CSharpGenerator(context).Generate(analysis);
        Assert.Contains("switch ((target & ~1u) - Recomp.CopyOffset)", method);
        Assert.Contains("case 0x08000008u: goto L_08000008;", method);
        Assert.Contains("default: throw Recomp.SwitchError", method);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(24u)]
    public void MisalignedOrOutsideTargetsAreRejected(uint target)
    {
        Assert.Throws<InvalidDataException>(() => Dispatch(new InputConfig { ComputedJumps = [Jump(target)] }));
    }

    [Fact]
    public void EmptyAndDuplicateDeclarationsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Dispatch(new InputConfig { ComputedJumps = [Jump()] }));
        Assert.Throws<InvalidDataException>(() => Dispatch(new InputConfig { ComputedJumps = [Jump(8), Jump(16)] }));
    }

    [Fact]
    public void MetadataCannotTurnAnUnrelatedInstructionIntoADispatch()
    {
        var jump = Jump(8);
        jump.Offset = 8;
        var context = Dispatch(new InputConfig { ComputedJumps = [Jump(8, 16), jump] });
        Assert.Contains(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors,
            error => error.Contains("not an ARM ADD"));
    }

    [Theory]
    [InlineData(0xDE00)]
    [InlineData(0xDEFF)]
    [InlineData(0xEFFF)]
    public void IntentionalTrapsStopAnalysisAndEmitAnExplicitNativeFailure(ushort encoding)
    {
        var context = CreateContext([encoding, 0xFFFF], [new Symbol(Base, 4, "Stop")]);
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);
        Assert.Single(analysis.Instructions);
        Assert.Equal(Opcode.Trap, analysis.Instructions.Single().Opcode);
        Assert.Contains("throw new System.InvalidOperationException", new CSharpGenerator(context).Generate(analysis));
    }

    [Fact]
    public void AssertionWarningPathCanStillReturn()
    {
        var context = CreateContext([0x2800, 0xD000, 0xEFFF, 0x4770, 0xFFFF], [new Symbol(Base, 10, "Assert")]);
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);
        Assert.Contains(analysis.Instructions, instruction => instruction.Address == Base + 6 && instruction.Opcode == Opcode.Bx);
    }
}
