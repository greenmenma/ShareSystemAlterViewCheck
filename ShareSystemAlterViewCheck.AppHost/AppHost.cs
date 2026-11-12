// デバッグ用 Aspire AppHost
// Server（ASP.NET Core）と Client（Vite 開発サーバー）を同時に起動する
// 検証用 SQL Server の接続文字列は Server 側の appsettings.json（ConnectionStrings:VerifyDb）に記載する
var builder = DistributedApplication.CreateBuilder(args);

// Aspire から起動するときは SpaProxy を無効にする（Vite は下の client リソースで起動するため）
var server = builder.AddProject<Projects.ShareSystemAlterViewCheck_Server>("server")
    .WithEnvironment("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "");

// Vite 開発サーバー。/api は vite.config.ts のプロキシで server に転送する
// Vite は vite.config.ts で開発証明書を使い HTTPS で待ち受けるので、エンドポイントも https にする
builder.AddViteApp("client", "../ShareSystemAlterViewCheck.Client")
    .WithEndpoint("http", e =>
    {
        e.UriScheme = "https";
        e.Port = 52731;
    })
    .WithReference(server)
    .WaitFor(server)
    .WithExternalHttpEndpoints();

builder.Build().Run();
