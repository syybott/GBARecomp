using GBARecomp.ARM;
using static GBARecomp.ARM.Registers;

namespace GBARecomp;

internal enum FlowKind
{
    Next,
    Branch,
    TailCall,
    Call,
    NoreturnCall,
    JumpTable,
    BranchExchange,
    IndirectCall,
    IndirectJump,
    Trap,
}

internal readonly record struct Flow(FlowKind Kind, uint Target = 0, uint[]? Targets = null, bool PCRelativeTargets = false)
{
    public IEnumerable<uint> LocalTargets => Kind switch
    {
        FlowKind.Branch or FlowKind.IndirectCall => [Target],
        FlowKind.BranchExchange => [Target & ~1u],
        FlowKind.JumpTable => Targets!,
        _ => [],
    };

    public bool FallsThrough(Instruction instruction)
    {
        return instruction.Condition != Condition.AL || Kind is FlowKind.Next or FlowKind.Call;
    }
}

internal sealed class FunctionAnalysis
{
    private const uint SoftReset = 0x00;
    private const uint HardReset = 0x26;
    private const ushort PreservedAcrossCalls = 0x0FF0;
    private const int JumpTablePathLimit = 64;

    private readonly Context _context;
    private readonly SortedDictionary<uint, Instruction> _instructions = [];
    private readonly SortedSet<uint> _labels = [];
    private readonly SortedSet<uint> _landingPads = [];
    private readonly Dictionary<uint, Flow> _flows = [];
    private readonly List<string> _errors = [];
    private readonly List<(uint Target, bool IsThumb)> _unknownTargets = [];
    private readonly Stack<(uint Address, bool IsThumb)> _pending = [];
    private readonly HashSet<Instruction> _unboundedJumpTables = [];
    private readonly HashSet<uint> _returnAddressQueries = [];

    private FunctionAnalysis(Context context, Function function)
    {
        _context = context;
        Function = function;
    }

    public Function Function { get; }

    public IReadOnlyCollection<Instruction> Instructions => _instructions.Values;

    public IReadOnlySet<uint> Labels => _labels;

    public IReadOnlySet<uint> LandingPads => _landingPads;

    public IReadOnlyDictionary<uint, Flow> Flows => _flows;

