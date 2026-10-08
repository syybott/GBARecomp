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
