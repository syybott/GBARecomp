using System.Text.Json;

namespace GBARecomp.Tests;

public class MetadataSymbolTests
{
    private static List<Symbol> Read(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"symbols-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return Symbol.ReadFile(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LosslessJSONPreservesOrderDuplicatesModesAndZeroSizeClassifications()
    {
        var expected = new[]
        {
            new Symbol(0x08000008, 0, "ThumbZero", true),
            new Symbol(0x08000000, 0, "ARMZero", false),
            new Symbol(0x08000008, 0, "Alias", true),
            new Symbol(0x08000008, 0, "label"),
            new Symbol(0x02000000, 0, "data"),
            new Symbol(0x02000000, 0, "data"),
            new Symbol(0x08000010, 4, "LegacyMode"),
        };
        string json = JsonSerializer.Serialize(new
        {
            format = "gbarecomp-symbols", format_version = 1,
            symbols = expected.Select(s => new { address = s.Address, size = s.Size, name = s.Name, is_thumb = s.IsThumb }),
        });
        var actual = Read(json);
        Assert.Equal(expected, actual);
        Assert.Equal([true, true, true, false, false, false, true], actual.Select(s => s.IsFunction));
    }

    [Theory]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":2,\"symbols\":[]}")]
    [InlineData("{\"format\":\"other\",\"format_version\":1,\"symbols\":[]}")]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":1,\"symbols\":[],\"payload\":\"bytes\"}")]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":1,\"format_version\":1,\"symbols\":[]}")]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":1,\"symbols\":[{\"address\":4294967296,\"size\":0,\"name\":\"x\",\"is_thumb\":true}]}")]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":1,\"symbols\":[{\"address\":0,\"size\":0,\"name\":\"x\",\"is_thumb\":\"thumb\"}]}")]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":1,\"symbols\":[{\"address\":0,\"size\":0,\"name\":\"x\"}]}")]
    [InlineData("{\"format\":\"gbarecomp-symbols\",\"format_version\":1,\"symbols\":[{\"address\":0,\"size\":0,\"name\":\"\",\"is_thumb\":null}]}")]
    [InlineData("{")]
    public void MalformedOrUnsupportedJSONIsRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => Read(json));
    }

    [Fact]
    public void JSONAndELFReadersReturnExactlyTheSameSyntheticRecords()
    {
        var expected = Symbol.ReadELF(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "elf_test.elf")));
        string json = JsonSerializer.Serialize(new
        {
            format = "gbarecomp-symbols", format_version = 1,
            symbols = expected.Select(s => new { address = s.Address, size = s.Size, name = s.Name, is_thumb = s.IsThumb }),
        });
        Assert.Equal(expected, Read(json));
    }
}
