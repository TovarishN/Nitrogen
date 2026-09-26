using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nitrogen.LanguageService.Lsp;

/// <summary>
/// LSP's base protocol (issue 238): <c>Content-Length</c> headers, a blank line, a UTF-8 JSON body.
/// Other headers are ignored. Broken framing throws <see cref="InvalidDataException"/>.
/// </summary>
public sealed class JsonRpcConnection(Stream input, Stream output)
{
    readonly byte[] _one = new byte[1];

    /// <summary>The next message, or null when the input ends between messages.</summary>
    public async Task<JsonDocument?> ReadAsync(CancellationToken cancel)
    {
        int length = -1;
        bool first = true;
        while (true)
        {
            string? line = await ReadHeaderLineAsync(cancel);
            if (line is null)
            {
                if (first) return null;
                throw new InvalidDataException("the input ended inside a message header");
            }
            first = false;
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon < 0) throw new InvalidDataException($"malformed header '{line}'");
            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length))
                    throw new InvalidDataException($"malformed header '{line}'");
            }
        }
        if (length < 0) throw new InvalidDataException("a message without Content-Length");

        var body = new byte[length];
        try
        {
            await input.ReadExactlyAsync(body, cancel);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("the input ended inside a message body");
        }
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"a message body is not JSON: {error.Message}");
        }
    }

    /// <summary>Writes one message; <paramref name="write"/> writes its JSON.</summary>
    public async Task WriteAsync(Action<Utf8JsonWriter> write, CancellationToken cancel)
    {
        var body = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(body)) write(writer);
        await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.WrittenCount}\r\n\r\n"), cancel);
        await output.WriteAsync(body.WrittenMemory, cancel);
        await output.FlushAsync(cancel);
    }

    /// <summary>One header line without its line break; null at the end of the input before any byte.</summary>
    async Task<string?> ReadHeaderLineAsync(CancellationToken cancel)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (await input.ReadAsync(_one, cancel) == 0) return line.Length == 0 ? null : throw new InvalidDataException("the input ended inside a message header");
            char c = (char)_one[0];
            if (c == '\n') return line.ToString().TrimEnd('\r');
            line.Append(c);
        }
    }
}
