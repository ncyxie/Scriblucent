using System.Text;
using Scriblucent.AI.Protocol;

namespace Scriblucent.Tests;

public sealed class WorkerLineReaderTests
{
    [Fact]
    public async Task ReadRequestAsync_TwoMessagesCoalescedInOneRead_BothAreReturned()
    {
        // Named pipes in Byte mode give no message-boundary guarantee - a single ReadAsync can
        // return two already-written, newline-terminated messages concatenated together. This is
        // exactly the scenario that used to silently drop the second message.
        var line1 = """{"type":"stop"}""" + "\n";
        var line2 = """{"type":"tokenize","text":"hello"}""" + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line1 + line2));
        var reader = new WorkerLineReader(stream);

        var first = await reader.ReadRequestAsync();
        var second = await reader.ReadRequestAsync();

        Assert.IsType<WorkerStopRequest>(first);
        Assert.IsType<WorkerTokenizeRequest>(second);
        Assert.Equal("hello", ((WorkerTokenizeRequest)second!).Text);
    }

    [Fact]
    public async Task ReadRequestAsync_MessageSplitAcrossManySmallReads_IsReassembledCorrectly()
    {
        var json = """{"type":"tokenize","text":"a longer message split across several reads"}""" + "\n";
        using var stream = new ThrottledStream(Encoding.UTF8.GetBytes(json), maxBytesPerRead: 3);
        var reader = new WorkerLineReader(stream);

        var request = await reader.ReadRequestAsync();

        Assert.IsType<WorkerTokenizeRequest>(request);
        Assert.Equal("a longer message split across several reads", ((WorkerTokenizeRequest)request!).Text);
    }

    [Fact]
    public async Task ReadRequestAsync_MultiByteUtf8CharacterSplitAcrossReads_IsNotCorrupted()
    {
        // '\n' (0x0A) can never appear as part of a multi-byte UTF-8 sequence, so scanning for it
        // at the byte level (rather than decoding each raw read chunk independently) must survive
        // a multi-byte character straddling two reads without corruption.
        var json = """{"type":"tokenize","text":"Lietuvių kalbos užrašas su ą č ę raidėmis"}""" + "\n";
        using var stream = new ThrottledStream(Encoding.UTF8.GetBytes(json), maxBytesPerRead: 1);
        var reader = new WorkerLineReader(stream);

        var request = await reader.ReadRequestAsync();

        Assert.IsType<WorkerTokenizeRequest>(request);
        Assert.Equal("Lietuvių kalbos užrašas su ą č ę raidėmis", ((WorkerTokenizeRequest)request!).Text);
    }

    [Fact]
    public async Task ReadRequestAsync_ThreeMessagesAcrossTwoReads_AllThreeAreReturnedInOrder()
    {
        var lines = string.Concat(
            """{"type":"stop"}""" + "\n",
            """{"type":"tokenize","text":"first"}""" + "\n",
            """{"type":"tokenize","text":"second"}""" + "\n");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(lines));
        var reader = new WorkerLineReader(stream);

        var a = await reader.ReadRequestAsync();
        var b = await reader.ReadRequestAsync();
        var c = await reader.ReadRequestAsync();

        Assert.IsType<WorkerStopRequest>(a);
        Assert.Equal("first", ((WorkerTokenizeRequest)b!).Text);
        Assert.Equal("second", ((WorkerTokenizeRequest)c!).Text);
    }

    [Fact]
    public async Task ReadRequestAsync_StreamClosed_ReturnsNull()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var reader = new WorkerLineReader(stream);

        var request = await reader.ReadRequestAsync();

        Assert.Null(request);
    }

    /// Test double simulating a Byte-mode named pipe that never returns more than a fixed number
    /// of bytes per ReadAsync call, regardless of how much buffer space is offered - forces
    /// WorkerLineReader's buffering to actually span multiple reads instead of always completing
    /// in one.
    private sealed class ThrottledStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maxBytesPerRead;

        public ThrottledStream(byte[] data, int maxBytesPerRead)
        {
            _inner = new MemoryStream(data);
            _maxBytesPerRead = maxBytesPerRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var limited = buffer.Length > _maxBytesPerRead ? buffer[.._maxBytesPerRead] : buffer;
            return _inner.ReadAsync(limited, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
