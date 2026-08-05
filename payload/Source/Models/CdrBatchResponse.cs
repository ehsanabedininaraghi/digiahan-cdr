namespace DigiAhan.CDR.Receiver.Models;

public sealed class CdrBatchResponse
{
    public Guid BatchId { get; set; }
    public int Received { get; set; }
    public int Inserted { get; set; }
    public int Duplicates { get; set; }
    public int Errors { get; set; }
    public List<string> ErrorMessages { get; set; } = [];
}
