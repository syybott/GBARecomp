using static GBARecomp.Tests.TestCode;

namespace GBARecomp.Tests;

public class LocalBranchTests
{
    [Fact]
    public void PassingLRAsDataBeforeACallDoesNotMakeALocalBlockAFunction()
    {
        var context = CreateContext(
            [
                0xB500,         // 00: push {lr}
                0xF000, 0xF803, // 02: bl 0x0c
                0x2022,         // 06: continuation: movs r0, #0x22
                0xBD00,         // 08: pop {pc}
                0xFFFF,         // 0a: padding
                0x4670,         // 0c: mov r0, lr (diagnostic argument)
                0xF000, 0xF801, // 0e: bl 0x14
                0xE7F8,         // 12: b 0x06
                0x2077,         // 14: Consume: movs r0, #0x77
                0x4770,         // 16: bx lr
            ],
            [new Symbol(Base, 0x14, "Caller"), new Symbol(Base + 0x14, 4, "Consume")]);

        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Empty(analysis.Errors);
        Assert.Equal(new Flow(FlowKind.Branch, Base + 0x0C), analysis.Flows[Base + 2]);
        Assert.Equal(new Flow(FlowKind.Call, Base + 0x14), analysis.Flows[Base + 0x0E]);
        Assert.Empty(analysis.UnknownTargets);
    }

    [Fact]
    public void DiagnosticDataCanSurviveACallAndAnotherLocalLongBranch()
    {
        var context = CreateContext(
            [
                0xB500, 0xF000, 0xF803, // 00: push {lr}; bl 0x0c
                0x2022, 0xBD00, 0xFFFF, // 06: continuation; padding
                0x4670,                // 0c: mov r0, lr
                0xF000, 0xF805,         // 0e: bl Consume at 0x1c (preserves r0)
                0xF000, 0xF801,         // 12: bl local continuation at 0x18
                0xFFFF,                // 16: padding, not executable
                0x0002, 0xE7F4,         // 18: movs r2, r0; b 0x06
                0x4770,                // 1c: Consume: bx lr
            ],
            [new Symbol(Base, 0x1C, "Caller"), new Symbol(Base + 0x1C, 2, "Consume")]);

        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Empty(analysis.Errors);
        Assert.Equal(new Flow(FlowKind.Branch, Base + 0x0C), analysis.Flows[Base + 2]);
        Assert.Equal(new Flow(FlowKind.Branch, Base + 0x18), analysis.Flows[Base + 0x12]);
        Assert.Empty(analysis.UnknownTargets);
        Assert.DoesNotContain(analysis.Instructions, instruction => instruction.Address == Base + 0x16);
    }

    [Theory]
    [InlineData(0x4674, 0x4720)] // mov r4, lr; bx r4
    [InlineData(0x4670, 0x4700)] // mov r0, lr; bx r0 (Helper preserves r0)
    public void ACopiedReturnAddressMaySurviveAnotherCall(ushort copy, ushort branch)
    {
        var context = CreateContext(
            [
                0xB500,         // 00: push {lr}
                0xF000, 0xF801, // 02: bl 0x08
                0xBD00,         // 06: pop {pc}
                copy,           // 08: save the return address in a register
                0xF000, 0xF801, // 0a: bl 0x10
                branch,         // 0e: return through that register
                0x4770,         // 10: Helper: bx lr
            ],
            [new Symbol(Base, 0x10, "Caller"), new Symbol(Base + 0x10, 2, "Helper")]);

        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Equal(new Flow(FlowKind.Call, Base + 8), analysis.Flows[Base + 2]);
        Assert.Equal([(Base + 8, true)], analysis.UnknownTargets);
    }

    [Theory]
    [InlineData(0xB500, 0xBD00)] // push {lr}; pop {pc}
    [InlineData(0x4670, 0x4770)] // mov r0, lr; bx lr
    public void SavingOrReturningThroughLRStillMakesACall(ushort first, ushort second)
    {
        var context = CreateContext(
            [0xB500, 0xF000, 0xF801, 0xBD00, first, second],
            [new Symbol(Base, 0x0C, "Caller")]);

        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Equal(new Flow(FlowKind.Call, Base + 8), analysis.Flows[Base + 2]);
    }
}
