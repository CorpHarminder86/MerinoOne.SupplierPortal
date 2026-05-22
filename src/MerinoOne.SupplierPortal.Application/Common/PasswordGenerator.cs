using System.Security.Cryptography;

namespace MerinoOne.SupplierPortal.Application.Common;

/// <summary>
/// Generates a 12-char one-time password using a curated alphabet that drops
/// ambiguous glyphs (0/O, 1/l/I) so users can read the OTP out of an email without
/// confusion. Guarantees at least one upper, one lower, and one digit.
/// </summary>
public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // dropped I, O
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";   // dropped l, o
    private const string Digits = "23456789";                  // dropped 0, 1

    private const int DefaultLength = 12;

    public static string Generate(int length = DefaultLength)
    {
        if (length < 8) length = 8;

        // Seed with one char from each class to satisfy the contract, then fill the rest.
        var pool = Upper + Lower + Digits;
        var chars = new char[length];

        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];

        for (var i = 3; i < length; i++)
            chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];

        // Fisher-Yates shuffle so the guaranteed positions are not fixed.
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }
}
