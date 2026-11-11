using System.Net;
using Microsoft.Extensions.Options;

namespace ShareSystemAlterViewCheck.Server.Infrastructure;

/// <summary>ホスト名リダイレクトの設定</summary>
public sealed class HostRedirectOptions
{
    /// <summary>リダイレクトを有効にするか</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ホスト名だけでアクセスされたときに付与するドメイン（例: example.local）</summary>
    public string? DefaultDomain { get; set; }

    /// <summary>HTTPS のポート番号（443 の場合は URL に付与しない）</summary>
    public int HttpsPort { get; set; } = 443;
}

/// <summary>
/// HTTPS 強制とドメイン補完を 1 回のリダイレクトで行うミドルウェア。
/// ・HTTP でのアクセス → HTTPS へリダイレクト
/// ・ドメインなしのホスト名（例: server01）→ 指定ドメインを付与（例: server01.example.local）
/// </summary>
public sealed class HostRedirectMiddleware(RequestDelegate next, IOptionsMonitor<HostRedirectOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var opt = options.CurrentValue;
        var request = context.Request;

        // サブフォルダ直下へのアクセス（末尾スラッシュなし）は、相対パスの資源が解決できるよう末尾に / を付与する
        var needSlash = request.PathBase.HasValue && !request.Path.HasValue && HttpMethods.IsGet(request.Method);

        var host = request.Host.Host;
        var newHost = host;
        if (opt.Enabled && !string.IsNullOrWhiteSpace(opt.DefaultDomain) && NeedsDomain(host))
        {
            newHost = $"{host}.{opt.DefaultDomain.Trim().TrimStart('.')}";
        }

        var needHttps = !request.IsHttps;
        if (!needHttps && newHost == host && !needSlash)
        {
            await next(context);
            return;
        }

        int? port = needHttps ? opt.HttpsPort : request.Host.Port;
        var hostString = port is null or 443 ? new HostString(newHost) : new HostString(newHost, port.Value);
        var path = needSlash ? request.PathBase.Add("/") : request.PathBase.Add(request.Path);
        var url = $"https://{hostString}{path}{request.QueryString}";

        // GET 以外も考慮してメソッドを保持する 307 を使う
        context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
        context.Response.Headers.Location = url;
    }

    /// <summary>ドメインが付いていないホスト名かどうか（localhost と IP アドレスは対象外）</summary>
    private static bool NeedsDomain(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Contains('.'))
        {
            return false;
        }
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // IPv6 などの IP アドレス表記は除外
        return !IPAddress.TryParse(host.Trim('[', ']'), out _);
    }
}
