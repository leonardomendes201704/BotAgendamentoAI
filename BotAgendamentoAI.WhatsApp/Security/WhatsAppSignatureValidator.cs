using System.Security.Cryptography;
using System.Text;

namespace BotAgendamentoAI.WhatsApp.Security;

public static class WhatsAppSignatureValidator
{
    public static bool IsValid(string payload, string signatureHeader, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(payload)
            || string.IsNullOrWhiteSpace(signatureHeader)
            || string.IsNullOrWhiteSpace(appSecret))
        {
            return false;
        }

        var normalizedHeader = signatureHeader.Trim();
        if (!normalizedHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = $"sha256={Convert.ToHexString(hashBytes).ToLowerInvariant()}";

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(normalizedHeader.ToLowerInvariant()));
    }
}
