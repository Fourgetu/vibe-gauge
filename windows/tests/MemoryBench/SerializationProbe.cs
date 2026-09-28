using System.Diagnostics;
using System.Text.Json;
using VibeGauge.Core;

internal static class SerializationProbe
{
    public static void Run()
    {
        var records = Enumerable.Range(0, 20000).ToDictionary(i => i.ToString(), i =>
            new InteractionRecord(i.ToString(), "Codex", "synthetic-model", DateTimeOffset.UnixEpoch,
                9500, 8000, 500, 200, 50));
        foreach (var asynchronous in new[] { false, true })
        {
            using var output = new WriteSizeStream();
            var before = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            if (asynchronous) JsonSerializer.SerializeAsync(output, records).GetAwaiter().GetResult();
            else JsonSerializer.Serialize(output, records);
            timer.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = asynchronous ? "async-stream" : "sync-stream", output.TotalBytes,
                output.LargestWrite, output.Writes,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - before,
                ElapsedMs = timer.Elapsed.TotalMilliseconds
            }));
        }
    }

    private sealed class WriteSizeStream : Stream
    {
        public long TotalBytes { get; private set; }
        public int LargestWrite { get; private set; }
        public int Writes { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Record(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Record(buffer.Length);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record(buffer.Length);
            return ValueTask.CompletedTask;
        }
        private void Record(int count)
        {
            TotalBytes += count;
            LargestWrite = Math.Max(LargestWrite, count);
            Writes++;
        }
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => TotalBytes;
        public override long Position { get => TotalBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
