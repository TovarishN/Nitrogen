namespace Nitrogen.Tests;

/// <summary>
/// Framed messages handed to the server one at a time, each only when the server is idle (it has
/// handled everything before and waits), as a client typing slowly sends them. A string array is
/// released at once, as a burst. An <see cref="Action"/> among the items runs at such a moment, before
/// the next message. Set <see cref="Idle"/> to the
/// server's before it runs.
/// </summary>
internal sealed class LockstepInput(params object[] items) : Stream
{
    readonly Queue<object> _items = new(items);
    MemoryStream? _current;
    bool _started;

    public WaitHandle? Idle { get; set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_current is not null)
            {
                int read = _current.Read(buffer, offset, count);
                if (read > 0) return read;
                _current = null;
            }
            if (_items.Count == 0) return 0;
            if (_started) Idle!.WaitOne();
            _started = true;
            switch (_items.Dequeue())
            {
                case Action action:
                    action();
                    break;
                case string body:
                    _current = new MemoryStream(JsonRpcConnectionTests.Frame(body));
                    break;
                case string[] bodies: // released together, so the server may take them in batches of any size
                    _current = new MemoryStream(bodies.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
                    break;
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
