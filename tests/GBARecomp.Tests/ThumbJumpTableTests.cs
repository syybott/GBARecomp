using static GBARecomp.Tests.TestCode;

namespace GBARecomp.Tests;

public class ThumbJumpTableTests
{
    private static Context IndexedTable(ushort load = 0x58D3, bool inline = false, uint address = Base)
    {
        uint table = inline ? address + 0x1C : Base + 0x1C;
        // Authored synthetic Thumb switch with a separate ROM word table.
        ushort[] code =
        [
            0x2B01,                         // 00: cmp r3, #1
            0xD807,                         // 02: bhi 0x14
            0x4A04,                         // 04: ldr r2, [pc, #16]
            0x009B,                         // 06: lsls r3, r3, #2
            load,                           // 08: ldr r3, [r2, r3] (or [r3, r2])
            0x469F,                         // 0a: mov pc, r3
            0x2011, 0x4770,                 // 0c: movs r0, #0x11; bx lr
            0x2022, 0x4770,                 // 10: movs r0, #0x22; bx lr
            0x20FF, 0x4770,                 // 14: default: movs r0, #0xff; bx lr
            (ushort)table, (ushort)(table >> 16),
            (ushort)(address + 0x0C), (ushort)(address >> 16),
            (ushort)(address + 0x10), (ushort)(address >> 16),
        ];
        uint size = inline ? 0x24u : 0x1Cu;
        var input = new InputConfig();
        Symbol[] symbols = [new Symbol(address, size, "Switch")];
        if (address != Base)
        {
            symbols = [];
            input.ManualFuncs = [new ManualFunction { Name = "Switch", Address = address, Size = size, ROMAddress = Base }];
        }
        return CreateContext(code, symbols, input);
    }

    private static FunctionAnalysis Analyze(Context context) => FunctionAnalysis.Analyze(context, context.Functions[0]);

    [Theory]
    [InlineData(0x58D3)]
    [InlineData(0x589B)]
    public void RegisterIndexedLoadFindsEveryExternalROMTableCase(ushort load)
    {
        var analysis = Analyze(IndexedTable(load));

        Assert.Empty(analysis.Errors);
        Assert.Equal(FlowKind.JumpTable, analysis.Flows[Base + 0x0A].Kind);
        Assert.Equal([Base + 0x0C, Base + 0x10], analysis.Flows[Base + 0x0A].Targets!);
        Assert.Equal([Base + 0x0C, Base + 0x10, Base + 0x14], analysis.Labels);
        Assert.Contains(analysis.Instructions, instruction => instruction.Address == Base + 0x12);
        Assert.DoesNotContain(analysis.Instructions, instruction => instruction.Address >= Base + 0x18);
    }

    [Theory]
    [InlineData(Base)]
    [InlineData(0x03000000u)]
    public void InlineTableUsesTheFunctionsROMBacking(uint address)
    {
        var analysis = Analyze(IndexedTable(inline: true, address: address));

        Assert.Empty(analysis.Errors);
        Assert.Equal([address + 0x0C, address + 0x10], analysis.Flows[address + 0x0A].Targets!);
    }

    [Fact]
    public void ExternalEntriesNormalizeThumbBitsAndRemoveDuplicates()
    {
        var context = IndexedTable();
        context.ROM.Write(Base + 0x1C, Base + 0x0D, 4);
        context.ROM.Write(Base + 0x20, Base + 0x0C, 4);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x0C], analysis.Flows[Base + 0x0A].Targets!);
    }

    [Fact]
    public void IndexedTableStillRequiresARangeCheck()
    {
        var context = IndexedTable();
        context.ROM.Write(Base, 0x2301, 2); // movs r3, #1 instead of cmp

        Assert.Contains("no range check", Assert.Single(Analyze(context).Errors));
    }

    [Theory]
    [InlineData(Base + 0x20)] // only one word remains in ROM
    [InlineData(Base + 0x24)] // end of ROM
    [InlineData(0x02000000u)] // external RAM table is not static ROM data
    [InlineData(0xFFFFFFFCu)] // range would wrap in 32-bit arithmetic
    [InlineData(Base + 0x1E)] // unaligned table
    public void InvalidExternalTableRangesAreErrors(uint table)
    {
        var context = IndexedTable();
        context.ROM.Write(Base + 0x18, table, 4);

        Assert.Single(Analyze(context).Errors);
    }

    [Fact]
    public void TheEntireBoundedTableMustFitInROM()
    {
        var context = IndexedTable();
        context.ROM.Write(Base, 0x2BFF, 2); // cmp r3, #255

        Assert.Contains("full range is not in ROM", Assert.Single(Analyze(context).Errors));
    }

    [Theory]
    [InlineData(Base - 2)]
    [InlineData(Base + 0x1C)]
    public void ExternalTableTargetsMustRemainInsideTheFunction(uint target)
    {
        var context = IndexedTable();
        context.ROM.Write(Base + 0x1C, target, 4);

        Assert.Contains("entry", Assert.Single(Analyze(context).Errors));
    }
}
