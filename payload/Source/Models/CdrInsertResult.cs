namespace DigiAhan.CDR.Receiver.Models;

public sealed record CdrInsertResult(bool Inserted, long RawCdrId);
