using DocumentViewer.Domain;
using Shouldly;

namespace DocumentViewer.UnitTests.Domain;

public sealed class VinTests
{
    [Theory]
    [InlineData("1HGCM82633A004352", "1HGCM82633A004352")]     // valid, check digit 3
    [InlineData(" 1hgcm82633a004352 ", "1HGCM82633A004352")]   // Review Focus #1: whitespace + lowercase
    [InlineData("1M8GDM9AXKP042788", "1M8GDM9AXKP042788")]     // valid, check digit X (remainder 10)
    public void TryParse_accepts_valid_vin(string input, string expectedNormalized)
    {
        Vin.TryParse(input, out var vin, out var error).ShouldBeTrue();
        error.ShouldBe(VinError.None);
        vin.Value.ShouldBe(expectedNormalized);
    }

    [Theory]
    [InlineData(null, VinError.Empty)]
    [InlineData("", VinError.Empty)]
    [InlineData("   ", VinError.Empty)]
    [InlineData("1HGCM82633A00435", VinError.InvalidLength)]      // 16
    [InlineData("1HGCM82633A0043521", VinError.InvalidLength)]     // 18
    [InlineData("1HGCM82633A0O4352", VinError.IllegalCharacter)]   // contains O
    [InlineData("1HGCM82633A0I4352", VinError.IllegalCharacter)]   // contains I
    [InlineData("1HGCM82633A0Q4352", VinError.IllegalCharacter)]   // contains Q
    [InlineData("1HGCM82633A004353", VinError.InvalidCheckDigit)]  // last digit altered
    public void TryParse_rejects_invalid_vin(string? input, VinError expected)
    {
        Vin.TryParse(input, out _, out var error).ShouldBeFalse();
        error.ShouldBe(expected);
    }

    [Fact]
    public void Vin_does_not_expose_raw_value_when_formatted()
    {
        Vin.TryParse("1HGCM82633A004352", out var vin, out _).ShouldBeTrue();
        vin.ToString().ShouldNotContain("1HGCM82633A004352");
        vin.ToString().ShouldBe("***004352");
        vin.Masked.ShouldBe("***004352");
        vin.Hash12.Length.ShouldBe(12);
        vin.Hash12.ShouldMatch("^[0-9a-f]{12}$");
    }
}
