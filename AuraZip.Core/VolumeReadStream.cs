namespace AuraZip.Core;

// TAR and compressed TAR readers need a continuous stream across raw numbered volumes.
internal sealed class VolumeReadStream : Stream
{
    private readonly List<Stream> _parts = new();
    private readonly List<long> _offsets = new();
    private long _position, _length;
    public VolumeReadStream(IReadOnlyList<string> paths)
    {
        try
        {
            foreach (var path in paths)
            {
                var stream = File.OpenRead(path); _parts.Add(stream); _offsets.Add(_length);
                _length = checked(_length + stream.Length);
            }
        }
        catch { Dispose(); throw; }
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; } }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        for (int i = 0; i < _parts.Count && buffer.Length > 0 && _position < _length; i++)
        {
            var part = _parts[i];
            if (_position >= _offsets[i] + part.Length) continue;
            part.Position = _position - _offsets[i];
            while (buffer.Length > 0 && part.Position < part.Length)
            {
                int read = part.Read(buffer[..(int)Math.Min(buffer.Length, part.Length - part.Position)]);
                if (read == 0) throw new EndOfStreamException("An archive volume ended unexpectedly.");
                _position += read; total += read; buffer = buffer[read..];
            }
        }
        return total;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(_length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) foreach (var part in _parts) part.Dispose(); base.Dispose(disposing); }
}
