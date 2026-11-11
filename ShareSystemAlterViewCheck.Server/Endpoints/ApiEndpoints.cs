using ShareSystemAlterViewCheck.Server.Verification;

namespace ShareSystemAlterViewCheck.Server.Endpoints;

public static class ApiEndpoints
{
    public static void MapVerificationApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // 環境の状態
        api.MapGet("/environment", (SetupService setup, CancellationToken ct) => setup.GetStatusAsync(ct));

        // 環境セットアップ（検証用オブジェクトの作り直し）
        api.MapPost("/environment/setup", (SetupRequest req, SetupService setup) =>
        {
            var (ctx, error) = setup.StartSetup(req);
            return ctx is null ? Results.Conflict(new { message = error }) : Results.Ok(new { id = ctx.Record.Id });
        });

        // 検証用オブジェクトの削除
        api.MapPost("/environment/drop", (SetupService setup) =>
        {
            var (ctx, error) = setup.StartDrop();
            return ctx is null ? Results.Conflict(new { message = error }) : Results.Ok(new { id = ctx.Record.Id });
        });

        // 検証ログ一覧
        api.MapGet("/runs", (RunStore store, RunManager manager) =>
        {
            var list = store.List().ToList();
            var current = manager.Current;
            if (current is not null && list.All(x => x.Id != current.Record.Id))
            {
                RunListItem? item = null;
                current.Mutate(r => item = RunListItem.From(r));
                list.Insert(0, item!);
            }
            return Results.Ok(list);
        });

        // 検証ログ詳細（実行中はメモリ上の最新状態を返す）
        api.MapGet("/runs/{id}", async (string id, RunStore store, RunManager manager) =>
        {
            var current = manager.Current;
            if (current is not null && current.Record.Id == id)
            {
                return Results.Bytes(current.Serialize(manager.Json), "application/json");
            }
            try
            {
                var bytes = await store.ReadAsync(id);
                return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "application/json");
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });

        // 検証の開始
        api.MapPost("/runs", (RunRequest req, RunManager manager) =>
        {
            var ctx = manager.Start(req);
            return ctx is null
                ? Results.Conflict(new { message = "他の検証が実行中です。終了してから実行してください。" })
                : Results.Ok(new { id = ctx.Record.Id });
        });

        // 検証のキャンセル
        api.MapPost("/runs/{id}/cancel", (string id, RunManager manager) =>
            manager.Cancel(id) ? Results.Ok() : Results.NotFound());

        // 検証ログの削除
        api.MapDelete("/runs/{id}", (string id, RunStore store, RunManager manager) =>
        {
            if (manager.Current?.Record.Id == id)
            {
                return Results.Conflict(new { message = "実行中のログは削除できません。" });
            }
            try
            {
                return store.Delete(id) ? Results.Ok() : Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });
    }
}
