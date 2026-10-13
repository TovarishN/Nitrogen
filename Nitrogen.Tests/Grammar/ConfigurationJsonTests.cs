using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ConfigurationJsonTests
{
    [Theory]
    [InlineData("{\"x\":01}")]
    [InlineData("{\"x\":1.}")]
    [InlineData("{\"x\":1e}")]
    [InlineData("{\"x\":[true,]}")]
    [InlineData("{\"x\":\"\\q\"}")]
    [InlineData("{\"x\":\"\\uXYZW\"}")]
    [InlineData("{\"x\":\"raw\nline\"}")]
    [InlineData("{} extra")]
    [InlineData("{\"x\": [")]
    public void Malformed_json_is_rejected(string json) => Assert.Throws<FormatException>(() => ConfigurationJson.Read(json));

    [Fact]
    public void Escaped_strings_and_unrelated_nested_values_are_decoded()
    {
        var json = ConfigurationJson.Read("""{"name":"D\u0065mo","other":[{"x":-0.12e-3},true,false,null,"\"\\\/\b\f\n\r\t"]}""");
        Assert.Equal("Demo", json["name"]);
        Assert.Equal(5, Assert.IsType<List<object?>>(json["other"]).Count);
    }

    [Fact]
    public void Deeply_nested_configuration_is_bounded() => Assert.Throws<FormatException>(() =>
        ConfigurationJson.Read("{\"x\":" + new string('[', 65) + "0" + new string(']', 65) + "}"));
}
