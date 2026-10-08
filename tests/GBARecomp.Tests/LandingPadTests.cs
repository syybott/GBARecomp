using static GBARecomp.Tests.TestCode;

namespace GBARecomp.Tests;

public class LandingPadTests
{
    // Authored code: ordinary execution returns before the saved continuation.
    private static readonly ushort[] Code = [0x4802, 0x4770, 0x2011, 0x4770, 0x46C0, 0x46C0, 0x0004, 0x0800];

    private static InputConfig Pads(params uint[] offsets) => new()
    {
        LandingPads = [new LandingPad { Func = "Test", Offsets = [.. offsets] }],
    };

    private static Context Create(InputConfig input, PatchesConfig? patches = null) =>
        CreateContext(Code, [new Symbol(Base, (uint)Code.Length * 2, "Test")], input, patches);

    [Fact]
    public void ExplicitContinuationDecodesAnOtherwiseUnreachablePath()
    {
        var ordinaryContext = Create(new InputConfig());
        var ordinary = FunctionAnalysis.Analyze(ordinaryContext, ordinaryContext.Functions[0]);
        Assert.DoesNotContain(ordinary.Instructions, instruction => instruction.Address == Base + 4);
        Assert.Empty(ordinary.LandingPads);

        var context = Create(Pads(4));
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);
        Assert.Contains(analysis.Instructions, instruction => instruction.Address == Base + 4);
        Assert.Equal([Base + 4], analysis.LandingPads);
        Assert.Single(context.Functions);
        Assert.Contains("public static RecompFunc Test = Original.Test;", new CSharpGenerator(context).GenerateTables());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(16u)]
    [InlineData(uint.MaxValue)]
    public void EntryMisalignedOutsideAndOverflowingOffsetsAreRejected(uint offset) =>
        Assert.Throws<InvalidDataException>(() => Create(Pads(offset)));

    [Fact]
    public void EmptyDuplicateOffsetsAndDuplicateFunctionDeclarationsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Create(Pads()));
        Assert.Throws<InvalidDataException>(() => Create(Pads(4, 4)));
        var input = Pads(4);
        input.LandingPads.Add(new LandingPad { Func = "Test", Offsets = [6] });
        Assert.Throws<InvalidDataException>(() => Create(input));
    }

    [Fact]
    public void UnknownAmbiguousAndAliasFunctionNamesAreRejected()
    {
        var input = Pads(4);
        input.LandingPads[0].Func = "Missing";
        Assert.Throws<InvalidDataException>(() => Create(input));
        Assert.Throws<InvalidDataException>(() => CreateContext(Code,
            [new Symbol(Base, 8, "Test"), new Symbol(Base + 8, 8, "Test")], Pads(4)));
        input.LandingPads[0].Func = "Alias";
        Assert.Throws<InvalidDataException>(() => CreateContext(Code,
            [new Symbol(Base, 16, "Test"), new Symbol(Base, 16, "Alias")], input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StubAndIgnoredFunctionsCannotHaveLandingPads(bool ignored) =>
        Assert.Throws<InvalidDataException>(() => Create(Pads(4), ignored
            ? new PatchesConfig { Ignored = ["Test"] } : new PatchesConfig { Stubs = ["Test"] }));

    [Theory]
    [InlineData(12u)]
    [InlineData(14u)]
    public void AContinuationCannotDecodeLoadedLiteralData(uint offset)
    {
        var context = Create(Pads(offset));
        Assert.Contains(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors,
            error => error.Contains("was also decoded as an instruction"));
    }

    [Fact]
    public void AContinuationCannotEnterTheSecondHalfOfAThumbLongBranch()
    {
        var context = CreateContext([0xF000, 0xF802, 0x4770, 0x46C0, 0x4770],
            [new Symbol(Base, 8, "Test"), new Symbol(Base + 8, 2, "Callee")], Pads(2));
        Assert.Contains(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors,
            error => error.Contains("not at a decoded instruction boundary"));
    }

    [Fact]
    public void ARMContinuationUsesWordAlignment()
    {
        ushort[] code = [0xFF1E, 0xE12F, 0x0011, 0xE3A0, 0xFF1E, 0xE12F];
        Symbol[] symbols = [new Symbol(Base, 12, "Test", IsThumb: false)];
        Assert.Throws<InvalidDataException>(() => CreateContext(code, symbols, Pads(2)));
        var context = CreateContext(code, symbols, Pads(4));
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 4], analysis.LandingPads);
    }

    [Fact]
    public void CopiedFunctionResumeCheckUsesTheExistingCopyOffset()
    {
        var input = Pads(8);
        input.RAMFuncs = ["Test"];
        var context = CreateContext([0xF000, 0xF804, 0x4770, 0x46C0, 0x4770, 0x46C0, 0x4770],
            [new Symbol(Base, 12, "Test"), new Symbol(Base + 12, 2, "Callee")], input);
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);
        Assert.Contains("Recomp.TryResumeAt((0x08000008u + Recomp.CopyOffset))", new CSharpGenerator(context).Generate(analysis));
    }
}
