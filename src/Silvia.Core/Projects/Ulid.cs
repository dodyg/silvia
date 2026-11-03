using System.Security.Cryptography;

namespace Silvia.Core.Projects;

/// <summary>
/// Sortable, URL-safe identifiers (26 characters, Crockford base32): 48 bits of millisecond
/// timestamp followed by 80 random bits. Project ids are ULIDs, so they are also folder names.
/// </summary>
internal static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset? at = null)
    {
        var millis = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)(millis >> 40);
        bytes[1] = (byte)(millis >> 32);
        bytes[2] = (byte)(millis >> 24);
        bytes[3] = (byte)(millis >> 16);
        bytes[4] = (byte)(millis >> 8);
        bytes[5] = (byte)millis;
        RandomNumberGenerator.Fill(bytes[6..]);

        // 128 bits as 26 base32 digits (130 bits, the top two are zero).
        var chars = new char[26];
        ulong high = ((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) | ((ulong)bytes[2] << 40) |
                     ((ulong)bytes[3] << 32) | ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) |
                     ((ulong)bytes[6] << 8) | bytes[7];
        ulong low = ((ulong)bytes[8] << 56) | ((ulong)bytes[9] << 48) | ((ulong)bytes[10] << 40) |
                    ((ulong)bytes[11] << 32) | ((ulong)bytes[12] << 24) | ((ulong)bytes[13] << 16) |
                    ((ulong)bytes[14] << 8) | bytes[15];

        // Peel off 5 bits at a time from the least significant end: low 64 bits, then high 64.
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(low & 31)];
            low = (low >> 5) | ((high & 31) << 59);
            high >>= 5;
        }

        return new string(chars);
    }
}
