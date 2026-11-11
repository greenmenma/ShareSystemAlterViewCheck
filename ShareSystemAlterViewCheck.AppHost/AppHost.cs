using Microsoft.Extensions.Configuration;

// デバッグ用 Aspire AppHost
// 検証用 SQL Server の種類を構成 Verification:SqlMode で切り替える
//   Docker   : SQL Server 2022 コンテナー（Docker / Podman が必要）を起動して使う（既定）
//   External : 既存の SQL Server（ConnectionStrings:VerifyDb）を使う
var builder = DistributedApplication.CreateBuilder(args);

var sqlMode = builder.Configuration["Verification:SqlMode"] ?? "Docker";

var server = builder.AddProject<Projects.ShareSystemAlterViewCheck_Server>("server")
    .WithExternalHttpEndpoints();

if (string.Equals(sqlMode, "External", StringComparison.OrdinalIgnoreCase))
{
    // 既存サーバー：AppHost のユーザーシークレット等に ConnectionStrings:VerifyDb を設定する
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("VerifyDb")))
    {
        throw new InvalidOperationException(
            "Verification:SqlMode=External の場合は AppHost の構成に ConnectionStrings:VerifyDb を設定してください。");
    }
    server.WithReference(builder.AddConnectionString("VerifyDb"));
}
else
{
    // Docker：sa パスワードは Parameters:sql-password（未設定なら自動生成してユーザーシークレットに保存）
    var password = builder.AddParameter("sql-password", secret: true);
    var hostPort = builder.Configuration.GetValue<int?>("Verification:SqlHostPort") ?? 14330;

    var sql = builder.AddSqlServer("sql", password, hostPort)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("sharesystemalterviewcheck-sqldata");

    // Apple Silicon などでは x64 イメージをエミュレーションで動かす
    if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
    {
        sql.WithContainerRuntimeArgs("--platform", "linux/amd64");
    }

    var db = sql.AddDatabase("VerifyDb", "ShareSystemVerify");
    server.WithReference(db).WaitFor(db);
}

builder.Build().Run();
