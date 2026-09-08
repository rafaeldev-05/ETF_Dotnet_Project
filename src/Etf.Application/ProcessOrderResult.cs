using Etf.Domain;
namespace Etf.Application;

public enum SimulationOutcome { Executed, Rejected, TemporaryFailure }
public sealed record SimulationRequest(string? Outcome, decimal? ExecutionPrice);
public sealed record SimulationResponse(SimulationOutcome Outcome, OrderView Order);

// Deterministic demo input; no remote calls or waiting while holding database locks.
public sealed class ProcessOrderResult(IOrderStore store)
{
    public const string DemoRejectionReason = "demo_definitive_rejection";
    public async Task<SimulationResponse> Apply(Guid clientId, Guid orderId, SimulationRequest request, CancellationToken ct)
    {
        var outcome = request.Outcome switch
        {
            "Executed" => SimulationOutcome.Executed,
            "Rejected" => SimulationOutcome.Rejected,
            "TemporaryFailure" => SimulationOutcome.TemporaryFailure,
            _ => throw new RequestFailure(400, "invalid_simulation", "Outcome deve ser Executed, Rejected ou TemporaryFailure.")
        };
        if ((outcome == SimulationOutcome.Executed && (!request.ExecutionPrice.HasValue || !Money.ValidPrice(request.ExecutionPrice.Value))) ||
            (outcome != SimulationOutcome.Executed && request.ExecutionPrice.HasValue))
            throw new RequestFailure(400, "invalid_simulation", "Informe preço positivo com até duas casas somente para Executed.");
        try
        {
            var order = await store.ApplyResult(clientId, orderId, outcome, request.ExecutionPrice, ct);
            return new SimulationResponse(outcome, order);
        }
        catch (InvalidOrderData e) { throw new RequestFailure(400, e.Code, e.Message); }
        catch (OrderResultConflict e) { throw new RequestFailure(409, "order_result_conflict", e.Message); }
    }
}
