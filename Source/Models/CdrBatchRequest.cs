namespace DigiAhan.CDR.Receiver.Models;

public sealed class CdrBatchRequest
{
    public string? Token { get; set; }
    public Guid? BatchId { get; set; }
    public string? SourceServer { get; set; }
    public List<CdrRecord> Records { get; set; } = [];
}
