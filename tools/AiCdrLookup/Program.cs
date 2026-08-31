using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

if (args.Length == 0)
    throw new ArgumentException("Pass one or more recording file names.");

var connectionString = Environment.GetEnvironmentVariable("DIGIAHAN_CDR_CONNECTION")
    ?? "Server=lpc:localhost;Database=DigiAhan_CDR;Integrated Security=True;TrustServerCertificate=True;";

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
await using var command = connection.CreateCommand();
command.CommandTimeout = 15;

var parameterNames = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    var name = $"@recording{i}";
    parameterNames.Add(name);
    command.Parameters.Add(name, SqlDbType.NVarChar, 500).Value = Path.GetFileName(args[i]);
}

command.CommandText = $$"""
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
    WITH ranked AS
    (
        SELECT RecordingFile,LinkedId,Calldate,Src,Dst,Did,Cnum,OutboundCnum,
               Channel,DstChannel,Disposition,Duration,Billsec,
               COUNT(*) OVER(PARTITION BY RecordingFile) AS LegCount,
               ROW_NUMBER() OVER
               (
                   PARTITION BY RecordingFile
                   ORDER BY ISNULL(Billsec,0) DESC,ISNULL(Duration,0) DESC,RawCDRId DESC
               ) AS rn
        FROM dbo.RawCDR
        WHERE RecordingFile IN ({{string.Join(",", parameterNames)}})
    )
    SELECT RecordingFile,LinkedId,Calldate,Src,Dst,Did,Cnum,OutboundCnum,
           Channel,DstChannel,Disposition,Duration,Billsec,LegCount
    FROM ranked WHERE rn=1 ORDER BY Calldate;
    """;

var results = new List<object>();
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    results.Add(new
    {
        recordingFile = GetString(reader, 0),
        linkedId = GetString(reader, 1),
        callDate = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
        src = GetString(reader, 3),
        dst = GetString(reader, 4),
        did = GetString(reader, 5),
        cnum = GetString(reader, 6),
        outboundCnum = GetString(reader, 7),
        channel = GetString(reader, 8),
        dstChannel = GetString(reader, 9),
        disposition = GetString(reader, 10),
        duration = reader.IsDBNull(11) ? (int?)null : reader.GetInt32(11),
        billsec = reader.IsDBNull(12) ? (int?)null : reader.GetInt32(12),
        legCount = reader.GetInt32(13)
    });
}

Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

static string? GetString(SqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
