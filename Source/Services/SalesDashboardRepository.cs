using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class SalesDashboardRepository
{
    private readonly string _connectionString;

    public SalesDashboardRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
    }

    public async Task<SalesDashboardSummary> Summary(DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        var start = ToPersianDate(startDate.Date);
        var end = ToPersianDate(endDate.Date);

        const string sql = """
        SELECT
            CAST(CASE WHEN EXISTS
            (
                SELECT 1 FROM dbo.AccountingSyncRuns WHERE Status=N'SUCCESS'
            ) THEN 1 ELSE 0 END AS bit) AS Connected,

            (SELECT TOP(1) FinishedAtUtc
             FROM dbo.AccountingSyncRuns
             WHERE Status=N'SUCCESS'
             ORDER BY FinishedAtUtc DESC) AS LastSyncAtUtc,

            (SELECT TOP(1) Status
             FROM dbo.AccountingSyncRuns
             ORDER BY StartedAtUtc DESC) AS LastSyncStatus,

            COUNT_BIG(*) AS InvoiceCount,
            COUNT(DISTINCT NULLIF(CustomerDetailCode,N'')) AS CustomerCount,
            ISNULL(SUM(Amount),0) AS TotalSales,
            ISNULL(AVG(NULLIF(Amount,0)),0) AS AverageInvoice,
            COUNT(DISTINCT VisitorId) AS VisitorCount,
            ISNULL(MAX(SourceDatabase),N'daftar1405') AS SourceDatabase,
            ISNULL(MAX(FiscalYear),1405) AS FiscalYear,
            MAX(FactorDate) AS LatestFactorDate
        FROM dbo.AccountingInvoices
        WHERE FactorDate>=@start AND FactorDate<=@end;
        """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@start", SqlDbType.NVarChar, 10).Value = start;
        command.Parameters.Add("@end", SqlDbType.NVarChar, 10).Value = end;
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return new SalesDashboardSummary(false, null, null, 0, 0, 0, 0, 0, "daftar1405", 1405, null);

        return new SalesDashboardSummary(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Convert.ToInt32(reader.GetInt64(3)),
            reader.GetInt32(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    public async Task<IReadOnlyList<SalesByVisitorRow>> ByVisitor(DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        var start = ToPersianDate(startDate.Date);
        var end = ToPersianDate(endDate.Date);

        const string sql = """
        SELECT
            v.VisitorId,
            ISNULL(NULLIF(v.VisitorName,N''),N'نامشخص') AS VisitorName,
            ISNULL(v.RoleType,N'SALES') AS RoleType,
            v.IsActive,
            COUNT(i.FactorCode) AS InvoiceCount,
            ISNULL(SUM(i.Amount),0) AS TotalSales,
            ISNULL(AVG(NULLIF(i.Amount,0)),0) AS AverageInvoice
        FROM dbo.AccountingVisitors v
        LEFT JOIN dbo.AccountingInvoices i
          ON i.SourceDatabase=v.SourceDatabase
         AND i.FiscalYear=v.FiscalYear
         AND i.VisitorId=v.VisitorId
         AND i.FactorDate>=@start AND i.FactorDate<=@end
        GROUP BY v.VisitorId,v.VisitorName,v.RoleType,v.IsActive
        ORDER BY TotalSales DESC,v.VisitorId;
        """;

        var result = new List<SalesByVisitorRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@start", SqlDbType.NVarChar, 10).Value = start;
        command.Parameters.Add("@end", SqlDbType.NVarChar, 10).Value = end;
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new SalesByVisitorRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetInt32(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6)));
        }

        return result;
    }

    public async Task<IReadOnlyList<RecentInvoiceRow>> RecentInvoices(
        DateTime startDate,
        DateTime endDate,
        int take,
        CancellationToken ct)
    {
        take = Math.Clamp(take,1,100);
        var start = ToPersianDate(startDate.Date);
        var end = ToPersianDate(endDate.Date);

        const string sql = """
        SELECT TOP(@take)
            i.FactorCode,
            i.FactorNumber,
            i.FactorDate,
            i.CustomerDetailCode,
            i.CustomerName,
            ISNULL(i.Amount,0) AS Amount,
            i.VisitorId,
            i.VisitorName,
            COUNT(ii.ItemCode) AS ItemCount
        FROM dbo.AccountingInvoices i
        LEFT JOIN dbo.AccountingInvoiceItems ii
          ON ii.SourceDatabase=i.SourceDatabase
         AND ii.FiscalYear=i.FiscalYear
         AND ii.FactorCode=i.FactorCode
        WHERE i.FactorDate>=@start AND i.FactorDate<=@end
        GROUP BY
            i.FactorCode,i.FactorNumber,i.FactorDate,i.CustomerDetailCode,
            i.CustomerName,i.Amount,i.VisitorId,i.VisitorName
        ORDER BY i.FactorDate DESC,i.FactorCode DESC;
        """;

        var result = new List<RecentInvoiceRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add("@start", SqlDbType.NVarChar, 10).Value = start;
        command.Parameters.Add("@end", SqlDbType.NVarChar, 10).Value = end;
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new RecentInvoiceRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    private static string ToPersianDate(DateTime date)
    {
        var calendar = new PersianCalendar();
        return $"{calendar.GetYear(date):0000}/{calendar.GetMonth(date):00}/{calendar.GetDayOfMonth(date):00}";
    }
}
