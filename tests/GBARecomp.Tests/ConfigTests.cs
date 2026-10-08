namespace GBARecomp.Tests;

public class ConfigTests : IDisposable
{
    private const string Minimal = """
        [input]
        rom_file_path = "game.gba"
        symbols_file_path = "game.sym"
        output_func_path = "out"
        text_address = 0x08000000
        text_size = 0x100

        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("config").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string toml)
    {
        string path = Path.Combine(_folder, "game.toml");
        File.WriteAllText(path, toml);
        return path;
    }

    [Fact]
    public void PathsAreRelativeToTheConfig()
    {
        var config = Config.Load(Write(Minimal));

        Assert.Equal(Path.Combine(_folder, "game.gba"), config.Input.ROMFilePath);
        Assert.Equal(0x08000000u, config.Input.TextAddress);
    }

    [Fact]
    public void UnknownKeysAreErrors()
    {
        string toml = Minimal + """
            arm_func = ["Foo"]
            manual_funcs = [{ name = "Bar", adress = 0x08000000 }]

            [[patches.hook]]
            func = "Foo"
            txt = "Hooked(ctx);"
            """;

        var error = Assert.Throws<InvalidDataException>(() => Config.Load(Write(toml)));

        Assert.Contains("input.arm_func", error.Message);
        Assert.Contains("input.manual_funcs.adress", error.Message);
        Assert.Contains("patches.hook.txt", error.Message);
    }

    [Fact]
    public void TheTextAddressIsRequired()
    {
        string toml = Minimal.Replace("text_address = 0x08000000", "");

        Assert.Throws<InvalidDataException>(() => Config.Load(Write(toml)));
    }

    [Fact]
    public void ComputedJumpsKeepTheirFunctionRelativeOffsets()
    {
        var config = Config.Load(Write(Minimal + """
            [[input.computed_jumps]]
            func = "Dispatch"
            offset = 0x10
            target_offsets = [0x18, 0x28]
            """));

        var jump = Assert.Single(config.Input.ComputedJumps);
        Assert.Equal("Dispatch", jump.Func);
        Assert.Equal(0x10u, jump.Offset);
        Assert.Equal([0x18u, 0x28u], jump.TargetOffsets);
    }

    [Fact]
    public void LandingPadsKeepTheirFunctionRelativeOffsets()
    {
        var config = Config.Load(Write(Minimal + """
            [[input.landing_pads]]
            func = "ScriptLoop"
            offsets = [0x80, 0x90]
            """));

        var pad = Assert.Single(config.Input.LandingPads);
        Assert.Equal("ScriptLoop", pad.Func);
        Assert.Equal([0x80u, 0x90u], pad.Offsets);
    }
}
