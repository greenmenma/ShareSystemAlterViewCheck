// デバッグ用 Aspire AppHost
// 検証用 SQL Server の接続文字列は Server 側の appsettings.json（ConnectionStrings:VerifyDb）に記載する
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.ShareSystemAlterViewCheck_Server>("server")
    .WithExternalHttpEndpoints();

builder.Build().Run();
