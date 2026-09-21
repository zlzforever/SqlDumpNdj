using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using MySqlConnector;

var options = ExportOptions.Parse(args);

if (string.IsNullOrWhiteSpace(options.ConnectionString))
{
    Console.Error.WriteLine("Missing MySQL connection string. Use --connection or MYSQL_CONNECTION_STRING.");
    return 2;
}

Directory.CreateDirectory(options.OutputDirectory);

var tables = options.TableNames
    .Select(tableName => new TableExport(tableName, $"{tableName}.ndjson.gz"))
    .ToArray();

await using var connection = new MySqlConnection(options.ConnectionString);
await connection.OpenAsync();

var allCountsMatched = true;
foreach (var table in tables)
{
    var outputPath = Path.Combine(options.OutputDirectory, table.FileName);
    var countsMatched = await ExportTableAsync(connection, table.TableName, outputPath, options.BatchSize);
    allCountsMatched &= countsMatched;
}

if (!allCountsMatched)
{
    Console.Error.WriteLine("Export completed with row count errors.");
    return 1;
}

Console.WriteLine("Export completed and row counts matched.");
return 0;

static async Task<bool> ExportTableAsync(
    MySqlConnection connection,
    string tableName,
    string outputPath,
    int batchSize)
{
    var temporaryPath = outputPath + ".tmp";
    if (File.Exists(temporaryPath))
    {
        File.Delete(temporaryPath);
    }

    object? lastId = null;
    long rowCount = 0;

    try
    {
        await using (var file = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false))
        await using (var writer = new StreamWriter(gzip, new System.Text.UTF8Encoding(false), 64 * 1024))
        {
            while (true)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = lastId is null
                    ? $"SELECT * FROM `{tableName}` ORDER BY `id` LIMIT @batchSize"
                    : $"SELECT * FROM `{tableName}` WHERE `id` > @lastId ORDER BY `id` LIMIT @batchSize";

                if (lastId is not null)
                {
                    command.Parameters.AddWithValue("@lastId", lastId);
                }

                command.Parameters.AddWithValue("@batchSize", batchSize);

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                var batchCount = 0;

                while (await reader.ReadAsync())
                {
                    await WriteRowAsync(reader, writer);
                    lastId = reader.GetValue(reader.GetOrdinal("id"));
                    if (lastId is DBNull)
                    {
                        throw new InvalidOperationException($"{tableName}.id cannot be NULL for keyset pagination.");
                    }

                    batchCount++;
                    rowCount++;
                }

                await writer.FlushAsync();
                Console.WriteLine($"{tableName}: exported {rowCount:N0} rows");

                if (batchCount < batchSize)
                {
                    break;
                }
            }
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
        Console.WriteLine($"{tableName}: {outputPath}");

        var sourceRowCount = await CountRowsAsync(connection, tableName);
        if (sourceRowCount != rowCount)
        {
            Console.Error.WriteLine(
                $"ERROR: {tableName} row count mismatch. Exported {rowCount:N0}, database has {sourceRowCount:N0} rows.");
            return false;
        }

        Console.WriteLine($"{tableName}: row count matched ({rowCount:N0})");
        return true;
    }
    catch
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        throw;
    }
}

static async Task<long> CountRowsAsync(MySqlConnection connection, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM `{tableName}`";
    var value = await command.ExecuteScalarAsync();
    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

static async Task WriteRowAsync(DbDataReader reader, TextWriter writer)
{
    await using var buffer = new MemoryStream();
    await using (var jsonWriter = new Utf8JsonWriter(buffer))
    {
        jsonWriter.WriteStartObject();

        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            var columnName = reader.GetName(ordinal);
            jsonWriter.WritePropertyName(columnName);

            if (await reader.IsDBNullAsync(ordinal))
            {
                jsonWriter.WriteNullValue();
                continue;
            }

            WriteJsonValue(jsonWriter, reader.GetValue(ordinal));
        }

        jsonWriter.WriteEndObject();
        await jsonWriter.FlushAsync();
    }

    await writer.WriteLineAsync(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
}

static void WriteJsonValue(Utf8JsonWriter writer, object value)
{
    switch (value)
    {
        case bool boolean:
            writer.WriteBooleanValue(boolean);
            break;
        case byte byteValue:
            writer.WriteNumberValue(byteValue);
            break;
        case sbyte sbyteValue:
            writer.WriteNumberValue(sbyteValue);
            break;
        case short shortValue:
            writer.WriteNumberValue(shortValue);
            break;
        case ushort ushortValue:
            writer.WriteNumberValue(ushortValue);
            break;
        case int intValue:
            writer.WriteNumberValue(intValue);
            break;
        case uint uintValue:
            writer.WriteNumberValue(uintValue);
            break;
        case long longValue:
            writer.WriteNumberValue(longValue);
            break;
        case ulong ulongValue:
            writer.WriteNumberValue(ulongValue);
            break;
        case float floatValue:
            writer.WriteNumberValue(floatValue);
            break;
        case double doubleValue:
            writer.WriteNumberValue(doubleValue);
            break;
        case decimal decimalValue:
            writer.WriteNumberValue(decimalValue);
            break;
        case DateTime dateTime:
            writer.WriteStringValue(dateTime.ToString("O", CultureInfo.InvariantCulture));
            break;
        case DateTimeOffset dateTimeOffset:
            writer.WriteStringValue(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
            break;
        case TimeSpan timeSpan:
            writer.WriteStringValue(timeSpan.ToString());
            break;
        case byte[] bytes:
            writer.WriteBase64StringValue(bytes);
            break;
        default:
            writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
            break;
    }
}

sealed record TableExport(string TableName, string FileName);

sealed record ExportOptions(
    string ConnectionString,
    string OutputDirectory,
    int BatchSize,
    IReadOnlyList<string> TableNames)
{
    private const int DefaultBatchSize = 1_000;
    private static readonly string[] DefaultTableNames = ["res_expert", "res_employment_history"];

    public static ExportOptions Parse(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING") ?? "";
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "export");
        var batchSize = DefaultBatchSize;
        var tableNames = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length:
                    connectionString = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    outputDirectory = args[++i];
                    break;
                case "--batch-size" when i + 1 < args.Length && int.TryParse(args[++i], out var parsedBatchSize):
                    batchSize = parsedBatchSize;
                    break;
                case "--table" when i + 1 < args.Length:
                    var tableName = args[++i];
                    if (!IsValidTableName(tableName))
                    {
                        throw new ArgumentException(
                            $"Invalid table name '{tableName}'. Use letters, digits, and underscores only.");
                    }

                    if (!tableNames.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                    {
                        tableNames.Add(tableName);
                    }

                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option: {args[i]}");
            }
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero.");
        }

        if (tableNames.Count == 0)
        {
            tableNames.AddRange(DefaultTableNames);
        }

        return new ExportOptions(connectionString, Path.GetFullPath(outputDirectory), batchSize, tableNames);
    }

    private static void PrintHelp() => Console.WriteLine(
        "Usage: dotnet run -- --connection <connection-string> [--table <name>]... [--output <directory>] [--batch-size <n>]\n" +
        "       Or set MYSQL_CONNECTION_STRING. Default tables: res_expert, res_employment_history.\n" +
        "       Default output directory: ./export");

    private static bool IsValidTableName(string tableName) =>
        !string.IsNullOrWhiteSpace(tableName) &&
        tableName.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
