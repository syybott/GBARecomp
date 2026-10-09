using static GBARecomp.Tests.TestCode;

namespace GBARecomp.Tests;

public class TableBaseProvenanceTests
{
    // Authored synthetic code and tables; no game ROM bytes are used.
    private static readonly ushort[] Loop =
    [
        0x4A0B, 0x4693,             // 00: load table literal; mov r11,r2
        0x2900, 0xD002,             // 04: cmp r1,#0; beq guard (0x0e)
        0x3901, 0xE7FB, 0x46C0,     // 08: subs r1,#1; b loop (0x04); padding
        0x2801, 0xD808,             // 0e: cmp r0,#1; bhi default (0x24)
        0x465A, 0x0080, 0x5813,     // 12: mov r2,r11; lsls r0,#2; ldr r3,[r2,r0]
        0x469F,                     // 18: mov pc,r3
        0x2011, 0x4770,             // 1a: case 0
        0x2022, 0x4770, 0x46C0,     // 1e: case 1; padding
        0x20FF, 0x4770,             // 24: default
        0x46C0, 0x46C0, 0x46C0, 0x46C0,
        0x0034, 0x0800,             // 30: literal: table at 0x34
        0x001A, 0x0800, 0x001E, 0x0800,
    ];

    private static ushort Move(byte destination, byte source) =>
        destination < 8 && source < 8 ? (ushort)((source << 3) | destination)
            : (ushort)(0x4600 | (destination & 7) | ((destination & 8) << 4) | (source << 3));

    private static Context Copies(byte temporary = 11, bool landingPad = false)
    {
        ushort[] code =
        [
            0x4A09, Move(temporary, 2), Move(2, temporary), 0x1C12,
            0x2801, 0xD809, 0x0080, 0x5813, 0x469F, 0x46C0,
            0x2011, 0x4770, 0x2022, 0x4770, 0x46C0, 0x46C0,
            0x20FF, 0x4770, 0x46C0, 0x46C0,
            0x002C, 0x0800, 0x0014, 0x0800, 0x0018, 0x0800,
        ];
        return CreateContext(code, [new Symbol(Base, 0x2C, "Switch")], new InputConfig
        {
            LandingPads = landingPad ? [new LandingPad { Func = "Switch", Offsets = [8] }] : [],
        });
    }

    private static Context Join(bool conflicting = false, bool bypass = false)
    {
        ushort[] code =
        [
            0x2900, 0xD003, 0x4A09, 0x4693, 0xE002, 0x46C0,
            bypass ? (ushort)0x46C0 : conflicting ? (ushort)0x4A08 : (ushort)0x4A07,
            bypass ? (ushort)0x46C0 : (ushort)0x4693,
            0x2801, 0xD807, 0x465A, 0x0080, 0x5813, 0x469F,
            0x2011, 0x4770, 0x2022, 0x4770, 0x20FF, 0x4770, 0x46C0, 0x46C0,
            0x0034, 0x0800, 0x003C, 0x0800,
            0x001C, 0x0800, 0x0020, 0x0800,
            0x001C, 0x0800, 0x0020, 0x0800,
        ];
        return CreateContext(code, [new Symbol(Base, 0x34, "Switch")]);
    }

    private static FunctionAnalysis Analyze(Context context) => FunctionAnalysis.Analyze(context, context.Functions[0]);

