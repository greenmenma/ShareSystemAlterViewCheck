using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>検証用 DB オブジェクトの名前と DDL</summary>
public static class DbObjects
{
    public const string Schema = "dbo";

    /// <summary>連携元データ（各システムから取得したデータの代わり）</summary>
    public const string Source = "vt_Source";

    // 現行方式（テーブル公開）
    public const string PublicTable = "vt_PublicTable";
    public const string PublicTableNew = "vt_PublicTable_New";

    // 改修後（ビュー公開 Blue/Green）
    public const string PublicView = "vt_PublicView";
    public const string ViewBlue = "vt_View_Blue";
    public const string ViewGreen = "vt_View_Green";

    // 参考（シノニム公開 Blue/Green）
    public const string PublicSynonym = "vt_PublicSynonym";
    public const string SynBlue = "vt_Syn_Blue";
    public const string SynGreen = "vt_Syn_Green";

    // 代替案：ALTER TABLE SWITCH（テーブル公開のまま入れ替え）
    public const string SwPublic = "vt_SwPublic";
    public const string SwStage = "vt_SwStage";
    public const string SwOld = "vt_SwOld";

    // 代替案：制御テーブル切替（ビュー定義は固定、制御テーブルの値で参照先を切替）
    public const string CtlView = "vt_CtlView";
    public const string CtlA = "vt_Ctl_A";
    public const string CtlB = "vt_Ctl_B";
    public const string CtlActive = "vt_CtlActive";

    /// <summary>非アクティブ側を SWITCH で退避して TRUNCATE するための空テーブル（読み取りから参照されない）</summary>
    public const string CtlTrash = "vt_Ctl_Trash";

    /// <summary>差分更新・全件同期用のステージングテーブル</summary>
    public const string DiffStage = "vt_DiffStage";

    public const string Columns = "Id, Code, Name, Amount, UpdatedAt, Payload";

    public static string Q(string name) => $"[{Schema}].[{name}]";

    public static string CreateDataTableSql(string name) => $"""
        CREATE TABLE {Q(name)} (
            Id        int            NOT NULL PRIMARY KEY CLUSTERED,
            Code      nvarchar(20)   NOT NULL,
            Name      nvarchar(100)  NOT NULL,
            Amount    decimal(18, 2) NOT NULL,
            UpdatedAt datetime2(3)   NOT NULL,
            Payload   nvarchar(400)  NOT NULL
        );
        """;

    public static string AlterViewSql(string table) =>
        $"ALTER VIEW {Q(PublicView)} AS SELECT {Columns} FROM {Q(table)};";

    public static string CreateViewSql(string table) =>
        $"CREATE VIEW {Q(PublicView)} AS SELECT {Columns} FROM {Q(table)};";

    /// <summary>制御テーブル（1 行だけ。ActiveSide = 'A' / 'B'）</summary>
    public static string CreateCtlActiveSql() => $"""
        CREATE TABLE {Q(CtlActive)} (
            Id         int     NOT NULL PRIMARY KEY CLUSTERED CHECK (Id = 1),
            ActiveSide char(1) NOT NULL CHECK (ActiveSide IN ('A', 'B'))
        );
        INSERT INTO {Q(CtlActive)} (Id, ActiveSide) VALUES (1, 'A');
        """;

    /// <summary>
    /// 制御テーブル切替のビュー。定義は固定で、切替は制御テーブルの UPDATE（DML）だけで行う。
    /// UNION ALL の各側に「制御テーブルの値がその側か」をスカラーサブクエリで付ける。
    /// 利用者の WHERE・JOIN 条件が各テーブルまで押し込まれるのでインデックス（Seek）が効き、
    /// 制御テーブルの判定が先に行われるので、非アクティブ側は通常読まれない。
    /// 以前の CROSS APPLY 版（制御テーブルを 1 回だけ読む形）は条件が押し込まれず、毎回全件スキャンになった。
    /// 制御テーブルを各側で 1 回ずつ読むため、1 文の中で A/B が混ざらないことは RCSI（ステートメント単位のスナップショット）が前提。
    /// RCSI OFF や NOLOCK の読み取りでは、切替と重なると重複（両側）や 0 件になることがある（docs 参照）。
    /// </summary>
    public static string CreateCtlViewSql() => $"""
        CREATE VIEW {Q(CtlView)} AS
        SELECT {Columns} FROM {Q(CtlA)}
        WHERE (SELECT ActiveSide FROM {Q(CtlActive)} WHERE Id = 1) = 'A'
        UNION ALL
        SELECT {Columns} FROM {Q(CtlB)}
        WHERE (SELECT ActiveSide FROM {Q(CtlActive)} WHERE Id = 1) = 'B';
        """;

