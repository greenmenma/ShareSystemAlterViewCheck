using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>
/// デバッグ用：接続文字列のデータベースが存在しなければ作成する（Docker の SQL Server を想定）。
/// Verification:CreateDatabaseIfNotExists が true のときだけ動作する。
/// </summary>
public sealed class DatabaseBootstrapper(IOptions<VerificationOptions> options, ILogger<DatabaseBootstrapper> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _done;

    public async Task EnsureAsync(string connectionString, CancellationToken ct)
    {
        if (_done || !options.Value.CreateDatabaseIfNotExists)
        {
            return;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_done)
            {
                return;
            }

            var builder = new SqlConnectionStringBuilder(connectionString);
            var database = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(database))
            {
                _done = true;
                return;
            }

            // master に接続して作成する
            builder.InitialCatalog = "master";
            builder.ConnectTimeout = Math.Min(builder.ConnectTimeout, 15);
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "IF DB_ID(@db) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC (@sql); SELECT 1; END ELSE SELECT 0;",
                conn);
            cmd.Parameters.AddWithValue("@db", database);
            var created = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
            if (created)
            {
                logger.LogInformation("データベース {Database} を作成しました", database);
            }
            _done = true;
        }
        finally
        {
            _lock.Release();
        }
    }
}