    public Function? FallsThroughTo { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    public IReadOnlyList<(uint Target, bool IsThumb)> UnknownTargets => _unknownTargets;

    public static FunctionAnalysis Analyze(Context context, Function function)
    {
        var analysis = new FunctionAnalysis(context, function);
        analysis._pending.Push((function.Address, function.IsThumb));

        bool resolved;
        do
        {
            while (analysis._pending.TryPop(out var start))
            {
                analysis.FollowPath(start.Address, start.IsThumb);
            }

            // A switch can be reached before another predecessor has been
            // decoded. Retry its bound proof after those paths are available.
            resolved = false;
            foreach (var instruction in analysis._unboundedJumpTables.ToArray())
            {
                if (analysis.FindJumpTable(instruction) is not { Kind: FlowKind.JumpTable } flow) continue;
                analysis._flows[instruction.Address] = flow;
                foreach (uint target in flow.LocalTargets) analysis.AddLabel(target, instruction.IsThumb);
                resolved = true;
            }
        }
        while (resolved);

        foreach (var instruction in analysis._unboundedJumpTables)
        {
            analysis._errors.Add($"The jump table dispatch at 0x{instruction.Address:X8} has no range check before it, so its size is unknown. Replace the function through patches.ignored.");
        }

        analysis.CheckLiterals();
        analysis.CheckHooks();
        foreach (var key in context.ComputedJumps.Keys.Where(key => key.Function == function.Name))
        {
            if (!analysis._instructions.ContainsKey(key.Address))
            {
                analysis._errors.Add($"The configured computed jump at 0x{key.Address:X8} is not at an instruction that runs.");
            }
        }
        analysis.FindLandingPads();
        return analysis;
    }

    private void FindLandingPads()
    {
        foreach (uint address in _context.PCRelativeAddressesIn(Function))
        {
            bool startsBlock = _labels.Contains(address)
                || (_instructions.ContainsKey(address)
                    && TryGetInstructionBefore(address, out var previous)
                    && _flows.ContainsKey(previous.Address));
            if (startsBlock)
            {
                _landingPads.Add(address);
                _labels.Add(address);
            }
        }
    }

    private void CheckHooks()
    {
        foreach (var hook in _context.Hooks[Function.Name])
        {
            if (hook.BeforeAddress != 0 && !_instructions.ContainsKey(hook.BeforeAddress))
            {
                _errors.Add($"The hook before 0x{hook.BeforeAddress:X8} is not at an instruction that runs. Move it to one that does.");
            }
        }
    }

    private void CheckLiterals()
    {
        foreach (var load in _instructions.Values)
        {
            if (load is not { Opcode: Opcode.Ldr, Rn: PC, OperandKind: OperandKind.Immediate, PreIndexed: true })
            {
                continue;
            }

            uint literal = load.LiteralAddress;

            bool isDecoded = _instructions.ContainsKey(literal)
                || _instructions.ContainsKey(literal + 2)
                || (_instructions.TryGetValue(literal - 2, out var previous) && previous.Size == 4);

            if (isDecoded)
            {
                _errors.Add($"The literal at 0x{literal:X8} loaded by 0x{load.Address:X8} was also decoded as an instruction. If the call before it never returns, add that function to input.noreturn_funcs.");
            }
        }
    }

    private bool IsFarJump(Instruction instruction)
    {
        return instruction.Opcode == Opcode.Bl
            && instruction.Target > Function.Address
            && instruction.Target < Function.End
            && _context.FindFunction(instruction.Target) is null
            && !UsesReturnAddress(instruction.Target, instruction.IsThumb);
    }

    private bool UsesReturnAddress(uint start, bool isThumb)
    {
        // Recursive local long branches must not make an unproven cycle look
        // like a non-returning block.
        if (!_returnAddressQueries.Add(start)) return true;
        try { return FollowReturnAddress(start, isThumb); }
        finally { _returnAddressQueries.Remove(start); }
    }

    private bool FollowReturnAddress(uint start, bool isThumb)
    {
        // LR can be passed as diagnostic data by a local long branch. Follow
        // its value until it is returned through PC, saved, or clobbered.
        var visited = new HashSet<(uint Address, ushort Registers)>();
        var pending = new Stack<(uint Address, ushort Registers)>([(start, 1 << LR)]);
        while (pending.TryPop(out var path))
        {
            uint address = path.Address;
            ushort registers = path.Registers;
            while (registers != 0 && address + (isThumb ? 2 : 4) <= Function.End && visited.Add((address, registers)))
            {
                var instruction = Decode(address, isThumb);
                bool localLongBranch = IsFarJump(instruction);
                bool readsReturnAddress = Enumerable.Range(0, PC).Any(register =>
                    (registers & (1 << register)) != 0 && instruction.Reads((byte)register));
                bool savesReturnAddress = instruction.Opcode == Opcode.Stm && (instruction.RegisterList & registers) != 0
                    || (instruction.Opcode is Opcode.Str or Opcode.Strb or Opcode.Strh && (registers & (1 << instruction.Rd)) != 0);
                if (savesReturnAddress || (readsReturnAddress && (instruction.Writes(PC) || instruction.Opcode == Opcode.Bx)))
                {
                    return true;
                }

                ushort before = registers;
                for (byte register = 0; register < PC; register++)
                {
                    if (instruction.Writes(register)) registers &= (ushort)~(1 << register);
                }
                if (readsReturnAddress && instruction.Opcode <= Opcode.Mvn && !instruction.IsCompare)
                {
                    registers |= (ushort)(1 << instruction.Rd);
                }
                // BL definitely overwrites LR. Other registers may survive an
                // assembly helper, even when the ABI permits it to clobber them.
                if (instruction.Opcode == Opcode.Bl) registers = (ushort)(registers & ~(1 << LR));
                if (instruction.Condition != Condition.AL) registers |= before;

                if ((instruction.Opcode == Opcode.B && Contains(instruction.Target)) || localLongBranch)
                {
                    pending.Push((instruction.Target, registers));
                }

                bool endsPath = instruction.Opcode is Opcode.Undefined or Opcode.Trap
                    || (instruction.Condition == Condition.AL
                        && (instruction.Writes(PC) || instruction.Opcode is Opcode.B or Opcode.Bx || localLongBranch
                            || (instruction.Opcode == Opcode.Bl && _context.FindFunction(instruction.Target) is { IsNoreturn: true })));
                if (endsPath)
                {
                    break;
                }

                address += instruction.Size;
            }
        }

        return false;
    }

    private Instruction Decode(uint address, bool isThumb)
    {
        if (!isThumb)
        {
            return ARMDecoder.Decode(address, _context.ReadUInt32(Function, address));
        }

        ushort next = address + 4 <= Function.End ? _context.ReadUInt16(Function, address + 2) : (ushort)0;
        return ThumbDecoder.Decode(address, _context.ReadUInt16(Function, address), next);
    }

    private void FollowPath(uint address, bool isThumb)
    {
        while (!_instructions.ContainsKey(address))
        {
            if (address == Function.End)
            {
                FallsThroughTo = _context.FindFunction(address);
                if (FallsThroughTo is null)
                {
                    _errors.Add("Execution runs past the end and no function starts there. If the call before it never returns, add that function to input.noreturn_funcs; otherwise fix the size with input.function_sizes.");
                }

                return;
            }

            if (address + (isThumb ? 2 : 4) > Function.End)
            {
                _errors.Add($"The instruction at 0x{address:X8} runs past the end. Fix the size with input.function_sizes.");
                return;
            }

            var instruction = Decode(address, isThumb);
            if (instruction.Opcode == Opcode.Undefined)
            {
                _errors.Add($"Undefined instruction 0x{instruction.Encoding:X} at 0x{address:X8}. If it isn't code, check input.noreturn_funcs and input.arm_funcs; if it is, replace the function through patches.ignored.");
                return;
            }

            bool configuredJump = _context.ComputedJumps.ContainsKey((Function.Name, instruction.Address));
            if (configuredJump && !IsPCRelativeDispatch(instruction) && !IsThumbRegisterDispatch(instruction))
            {
                _errors.Add($"The configured computed jump at 0x{address:X8} is not an ARM ADD of a shifted register to PC or a Thumb MOV of a register to PC.");
                return;
            }

            if (Unsupported(instruction, configuredJump) is { } problem)
            {
                _errors.Add($"0x{address:X8} ({Disassembler.Format(instruction)}) {problem}");
                return;
            }

            _instructions.Add(address, instruction);

            var flow = Classify(instruction);
            if (flow.Kind != FlowKind.Next)
            {
                _flows.Add(address, flow);
            }

            bool targetIsThumb = flow.Kind == FlowKind.BranchExchange ? (flow.Target & 1) != 0 : isThumb;
            foreach (uint target in flow.LocalTargets)
            {
                AddLabel(target, targetIsThumb);
            }

            if (!flow.FallsThrough(instruction))
            {
                return;
            }

            address += instruction.Size;
        }
    }

    private Flow Classify(Instruction instruction)
    {
        if (_context.ComputedJumps.TryGetValue((Function.Name, instruction.Address), out var targets))
        {
            return new Flow(FlowKind.JumpTable, Targets: targets, PCRelativeTargets: IsPCRelativeDispatch(instruction));
        }
        switch (instruction.Opcode)
        {
            case Opcode.Trap:
                return new Flow(FlowKind.Trap);
            case Opcode.B when Contains(instruction.Target):
                return new Flow(FlowKind.Branch, instruction.Target);

            case Opcode.B:
                if (_context.FindFunction(instruction.Target) is null)
                {
                    _unknownTargets.Add((instruction.Target, instruction.IsThumb));
                    _errors.Add($"The branch at 0x{instruction.Address:X8} leaves for 0x{instruction.Target:X8}, which is not a function. Add one there with input.manual_funcs.");
                }

                return new Flow(FlowKind.TailCall, instruction.Target);

            case Opcode.Bl when IsFarJump(instruction):
                return new Flow(FlowKind.Branch, instruction.Target);

            case Opcode.Bl:
                var callee = _context.FindFunction(instruction.Target);
                if (callee is null)
                {
                    _unknownTargets.Add((instruction.Target, instruction.IsThumb));
                    _errors.Add($"The call at 0x{instruction.Address:X8} targets 0x{instruction.Target:X8}, which is not a function. Add one there with input.manual_funcs.");
                }

                return new Flow(callee is { IsNoreturn: true } ? FlowKind.NoreturnCall : FlowKind.Call, instruction.Target);

            case Opcode.Swi when instruction.BIOSFunction is SoftReset or HardReset:
                return new Flow(FlowKind.NoreturnCall);

            case Opcode.Bx:
                return FindBranchExchange(instruction) ?? FindIndirectCall(instruction) ?? new Flow(FlowKind.IndirectJump);

            case Opcode.Mov when instruction.Rd == PC:
                return FindJumpTable(instruction) ?? FindIndirectCall(instruction) ?? new Flow(FlowKind.IndirectJump);

            case var _ when instruction.Writes(PC):
                return new Flow(FlowKind.IndirectJump);

            default:
                return new Flow(FlowKind.Next);
        }
    }

    private static bool IsPCRelativeDispatch(Instruction i) => i is
        { IsThumb: false, Opcode: Opcode.Add, Rd: PC, Rn: PC, OperandKind: OperandKind.ImmediateShift,
          ShiftType: ShiftType.LSL, SetsFlags: false } && i.Rm != PC;

    private static bool IsThumbRegisterDispatch(Instruction i) => i is
        { IsThumb: true, Opcode: Opcode.Mov, Rd: PC, OperandKind: OperandKind.ImmediateShift,
          ShiftType: ShiftType.LSL, ShiftAmount: 0, SetsFlags: false } && i.Rm != PC;

    private static string? Unsupported(Instruction i, bool configuredJump)
    {
        bool usesPC = i.Opcode switch
        {
            <= Opcode.Mvn => i.OperandKind == OperandKind.RegisterShift && i.Rs == PC,
            Opcode.Mul => i.Rd == PC || i.Rs == PC || i.Rm == PC,
            >= Opcode.Mla and <= Opcode.Smlal => i.Rd == PC || i.Rn == PC || i.Rs == PC || i.Rm == PC,
            Opcode.Mrs => i.Rd == PC,
            Opcode.Msr => i.OperandKind == OperandKind.ImmediateShift && i.Rm == PC,
            Opcode.Swp or Opcode.Swpb => i.Rd == PC || i.Rn == PC || i.Rm == PC,
            >= Opcode.Ldr and <= Opcode.Strh => (i.OperandKind == OperandKind.ImmediateShift && i.Rm == PC) || (i.WriteBack && i.Rn == PC),
            Opcode.Ldm or Opcode.Stm => i.Rn == PC,
            _ => false,
        };

        if (usesPC)
        {
            return "uses R15 where the CPU only takes R0 to R14. Replace the function through patches.ignored.";
        }

        if (i.Opcode <= Opcode.Mvn && i.Rd == PC && i.SetsFlags && !i.IsCompare)
        {
            return "returns from an exception, which isn't supported. Replace the function through patches.ignored.";
        }

        if (i.Opcode is Opcode.Ldm or Opcode.Stm && i.PSROrUserBank)
        {
            return "has the S bit set, which isn't supported. Replace the function through patches.ignored.";
        }

        if (i.Opcode is Opcode.Ldm or Opcode.Stm && i.RegisterList == 0)
        {
            return "has an empty register list, which isn't supported. Replace the function through patches.ignored.";
        }

        bool computesJumpFromPC = i.Rd == PC && i.Rn == PC && i.OperandKind != OperandKind.Immediate
            && (i.Opcode is Opcode.Ldr || (i.Opcode <= Opcode.Mvn && !i.IsCompare));
        if (computesJumpFromPC && !configuredJump)
        {
            return "jumps to an address worked out from R15. Only agbcc's Thumb jump tables are recognized, so replace the function through patches.ignored.";
        }

        return null;
    }

    private bool Contains(uint address) => address >= Function.Address && address < Function.End;

    private void AddLabel(uint address, bool isThumb)
    {
        if (!Contains(address))
        {
            return;
        }

        _labels.Add(address);
        _pending.Push((address, isThumb));
    }

    private bool TryGetPrevious(Instruction instruction, out Instruction previous)
    {
        return _instructions.TryGetValue(instruction.Address - (instruction.IsThumb ? 2u : 4u), out previous);
    }

    private Flow? FindBranchExchange(Instruction bx)
    {
        uint target;
        if (bx.Rm == PC)
        {
            target = bx.AlignedPC;
        }
        else if (TryGetPrevious(bx, out var adr)
            && adr is { Opcode: Opcode.Add, Rn: PC, OperandKind: OperandKind.Immediate }
            && adr.Rd == bx.Rm
            && adr.Condition == bx.Condition)
        {
            target = adr.AlignedPC + adr.Immediate;
        }
        else
        {
            return null;
        }

        if (!Contains(target & ~1u))
        {
            _errors.Add($"The BX at 0x{bx.Address:X8} leaves the function for 0x{target & ~1u:X8}. Fix the size with input.function_sizes.");
        }

        return new Flow(FlowKind.BranchExchange, target);
    }

    private Flow? FindIndirectCall(Instruction branch)
    {
        if (!TryGetPrevious(branch, out var link) || link.Rd != LR || link.Condition != branch.Condition)
        {
            return null;
        }

        uint returnAddress;
        if (link is { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, Rm: PC, ShiftAmount: 0 })
        {
            returnAddress = link.Address + (link.IsThumb ? 4u : 8u);
        }
        else if (link is { Opcode: Opcode.Add, Rn: PC, OperandKind: OperandKind.Immediate })
        {
            returnAddress = link.AlignedPC + link.Immediate;
        }
        else
        {
            return null;
        }

        if (!Contains(returnAddress))
        {
            _errors.Add($"The indirect call at 0x{branch.Address:X8} returns to 0x{returnAddress:X8}, outside the function. Fix the size with input.function_sizes.");
        }

        return new Flow(FlowKind.IndirectCall, returnAddress);
    }

    private Flow? FindJumpTable(Instruction movPC)
    {
        if (!movPC.IsThumb
            || FindWriter(movPC.Rm, movPC) is not { Opcode: Opcode.Ldr } loadEntry)
        {
            return null;
        }

        // agbcc adds the table and scaled index before loading; GCC can use
        // the same operands directly in a register-indexed word load.
        Instruction? addressCalculation = loadEntry switch
        {
            { OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 } => loadEntry,
            { OperandKind: OperandKind.Immediate, Immediate: 0 }
                when FindWriter(loadEntry.Rn, loadEntry) is
                    { Opcode: Opcode.Add, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 } addTable => addTable,
            _ => null,
        };
        if (addressCalculation is not { } calculation || FindTableAndIndex(calculation) is not { } table)
        {
            return null;
        }

        uint? entryCount = FindJumpTableEntryCount(table.ShiftIndex, movPC);
        if (entryCount is null or 0)
        {
            _unboundedJumpTables.Add(movPC);
            return null;
        }
        _unboundedJumpTables.Remove(movPC);

        if (ReadLiteral(table.LoadTable) is not { } tableAddress || (tableAddress & 3) != 0)
        {
            _errors.Add($"The jump table for 0x{movPC.Address:X8} has no readable, word-aligned address.");
            return null;
        }

        ulong tableSize = (ulong)entryCount.Value * 4;
        bool tableInFunction = tableAddress >= Function.Address && (ulong)tableAddress + tableSize <= Function.End;
        if (!tableInFunction && (tableSize > uint.MaxValue || !_context.ROM.Contains(tableAddress, (uint)tableSize)))
        {
            _errors.Add($"The jump table for 0x{movPC.Address:X8} is outside the function and its full range is not in ROM.");
            return null;
        }

        var targets = new uint[entryCount.Value];
        for (uint i = 0; i < targets.Length; i++)
        {
            // Inline tables can be linked in RAM; external tables use ROM addresses.
            uint entryAddress = tableAddress + i * 4;
            uint target = (tableInFunction ? _context.ReadUInt32(Function, entryAddress) : _context.ROM.ReadUInt32(entryAddress)) & ~1u;
            if (!Contains(target))
            {
                _errors.Add($"Jump table entry 0x{target:X8} at 0x{tableAddress + i * 4:X8} is outside the function. Fix the size with input.function_sizes.");
                return null;
            }

            targets[i] = target;
        }

        return new Flow(FlowKind.JumpTable, Targets: targets.Distinct().ToArray());
    }

    private (Instruction LoadTable, Instruction ShiftIndex)? FindTableAndIndex(Instruction calculation)
    {
        foreach (var (table, index) in new[] { (calculation.Rn, calculation.Rm), (calculation.Rm, calculation.Rn) })
        {
            if (FindWriter(table, calculation) is { Opcode: Opcode.Ldr, Rn: PC } loadTable && FindScaledIndex(index, calculation) is { } shiftIndex)
            {
                return (loadTable, shiftIndex);
            }
        }

        return null;
    }

    private Instruction? FindScaledIndex(byte register, Instruction before)
    {
        return FindWriter(register, before) switch
        {
            { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 2 } shift => shift,
            { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 } copy => FindScaledIndex(copy.Rm, copy),
            { Opcode: Opcode.Add, OperandKind: OperandKind.Immediate, Immediate: 0 } copy => FindScaledIndex(copy.Rn, copy),
            _ => null,
        };
    }

    private uint? FindJumpTableEntryCount(Instruction shiftIndex, Instruction movPC)
    {
        var path = PathBefore(movPC).ToList();
        int rangeCheck = path.FindIndex(step => step.Instruction.Opcode == Opcode.B
            && (step.IsTaken ? step.Instruction.Condition is Condition.LS or Condition.CC
                : step.Instruction.Condition is Condition.HI or Condition.CS));

        if (rangeCheck < 0
            || rangeCheck + 1 == path.Count
            || path[rangeCheck + 1].Instruction is not { Opcode: Opcode.Cmp } compare)
        {
            return null;
        }

        var index = FindIndexValue(shiftIndex.Rm, shiftIndex);
        var compared = FindIndexValue(compare.Rn, compare);
        if (index.Register != compared.Register || !SameIndexValue(index, compared))
        {
            return null;
        }

        uint? bound = compare.OperandKind == OperandKind.Immediate
            ? compare.Immediate
            : FindConstant(compare.Rm, compare.Address);
        bool exclusive = path[rangeCheck].Instruction.Condition is Condition.CC or Condition.CS;
        if (bound is null || (exclusive && bound == 0)) return null;

        ulong last = exclusive ? bound.Value - 1 : bound.Value;
        ulong modulus = 1ul << compared.Bits;
        ulong upper = Math.Min(((last + 1) << compared.RightShift) - 1, modulus - 1);
        if (index.Bits == compared.Bits && index.Adjustment == compared.Adjustment)
        {
            ulong count = (upper >> index.RightShift) + 1;
            return count <= uint.MaxValue ? (uint)count : null;
        }
        var range = IndexRange(compared.Register, compared.ReadAt);
        // A narrowing conversion must not let a different high-bit value pass
        // the check while indexing the original, untruncated register.
        if (upper >= modulus || range.Max + compared.Adjustment >= (long)modulus
            || range.Min + compared.Adjustment + (long)modulus <= (long)upper)
        {
            return null;
        }

        long minimumIndex = index.Adjustment - compared.Adjustment;
        long maximumIndex = (long)upper + minimumIndex;
        if (minimumIndex < 0 || maximumIndex >= (1L << index.Bits)) return null;
        ulong entries = ((ulong)maximumIndex >> index.RightShift) + 1;
        return entries <= uint.MaxValue ? (uint)entries : null;
    }

    private readonly record struct IndexValue(byte Register, Instruction ReadAt, long Adjustment = 0, int Bits = 32, int RightShift = 0);

    private IndexValue FindIndexValue(byte register, Instruction before)
    {
        var value = new IndexValue(register, before);
        if (FindWriter(register, before) is not { } writer) return value;
        if (writer is { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 })
            return FindIndexValue(writer.Rm, writer);
        if (writer is { Opcode: Opcode.Add or Opcode.Sub, OperandKind: OperandKind.Immediate })
        {
            var source = FindIndexValue(writer.Rn, writer);
            if (source.Bits == 32 && source.RightShift == 0)
                return source with { Adjustment = source.Adjustment + (writer.Opcode == Opcode.Add ? writer.Immediate : -(long)writer.Immediate) };
        }
        if (writer is { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSR })
        {
            if (FindWriter(writer.Rm, writer) is { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift,
                    ShiftType: ShiftType.LSL, ShiftAmount: > 0 } left && left.ShiftAmount <= writer.ShiftAmount)
            {
                var source = FindIndexValue(left.Rm, left);
                if (source.RightShift == 0)
                    return source with { Bits = Math.Min(source.Bits, 32 - left.ShiftAmount), RightShift = writer.ShiftAmount - left.ShiftAmount };
            }
            else
            {
                var source = FindIndexValue(writer.Rm, writer);
                if (source.RightShift + writer.ShiftAmount < 32)
                    return source with { RightShift = source.RightShift + writer.ShiftAmount };
            }
        }
        return value;
    }

    private bool SameIndexValue(IndexValue first, IndexValue second)
    {
        if (first.ReadAt.Address == second.ReadAt.Address) return true;
        var earlier = first.ReadAt.Address < second.ReadAt.Address ? first : second;
        var later = first.ReadAt.Address > second.ReadAt.Address ? first : second;
        var between = PathBefore(later.ReadAt).ToList();
        int read = between.FindIndex(step => step.Instruction.Address == earlier.ReadAt.Address);
        return read >= 0 && !between.Take(read + 1).Any(step => MayChange(step.Instruction, first.Register));
    }

    private (long Min, long Max) IndexRange(byte register, Instruction before)
    {
        // Each incoming path must establish a bounded value. A typed load on
        // only one predecessor cannot justify truncating an unknown value.
        (long Min, long Max) unknown = (int.MinValue, int.MaxValue);
        long minimum = long.MaxValue, maximum = long.MinValue;
        var pending = new Stack<uint>([before.Address]);
        var visited = new HashSet<uint>();
        while (pending.TryPop(out uint address))
        {
            if (!visited.Add(address)) continue;
            if (address == Function.Address || visited.Count > JumpTablePathLimit) return unknown;
            var predecessors = _flows.Where(pair => pair.Value.LocalTargets.Contains(address))
                .Select(pair => _instructions[pair.Key]).ToList();
            if (TryGetInstructionBefore(address, out var previous)
                && (!_flows.TryGetValue(previous.Address, out var flow) || flow.FallsThrough(previous)))
                predecessors.Add(previous);
            if (predecessors.Count == 0) return unknown;
            foreach (var predecessor in predecessors)
            {
                if (predecessor.Writes(register))
                {
                    var range = predecessor switch
                    {
                        { Opcode: Opcode.Ldrb } => (0L, byte.MaxValue),
                        { Opcode: Opcode.Ldrh } => (0L, ushort.MaxValue),
                        { Opcode: Opcode.Ldrsb } => ((long)sbyte.MinValue, sbyte.MaxValue),
                        { Opcode: Opcode.Ldrsh } => ((long)short.MinValue, short.MaxValue),
                        { Opcode: Opcode.Mov, OperandKind: OperandKind.Immediate } literal => ((long)(int)literal.Immediate, (int)literal.Immediate),
                        _ => unknown,
                    };
                    if (range == unknown) return unknown;
                    minimum = Math.Min(minimum, range.Item1);
                    maximum = Math.Max(maximum, range.Item2);
                    continue;
                }
                if (MayChange(predecessor, register)) return unknown;
                pending.Push(predecessor.Address);
            }
        }
        return minimum <= maximum ? (minimum, maximum) : unknown;
    }

    private Instruction? FindWriter(byte register, Instruction before)
    {
        foreach (var (instruction, _) in PathBefore(before))
        {
            if (instruction.Writes(register))
            {
                return instruction;
            }

            if (MayChange(instruction, register))
            {
                return null;
            }
        }

        return null;
    }

    private static bool MayChange(Instruction instruction, byte register)
    {
        return instruction.Writes(register)
            || instruction.Opcode == Opcode.Swi
            || (instruction.Opcode == Opcode.Bl && (PreservedAcrossCalls & (1 << register)) == 0);
    }

    private IEnumerable<(Instruction Instruction, bool IsTaken)> PathBefore(Instruction start)
    {
        uint address = start.Address;
        for (int step = 0; step < JumpTablePathLimit; step++)
        {
            bool hasPrevious = TryGetInstructionBefore(address, out var previous);
            bool hasFlow = _flows.TryGetValue(previous.Address, out var flow);
            bool isTaken = hasPrevious && hasFlow && !flow.FallsThrough(previous);
            if (!hasPrevious || (isTaken
                && (flow.Kind != FlowKind.Branch
                    || !TryGetInstructionBefore(previous.Address, out previous)
                    || previous is not { Opcode: Opcode.B, Condition: not Condition.AL }
                    || previous.Target != address)))
            {
                // GCC can put another block immediately before a branch target.
                // Follow its incoming edge only when that predecessor is unique.
                var incoming = _instructions.Values.Where(i => i.Opcode == Opcode.B && i.Target == address).Take(2).ToArray();
                if (incoming.Length != 1) yield break;
                previous = incoming[0];
                isTaken = true;
            }

            yield return (previous, isTaken);
            address = previous.Address;
        }
    }

    private bool TryGetInstructionBefore(uint address, out Instruction previous)
    {
        return _instructions.TryGetValue(address - 2, out previous)
            || (_instructions.TryGetValue(address - 4, out previous) && previous.Size == 4);
    }

    private uint? FindConstant(byte register, uint address)
    {
        if (!_instructions.TryGetValue(address, out var before) || FindWriter(register, before) is not { } writer) return null;
        return writer switch
        {
            { Opcode: Opcode.Ldr, Rn: PC } => ReadLiteral(writer),
            { Opcode: Opcode.Mov, OperandKind: OperandKind.Immediate } => writer.Immediate,
            { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL }
                => FindConstant(writer.Rm, writer.Address) << writer.ShiftAmount,
            _ => null,
        };
    }

    private uint? ReadLiteral(Instruction load)
    {
        uint address = load.LiteralAddress;
        return address >= Function.Address && address + 4 <= Function.End ? _context.ReadUInt32(Function, address) : null;
    }
}
