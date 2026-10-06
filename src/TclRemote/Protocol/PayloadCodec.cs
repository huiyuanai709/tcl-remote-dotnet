using System.Text;

namespace TclRemote;

internal readonly record struct HandshakeInfo(int AlgorithmType, string? CapabilityText);

internal static class PayloadCodec
{
    public static bool IsPlaintext(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] is < (byte)'0' or > (byte)'9')
            return false;

        var i = 0;
        while (i < payload.Length && payload[i] is >= (byte)'0' and <= (byte)'9')
            i++;

        return i + 1 < payload.Length && payload[i] == (byte)'>' && payload[i + 1] == (byte)'>';
    }

    public static string Decode(ReadOnlySpan<byte> payload)
    {
        byte[] textBytes;
        if (IsPlaintext(payload))
        {
            textBytes = payload.ToArray();
        }
        else if (payload.Length > 0 && payload.Length % 16 == 0)
        {
            textBytes = AesCipher.Decrypt(payload);
        }
        else
        {
            textBytes = payload.ToArray();
        }

        return Encoding.UTF8.GetString(textBytes).TrimEnd('\0');
    }

    public static bool TryGetAlgorithmType(string text, out int algorithmType)
    {
        algorithmType = -1;
        var fields = text.Split(">>");
        if (fields.Length <= 6 || fields[0] != Protocol.IdentityType)
            return false;
        if (!int.TryParse(fields[6], out algorithmType))
        {
            algorithmType = -1;
            return false;
        }

        return true;
    }

    public static HandshakeInfo Interpret(ReadOnlySpan<byte> frame1, ReadOnlySpan<byte> frame2)
    {
        var algorithm = -1;
        string? capability = null;
        foreach (var frame in new[] { frame1.ToArray(), frame2.ToArray() })
        {
            var text = Decode(frame);
            if (!TryGetAlgorithmType(text, out var parsed))
                continue;
            algorithm = parsed;
            capability = text;
        }

        return new HandshakeInfo(algorithm, capability);
    }
}
