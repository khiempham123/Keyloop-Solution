using MockServiceApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ChaosState>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "MockServiceApi" }));

app.MapGet("/admin/chaos", (ChaosState chaos) => Results.Ok(chaos.Current));

app.MapPost("/admin/chaos", (ChaosConfigRequest request, ChaosState chaos) =>
{
    var mode = (request.Mode ?? "normal").Trim().ToLowerInvariant();
    if (mode is not ("normal" or "slow" or "fail" or "garbage"))
    {
        return Results.BadRequest(new { error = "mode must be one of: normal, slow, fail, garbage" });
    }

    var updated = new ChaosSnapshot(mode, request.DelayMs ?? 8000);
    chaos.Update(updated);
    return Results.Ok(updated);
});

app.MapGet("/service/v2/vehicles/{vin}/docs", async (string vin, ChaosState chaos, CancellationToken ct) =>
{
    var snapshot = chaos.Current;

    switch (snapshot.Mode)
    {
        case "slow":
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, snapshot.DelayMs)), ct);
            break;
        case "fail":
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        case "garbage":
            return Results.Content(
                """{"data":{"items":[{"id":99999,"category":"job-card"}]},"meta":{"count":1}}""",
                "application/json",
                statusCode: StatusCodes.Status200OK);
    }

    var items = SeedData.GetForVin(vin);
    return Results.Ok(new ServiceDocumentsEnvelope(
        new ServiceDataDto(items),
        new ServiceMetaDto(items.Count)));
});

app.Run();

public sealed record ChaosConfigRequest(string? Mode, int? DelayMs);

public sealed record ChaosSnapshot(string Mode, int DelayMs);

public sealed class ChaosState
{
    private volatile ChaosSnapshot _current = new("normal", 8000);

    public ChaosSnapshot Current => _current;

    public void Update(ChaosSnapshot next) => _current = next;
}
