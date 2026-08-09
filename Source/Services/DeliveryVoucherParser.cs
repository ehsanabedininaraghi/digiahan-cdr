namespace DigiAhan.CDR.Receiver.Services;

public static class DeliveryVoucherParser
{
    public static string? Parse(string? description)
    {
        // The live accounting system stores the delivery reference as the full
        // non-empty factor description (for example: "5522/45 *").
        // Keep that text intact; a literal "حواله" label is not required.
        return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }
}
