namespace DigiAhan.CDR.Receiver.Models;

public sealed class CdrRecord
{
    public DateTime? Calldate { get; set; }
    public string? Clid { get; set; }
    public string? Src { get; set; }
    public string? Dst { get; set; }
    public string? Dcontext { get; set; }
    public string? Channel { get; set; }
    public string? DstChannel { get; set; }
    public string? LastApp { get; set; }
    public string? LastData { get; set; }
    public int? Duration { get; set; }
    public int? Billsec { get; set; }
    public string? Disposition { get; set; }
    public int? Amaflags { get; set; }
    public string? AccountCode { get; set; }
    public string? UniqueId { get; set; }
    public string? UserField { get; set; }
    public string? RecordingFile { get; set; }
    public string? Cnum { get; set; }
    public string? Cnam { get; set; }
    public string? OutboundCnum { get; set; }
    public string? OutboundCnam { get; set; }
    public string? DstCnam { get; set; }
    public string? Did { get; set; }
    public string? LinkedId { get; set; }
    public string? PeerAccount { get; set; }
    public int? SequenceNo { get; set; }
    public string? SourceRowKey { get; set; }
    public string? Fingerprint { get; set; }
}
