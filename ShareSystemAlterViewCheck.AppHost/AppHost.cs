using Microsoft.Extensions.Configuration;

// デバッグ用 Aspire AppHost
// ・AppHost の構成（appsettings / ユーザーシークレット）に ConnectionStrings:VerifyDb があれば既存の SQL Server を使う
// ・なければ SQL Server コンテナー（Docker / Podman が必要）を起動して使う
var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.ShareSystemAlterViewCheck_Server>("server")
    .WithExternalHttpEndpoints();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("VerifyDb")))
{
    var db = builder.AddConnectionString("VerifyDb");
    server.WithReference(db);
}
else
{
    var db = builder.AddSqlServer("sql")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume()
        .AddDatabase("VerifyDb", "ShareSystemVerify");
    server.WithReference(db).WaitFor(db);
}

builder.Build().Run();
