# GBARecomp

GBARecomp is a static recompiler that turns a Game Boy Advance ROM into C# code. Every function in the game becomes a C# method, which you then build and run with [GBA Modern Runtime](https://github.com/Asphaltian/GBAModernRuntime).

Keep in mind that GBARecomp needs to know where every function is, so you'll need a decompilation or disassembly of the game.

## Building

You will need the .NET 10 SDK.

```
git clone https://github.com/Asphaltian/GBARecomp
cd GBARecomp
dotnet build -c Release
```

## Running

Run GBARecomp with a TOML config for your game:

```
dotnet run --project src/GBARecomp -c Release -- game.toml
```

It generates `Funcs`, which has a method for every function in the game, and `Data`, which has the address of every other symbol. Only the files that actually changed get rewritten, so your build doesn't have to start over every time. If something can't be recompiled, GBARecomp lists every problem it found and writes nothing.

Thumb word jump tables can use agbcc's separate ADD-plus-load pattern or GCC's register-indexed load before `mov pc`. Analysis requires a recognized range check and local case targets. Tables may be inline in the function, including ROM-backed RAM code, or in a fully validated ROM range outside it. Unknown bounds and invalid tables remain generation errors.

Range analysis accepts inclusive (`bhi`/`bls`) and exclusive (`bcs`/`bcc`)
unsigned checks, unchanged register copies, and proven narrowing/offset patterns.
When the guard truncates an index that the table uses at full width, every
incoming path must establish a compatible typed range; an unknown high-bit value
or a clobbered index still fails. Bounds are retried after other reachable paths
have been decoded. Constant tracing and predecessor walks remain bounded.

Local `bl` blocks can read LR as diagnostic data before calling another function.
Return-address analysis follows the LR value through register copies and call
clobbers, rather than treating every LR read as a return. Saving that value or
returning through it still identifies a call. Generated local branches retain
the architectural LR write and the original named function and patch surface.

## Config

Paths in the config are relative to the config file itself. Here's an example you can start from:

```toml
[input]
rom_file_path = "game.gba"
# Or elf_path, if your decompilation builds an ELF
symbols_file_path = "game.sym"
output_func_path = "RecompiledFuncs"

# Where the game's code starts, and how many bytes of it there are
text_address = 0x080000C0
text_size = 0x100000

# Functions that start in ARM state. Everything else is Thumb, unless your ELF says otherwise
arm_funcs = ["AgbMain"]

# Functions that never return
noreturn_funcs = ["Hang"]

# Functions the game copies into RAM before it runs them
ram_funcs = ["SoundMainRAM"]

# Functions the symbols don't have, like code built to run from IWRAM
manual_funcs = [
    { name = "FastCopy", address = 0x03000000, size = 0x40, rom_address = 0x08100000 },
]

# Sizes to use when the symbols get one wrong
function_sizes = [
    { name = "SoundMain", size = 0x90 },
]

# Finite targets for an ARM `add pc, pc, register, lsl #shift` dispatch.
# Thumb `mov pc, register` dispatches use the same declaration.
# Both the instruction offset and target offsets are relative to the named function.
computed_jumps = [
    { func = "Dispatch", offset = 0x10, target_offsets = [0x18, 0x28] },
]

[patches]
# These become empty functions
stubs = ["DebugPrint"]

# These aren't recompiled at all, so you'll have to replace them yourself
ignored = ["StrangeSwitch"]
```

`computed_jumps` is for dispatches whose complete target set is known from the
game's code and data. GBARecomp checks the function name, instruction form,
alignment (four bytes for ARM, two for Thumb), and function bounds. It visits every
declared target and emits the existing local switch dispatcher; an undeclared
target throws at runtime.
ARM PC-relative targets in a `ram_funcs` function are normalized back to the
original function's address before selecting a case. Thumb MOV-to-PC dispatches
use the register's absolute target without that adjustment. Copies in either RAM
region retain the same named function and hooks.

Thumb undefined-instruction traps (`0xDE00`–`0xDEFF`) and the emulator assertion
stop (`0xEFFF`) end that execution path. They generate explicit native exceptions
with the function, instruction address, and encoding. Returning paths in the same
function still run normally.
