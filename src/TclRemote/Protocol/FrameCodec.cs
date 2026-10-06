using System.Buffers.Binary;

namespace TclRemote;

internal static class FrameCodec
{
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static void Write(Stream stream, ReadOnlySpan<byte> payload)
    {
        var frame = Encode(payload);
        stream.Write(frame);
        stream.Flush();
    }

    public static byte[] Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        ReadExact(stream, header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > Protocol.MaxFrameLength)
            throw new InvalidDataException($"响应长度异常: {length}");
        if (length == 0)
            return [];

        var payload = new byte[length];
        ReadExact(stream, payload);
        return payload;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
                throw new EndOfStreamException("连接已关闭");
            read += n;
        }
    }
}
