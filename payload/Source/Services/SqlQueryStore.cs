using System.Collections.Concurrent;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class SqlQueryStore
{
    private readonly string _sqlDirectory;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SqlQueryStore(IWebHostEnvironment environment)
    {
        _sqlDirectory = Path.Combine(environment.ContentRootPath, "Sql");
        if (!Directory.Exists(_sqlDirectory))
            throw new DirectoryNotFoundException($"SQL query directory not found: {_sqlDirectory}");
    }

    public string Get(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("SQL file name is required.", nameof(fileName));

        return _cache.GetOrAdd(fileName, static (name, directory) =>
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"SQL query file not found: {path}", path);

            var sql = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(sql))
                throw new InvalidOperationException($"SQL query file is empty: {path}");

            return sql;
        }, _sqlDirectory);
    }
}
