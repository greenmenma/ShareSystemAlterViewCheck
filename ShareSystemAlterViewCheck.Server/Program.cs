using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using ShareSystemAlterViewCheck.Server.Endpoints;
using ShareSystemAlterViewCheck.Server.Infrastructure;
using ShareSystemAlterViewCheck.Server.Verification;

var builder = WebApplication.CreateBuilder(args);

// JSON 設定（列挙型は文字列、日本語はエスケープしない）
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
};
jsonOptions.Converters.Add(new JsonStringEnumConverter());
builder.Services.AddSingleton(jsonOptions);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Encoder = jsonOptions.Encoder;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<HostRedirectOptions>(builder.Configuration.GetSection("HostRedirect"));
builder.Services.Configure<VerificationOptions>(builder.Configuration.GetSection("Verification"));
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<RunManager>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<DatabaseBootstrapper>();

var app = builder.Build();

// HTTPS 強制 + ドメイン補完（IIS のサブフォルダでは PathBase が自動設定される）
app.UseMiddleware<HostRedirectMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseDefaultFiles();
app.MapStaticAssets();

app.MapVerificationApi();

app.MapFallbackToFile("/index.html");

app.Run();
