using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IdempotentPayments.Infrastructure.Services;

public static class RequestFingerprint
{
    public static string Compute(decimal amount, string currency)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{amount:F2}|{currency.ToUpperInvariant()}"
        );
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
