using System.Text.Json.Serialization;
using Etf.Application;
using Etf.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Fail closed until production authentication is implemented.
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Esta etapa só pode iniciar em Development. Autenticação de produção não implementada.");
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<TradingDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Trading")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__Trading.")));
builder.Services.AddScoped<IOrderStore, PostgresOrderStore>();
builder.Services.AddScoped<Orders>();
builder.Services.AddScoped<ProcessOrderResult>();
builder.Services.AddScoped<IOutboxStore, PostgresOutboxStore>();
builder.Services.AddSingleton<InMemoryBroker>();
builder.Services.AddSingleton<ILocalBroker>(sp => sp.GetRequiredService<InMemoryBroker>());
builder.Services.AddScoped<OutboxPublisher>();
builder.Services.AddScoped<OrderConsumer>();
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) => {
    try { await next(context); }
    catch (RequestFailure e) { await Results.Problem(statusCode: e.Status, title: e.Code, detail: e.Message,
        extensions: new Dictionary<string, object?> { ["code"] = e.Code }).ExecuteAsync(context); }
    catch (BadHttpRequestException) { await Results.Problem(statusCode: 400, title: "invalid_request", detail: "Corpo ou parâmetros inválidos.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" }).ExecuteAsync(context); }
});
app.MapGet("/etfs", async (IOrderStore store, CancellationToken ct) => Results.Ok(await store.Catalog(ct)));
app.MapPost("/orders", async (CreateOrderRequest request, HttpContext context, Orders orders, CancellationToken ct) => {
    var client = DemoClient(context);
    var keys = context.Request.Headers["Idempotency-Key"];
    var order = await orders.Create(client, keys.Count == 1 ? keys[0] : null, request, ct);
    return Results.Created($"/orders/{order.Id}", order);
});
app.MapGet("/orders/{id:guid}", async (Guid id, HttpContext context, IOrderStore store, CancellationToken ct) => {
    var order = await store.Find(DemoClient(context), id, ct);
    return order is null ? Results.Problem(statusCode: 404, title: "order_not_found", detail: "Ordem não encontrada.",
        extensions: new Dictionary<string, object?> { ["code"] = "order_not_found" }) : Results.Ok(order);
});
if (app.Environment.IsDevelopment())
{
    // Demo control surface only: not a production execution API.
    app.MapPost("/development/orders/{id:guid}/result", async (Guid id, SimulationRequest request,
        HttpContext context, ProcessOrderResult process, CancellationToken ct) =>
        Results.Ok(await process.Apply(DemoClient(context), id, request, ct)));
    app.MapPost("/development/outbox/publish", async (int? batchSize, OutboxPublisher publisher, CancellationToken ct) =>
        Results.Ok(new { published = await publisher.PublishBatch(batchSize ?? 10, 5, ct) }));
    app.MapGet("/development/outbox/messages", async (TradingDb db, CancellationToken ct) =>
        Results.Ok(await db.OutboxMessages.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct)));
    app.MapPost("/development/outbox/consume/{eventId:guid}", async (Guid eventId, SimulationRequest request,
        OrderConsumer consumer, TradingDb db, HttpContext context, CancellationToken ct) =>
    {
        var row = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == eventId, ct);
        if (row is null) return Results.NotFound();
        var message = System.Text.Json.JsonSerializer.Deserialize<OrderCreatedV1>(row.Payload)!;
        var outcome = request.Outcome switch
        {
            "Executed" => SimulationOutcome.Executed,
            "Rejected" => SimulationOutcome.Rejected,
            "TemporaryFailure" => SimulationOutcome.TemporaryFailure,
            _ => throw new RequestFailure(400, "invalid_simulation", "Outcome inválido.")
        };
        var processed = await consumer.Consume(message, outcome, request.ExecutionPrice, ct);
        return Results.Ok(new { processed, orderId = message.OrderId });
    });
}
if (args.Contains("--seed-demo"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await DemoData.Seed(scope.ServiceProvider.GetRequiredService<TradingDb>());
    return;
}
app.Run();
static Guid DemoClient(HttpContext context)
{
    var values = context.Request.Headers["X-Demo-Client-Id"];
    if (values.Count != 1 || !Guid.TryParse(values[0], out var id) || (id != DemoData.Alice && id != DemoData.Bob))
        throw new RequestFailure(401, "invalid_demo_identity", "Selecione Alice ou Bob usando X-Demo-Client-Id (somente Development).");
    return id;
}
public partial class Program { }
