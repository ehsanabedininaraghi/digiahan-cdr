using DigiAhan.CDR.Receiver.Services;
using System.Reflection;

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

var normalizeSellerPhone = typeof(SellerWorkspaceRepository).GetMethod(
    "NormalizePhone", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Seller phone normalizer was not found.");
Equal("09121234567", normalizeSellerPhone.Invoke(null, new object?[] { "۰۹۱۲ ۱۲۳ ۴۵۶۷" }) as string,
    "Seller Persian phone normalization");
Equal("09121234567", normalizeSellerPhone.Invoke(null, new object?[] { "+98 (912) 123-4567" }) as string,
    "Seller international phone normalization");
Equal("09121234567", normalizeSellerPhone.Invoke(null, new object?[] { "9121234567" }) as string,
    "Seller trunk prefix normalization");

var localLookingTimestamp = DateTime.UtcNow.AddMinutes(210).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
var normalizedTimestamp = TehranClock.NormalizeIncomingEventUtc(localLookingTimestamp);
if (Math.Abs((normalizedTimestamp - DateTime.UtcNow).TotalMinutes) > 2)
    throw new InvalidOperationException("Tehran-local event timestamp was not normalized to UTC.");

var explicitUtc = DateTime.UtcNow.AddMinutes(-1);
var normalizedUtc = TehranClock.NormalizeIncomingEventUtc(explicitUtc.ToString("O"));
if (Math.Abs((normalizedUtc - explicitUtc).TotalSeconds) > 2)
    throw new InvalidOperationException("Explicit UTC timestamp changed during normalization.");

Console.WriteLine("v4.3.12 smoke tests passed.");
