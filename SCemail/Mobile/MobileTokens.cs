using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SCemail.Mobile;

/// <summary>Generazione e hashing di codici di attivazione e token dei dispositivi.</summary>
public static class MobileTokens
{
    // Niente caratteri ambigui (0/O, 1/I/L): il codice viene digitato a mano sul telefono.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 12;

    /// <summary>Codice monouso nel formato XXXX-XXXX-XXXX (~59 bit di entropia).</summary>
    public static string NewActivationCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// <summary>Maiuscolo, senza spazi e trattini: "abcd-efgh ijkl" → "ABCDEFGHIJKL".</summary>
    public static string NormalizeActivationCode(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Token operativo del dispositivo: 256 bit casuali, base64url.</summary>
    public static string NewDeviceToken() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 esadecimale minuscolo. Sul DB si salvano solo gli hash.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
