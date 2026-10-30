using System.Security.Cryptography;
using System.Text;

namespace DocumentViewer.Domain;

public readonly record struct Vin
{
    private static ReadOnlySpan<int> PositionalWeights => [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    private readonly string? _value;
    private readonly string? _hash12;
    private readonly string? _masked;

    private Vin(string value, string hash12, string masked)
    {
        _value = value;
        _hash12 = hash12;
        _masked = masked;
    }

    public string Value => _value ?? string.Empty;

    public string Hash12 => _hash12 ?? string.Empty;

    public string Masked => _masked ?? string.Empty;

    public static bool TryParse(string? input, out Vin vin, out VinError error)
    {
        vin = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = VinError.Empty;
            return false;
        }

        var normalized = input.Trim().ToUpperInvariant();
        if (normalized.Length != 17)
        {
            error = VinError.InvalidLength;
            return false;
        }

        var weights = PositionalWeights;
        var weightedSum = 0;

        for (var i = 0; i < 17; i++)
        {
            var transliterated = Transliterate(normalized[i]);
            if (transliterated < 0)
            {
                error = VinError.IllegalCharacter;
                return false;
            }

            weightedSum += transliterated * weights[i];
        }

        var remainder = weightedSum % 11;
        var expectedCheckDigit = remainder == 10 ? 'X' : (char)('0' + remainder);
        if (normalized[8] != expectedCheckDigit)
        {
            error = VinError.InvalidCheckDigit;
            return false;
        }

        var hashHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var hash12 = hashHex[..12];
        var masked = $"***{normalized[^6..]}";

        vin = new Vin(normalized, hash12, masked);
        error = VinError.None;
        return true;
    }

    public override string ToString() => Masked;

    private static int Transliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'H' => c - 'A' + 1,
        >= 'J' and <= 'N' => c - 'J' + 1,
        'P' => 7,
        'R' => 9,
        >= 'S' and <= 'Z' => c - 'S' + 2,
        _ => -1
    };
}
