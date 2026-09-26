using System.Text;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

public class JsonRpcConnectionTests
{
    internal static byte[] Frame(string body, string extraHeaders = "")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n{extraHeaders}\r\n").Concat(bytes).ToArray();
    }

    [Fact]
    public async Task A_message_round_trips_through_the_framing()
    {
        var stream = new MemoryStream();
        await new JsonRpcConnection(Stream.Null, stream).WriteAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("method", "héllo");
            w.WriteEndObject();
        }, CancellationToken.None);
        Assert.StartsWith("Content-Length: ", Encoding.ASCII.GetString(stream.ToArray()));

        stream.Position = 0;
        var reader = new JsonRpcConnection(stream, Stream.Null);
        using (var message = await reader.ReadAsync(CancellationToken.None))
            Assert.Equal("héllo", message!.RootElement.GetProperty("method").GetString());
        Assert.Null(await reader.ReadAsync(CancellationToken.None)); // end of input between messages
    }

    [Fact]
    public async Task Extra_headers_are_ignored()
    {
        var input = new MemoryStream(Frame("{\"a\":1}", "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n"));
        using var message = await new JsonRpcConnection(input, Stream.Null).ReadAsync(CancellationToken.None);
        Assert.Equal(1, message!.RootElement.GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData("Nonsense\r\n\r\n")]
    [InlineData("Content-Type: x\r\n\r\n{}")]
    [InlineData("Content-Length: 10\r\n")]
    public async Task Broken_framing_throws(string raw)
    {
        var input = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonRpcConnection(input, Stream.Null).ReadAsync(CancellationToken.None));
    }
}