    /// <summary>公開方式ごとの公開オブジェクト名（読み取り側が参照する名前）</summary>
    public static string PublicObjectOf(PublishMode mode) => mode switch
    {
        PublishMode.Table => PublicTable,
        PublishMode.View => PublicView,
        PublishMode.Synonym => PublicSynonym,
        PublishMode.Switch => SwPublic,
        PublishMode.Control => CtlView,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static (string Blue, string Green) BlueGreenOf(PublishMode mode) => mode switch
    {
        PublishMode.View => (ViewBlue, ViewGreen),
        PublishMode.Synonym => (SynBlue, SynGreen),
        PublishMode.Control => (CtlA, CtlB),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>現在ビューが参照しているテーブル名</summary>
    public static async Task<string?> GetActiveViewTableAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand($"""
            SELECT TOP (1) d.referenced_entity_name
            FROM sys.sql_expression_dependencies d
            WHERE d.referencing_id = OBJECT_ID(N'{Q(PublicView)}')
            """, conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>現在シノニムが参照しているテーブル名</summary>
    public static async Task<string?> GetActiveSynonymTableAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT PARSENAME(base_object_name, 1) FROM sys.synonyms WHERE schema_id = SCHEMA_ID(@s) AND name = @n", conn);
        cmd.Parameters.AddWithValue("@s", Schema);
        cmd.Parameters.AddWithValue("@n", PublicSynonym);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>現在制御テーブルが指している側のテーブル名</summary>
    public static async Task<string?> GetActiveControlTableAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand($"""
            IF OBJECT_ID(N'{Q(CtlActive)}', N'U') IS NOT NULL
                SELECT CASE ActiveSide WHEN 'A' THEN N'{CtlA}' ELSE N'{CtlB}' END FROM {Q(CtlActive)} WHERE Id = 1;
            """, conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public static Task<string?> GetActiveTableAsync(SqlConnection conn, PublishMode mode, CancellationToken ct) => mode switch
    {
        PublishMode.View => GetActiveViewTableAsync(conn, ct),
        PublishMode.Synonym => GetActiveSynonymTableAsync(conn, ct),
        PublishMode.Control => GetActiveControlTableAsync(conn, ct),
        PublishMode.Switch => Task.FromResult<string?>(SwPublic),
        _ => Task.FromResult<string?>(PublicTable),
    };

    /// <summary>復旧モデル</summary>
    public static async Task<string?> GetRecoveryModelAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT recovery_model_desc FROM sys.databases WHERE database_id = DB_ID()", conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>ログファイルの書込バイト数（累計）とサイズ（MB）。VIEW SERVER STATE がない場合は書込量が null</summary>
    public static async Task<(long? BytesWritten, double? SizeMb)> GetLogStatsAsync(SqlConnection conn, CancellationToken ct)
    {
        long? written = null;
        double? size = null;
        try
        {
            await using (var cmd = new SqlCommand(
                "SELECT SUM(CAST(size AS bigint)) * 8 / 1024.0 FROM sys.database_files WHERE type = 1", conn))
            {
                size = await cmd.ExecuteScalarAsync(ct) is decimal d ? (double)d : null;
            }
            await using (var cmd = new SqlCommand("""
                SELECT SUM(v.num_of_bytes_written)
                FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL) v
                JOIN sys.database_files f ON f.file_id = v.file_id
                WHERE f.type = 1
                """, conn))
            {
                written = await cmd.ExecuteScalarAsync(ct) is long w ? w : null;
            }
        }
        catch (SqlException)
        {
            // 権限不足など。ログ量は表示しない
        }
        return (written, size);
    }

    /// <summary>READ_COMMITTED_SNAPSHOT（RCSI）の設定</summary>
    public static async Task<bool?> GetReadCommittedSnapshotAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()", conn);
        return await cmd.ExecuteScalarAsync(ct) is bool b ? b : null;
    }
}
