using System.Security.Cryptography;

namespace UnifiedDirectoryManager.Services;

/// <summary>Generates random passwords that satisfy typical AD complexity rules.</summary>
public static class PasswordGenerator
{
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";      // no l/o (ambiguous)
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";       // no I/O
    private const string Digits = "23456789";                     // no 0/1
    private const string Special = "!@#$%^&*()-_=+[]{};:,.?";      // no spaces

    /// <summary>
    /// Returns a password of random length in [minLength, maxLength] containing at least one lower,
    /// upper, digit and special character, and no spaces.
    /// </summary>
    public static string Generate(int minLength = 12, int maxLength = 15)
    {
        if (minLength < 4) minLength = 4;
        if (maxLength < minLength) maxLength = minLength;

        var length = RandomNumberGenerator.GetInt32(minLength, maxLength + 1);
        const string all = Lower + Upper + Digits + Special;

        // Guarantee one of each required class, then fill the rest from the full set.
        var chars = new List<char>(length)
        {
            Pick(Lower), Pick(Upper), Pick(Digits), Pick(Special),
        };
        while (chars.Count < length)
            chars.Add(Pick(all));

        // Fisher–Yates shuffle so the guaranteed characters aren't always first.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}
