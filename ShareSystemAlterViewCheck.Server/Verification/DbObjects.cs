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

    /// <summary>差分更新用のステージングテーブル</summary>
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

    /// <summary>公開方式ごとの公開オブジェクト名（読み取り側が参照する名前）</summary>
    public static string PublicObjectOf(PublishMode mode) => mode switch
    {
        PublishMode.Table => PublicTable,
        PublishMode.View => PublicView,
        PublishMode.Synonym => PublicSynonym,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static (string Blue, string Green) BlueGreenOf(PublishMode mode) => mode switch
    {
        PublishMode.View => (ViewBlue, ViewGreen),
        PublishMode.Synonym => (SynBlue, SynGreen),
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

    public static Task<string?> GetActiveTableAsync(SqlConnection conn, PublishMode mode, CancellationToken ct) => mode switch
    {
        PublishMode.View => GetActiveViewTableAsync(conn, ct),
        PublishMode.Synonym => GetActiveSynonymTableAsync(conn, ct),
        _ => Task.FromResult<string?>(PublicTable),
    };
}
