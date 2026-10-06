using System.Security.Cryptography;

namespace TclRemote;

/// <summary>
/// AES-128-CBC with the fixed TCL key and IV. Each message is encrypted on its own.
/// Decrypt strips PKCS7 only when the padding bytes are valid.
/// </summary>
internal static class AesCipher
{
    internal static readonly byte[] Key = "tnscreentnscreen"u8.ToArray();
    internal static readonly byte[] Iv =
    [
        0x12, 0x34, 0x56, 0x78, 0x90, 0xAB, 0xCD, 0xEF,
        0x12, 0x34, 0x56, 0x78, 0x90, 0xAB, 0xCD, 0xEF
    ];

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        using var aes = Create();
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var input = plaintext.ToArray();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
            throw new ArgumentException("AES ciphertext length must be a positive multiple of 16.", nameof(ciphertext));

        using var aes = Create();
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var input = ciphertext.ToArray();
        var plain = decryptor.TransformFinalBlock(input, 0, input.Length);
        return StripPkcs7IfValid(plain);
    }

    internal static byte[] StripPkcs7IfValid(byte[] plain)
    {
        if (plain.Length == 0)
            return plain;

        var pad = plain[^1];
        if (pad is < 1 or > 16 || pad > plain.Length)
            return plain;

        for (var i = plain.Length - pad; i < plain.Length; i++)
        {
            if (plain[i] != pad)
                return plain;
        }

        var unpadded = new byte[plain.Length - pad];
        plain.AsSpan(0, unpadded.Length).CopyTo(unpadded);
        return unpadded;
    }

    private static Aes Create()
    {
        var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Key = Key;
        aes.IV = Iv;
        return aes;
    }
}