    private static void Rejected(FunctionAnalysis analysis, uint dispatch)
    {
        Assert.True(analysis.Flows[Base + dispatch].Kind == FlowKind.IndirectJump || analysis.Errors.Count != 0,
            "An unproven table must not be emitted without diagnostics.");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(11)]
    public void RegisterCopiesAndAddZeroRetainLiteralProvenance(byte temporary)
    {
        var analysis = Analyze(Copies(temporary));
        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x14, Base + 0x18], analysis.Flows[Base + 0x10].Targets!);
    }

    [Fact]
    public void LoopInvariantBaseIsProvenAcrossTheBackedge()
    {
        var analysis = Analyze(CreateContext(Loop, [new Symbol(Base, 0x34, "Switch")]));
        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x1A, Base + 0x1E], analysis.Flows[Base + 0x18].Targets!);
    }

    [Fact]
    public void EqualLiteralValuesFromBothPredecessorsAreAccepted()
    {
        var analysis = Analyze(Join());
        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x1C, Base + 0x20], analysis.Flows[Base + 0x1A].Targets!);
    }

    [Fact]
    public void ConflictingPredecessorDefinitionsAreRejected() => Rejected(Analyze(Join(conflicting: true)), 0x1A);

    [Fact]
    public void APredecessorBypassingInitializationIsRejected() => Rejected(Analyze(Join(bypass: true)), 0x1A);

    [Fact]
    public void ALoopWithoutAnEstablishedEntryValueIsRejected()
    {
        var context = CreateContext(Loop, [new Symbol(Base, 0x34, "Switch")]);
        context.ROM.Write(Base + 2, 0x46C0, 2);
        Rejected(Analyze(context), 0x18);
    }

    [Fact]
    public void AClobberDiscoveredInATableHandlerInvalidatesTheEarlierProof()
    {
        var context = CreateContext(Loop, [new Symbol(Base, 0x34, "Switch")]);
        context.ROM.Write(Base + 0x1A, Move(11, 0), 2);
        context.ROM.Write(Base + 0x1C, 0xE7F7, 2); // b guard (0x0e)
        Assert.Contains(Analyze(context).Errors, error => error.Contains("after decoding its handlers"));
    }

    [Theory]
    [InlineData(11, true, false)]
    [InlineData(1, false, false)]
    [InlineData(12, false, false)]
    [InlineData(11, true, true)]
    [InlineData(1, false, true)]
    [InlineData(12, false, true)]
    public void CallsPreserveOnlyABIPreservedBaseRegisters(byte temporary, bool accepted, bool indirect)
    {
        ushort[] code =
        [
            0xB500, 0x4A09, Move(temporary, 2), 0xF000, 0xF815,
            Move(2, temporary), 0x2801, 0xD807, 0x0080, 0x5813, 0x469F,
            0x2011, 0xBD00, 0x2022, 0xBD00, 0x46C0, 0x20FF, 0xBD00, 0x46C0, 0x46C0,
            0x002C, 0x0800, 0x0016, 0x0800, 0x001A, 0x0800, 0x4770,
        ];
        if (indirect) { code[3] = 0x46FE; code[4] = 0x4718; } // mov lr,pc; bx r3
        var analysis = Analyze(CreateContext(code,
            [new Symbol(Base, 0x2C, "Switch"), new Symbol(Base + 0x34, 2, "Callee")]));
        Assert.Empty(analysis.Errors);
        Assert.Equal(accepted ? FlowKind.JumpTable : FlowKind.IndirectJump, analysis.Flows[Base + 0x14].Kind);
    }

    [Fact]
    public void ANonlocalLandingPadCannotInheritTheOrdinaryEntryBase() =>
        Rejected(Analyze(Copies(landingPad: true)), 0x10);

    [Fact]
    public void CopiedBaseStillRequiresAnUnsignedRangeProof()
    {
        var context = Copies();
        context.ROM.Write(Base + 8, 0x2001, 2);
        Assert.Contains(Analyze(context).Errors, error => error.Contains("no range check"));
    }

    [Theory]
    [InlineData(0x02000000u)]
    [InlineData(Base + 0x30)]
    [InlineData(Base + 0x2E)]
    public void CopiedBaseRetainsROMRangeAndAlignmentChecks(uint table)
    {
        var context = Copies();
        context.ROM.Write(Base + 0x28, table, 4);
        Assert.NotEmpty(Analyze(context).Errors);
    }

    [Fact]
    public void CopiedBaseRetainsLocalTargetChecks()
    {
        var context = Copies();
        context.ROM.Write(Base + 0x2C, Base + 0x2C, 4);
        Assert.Contains(Analyze(context).Errors, error => error.Contains("outside the function"));
    }

    [Fact]
    public void ExhaustingTheProvenanceBudgetLeavesTheDispatchUnresolved()
    {
        var code = new List<ushort> { 0x4A01, 0x4693, 0xE002, 0x46C0, 0, 0 };
        code.AddRange(Enumerable.Repeat((ushort)0x46C0, 4100));
        uint guard = (uint)code.Count * 2;
        code.AddRange([0x2801, 0xD807, 0x465A, 0x0080, 0x5813, 0x469F,
            0x2011, 0x4770, 0x2022, 0x4770, 0x46C0, 0x46C0, 0x20FF, 0x4770]);
        uint table = Base + (uint)code.Count * 2;
        code[4] = (ushort)table; code[5] = (ushort)(table >> 16);
        code.AddRange([(ushort)(Base + guard + 12), (ushort)(Base >> 16),
            (ushort)(Base + guard + 16), (ushort)(Base >> 16)]);
        Rejected(Analyze(CreateContext([.. code], [new Symbol(Base, table - Base, "Switch")])), guard + 10);
    }
}
