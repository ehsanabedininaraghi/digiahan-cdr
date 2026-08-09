using DigiAhan.CDR.Receiver.Services;

static void Equal(string expected, string? actual, string name)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: expected '{expected}', actual '{actual}'.");
}

Equal("5522/45 *", DeliveryVoucherParser.Parse(" 5522/45 * "), "Raw accounting description");
Equal("شرح فروش - حواله: ۴۰۴۱۶ / ۲۰", DeliveryVoucherParser.Parse("شرح فروش - حواله: ۴۰۴۱۶ / ۲۰"), "Description preserved");
if (DeliveryVoucherParser.Parse("   ") is not null)
    throw new InvalidOperationException("Blank description must not become a voucher.");

var tokenA = PublicTokenService.Create();
var tokenB = PublicTokenService.Create();
if (!PublicTokenService.IsWellFormed(tokenA) || tokenA == tokenB)
    throw new InvalidOperationException("Secure public token generation failed.");
if (PublicTokenService.Hash(tokenA).SequenceEqual(PublicTokenService.Hash(tokenB)))
    throw new InvalidOperationException("Different tokens must not have the same hash.");

Equal("09121234567", MappingValueNormalizer.Phone("+98 912 123 4567"), "Iran mobile normalization");
Console.WriteLine("v4.3.8 smoke tests passed.");
