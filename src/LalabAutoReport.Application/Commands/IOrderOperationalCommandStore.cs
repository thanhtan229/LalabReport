using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts.Commands;

namespace LalabAutoReport.ApplicationCore.Commands;

public sealed class OrderCommandResult
{
    public bool IsReplay { get; init; }
    public string ResultJson { get; init; } = string.Empty;
    public OrderFieldStateDto FieldState { get; init; } = default!;
}

public interface IOrderOperationalCommandStore
{
    Task<OrderCommandResult> ExecuteOrderOperationalCommandAsync(
        ActorContext actor,
        string capability,
        long orderId,
        string operationId,
        long expectedRevision,
        string payloadHash,
        object? desiredValue,
        ICommandPipelineObserver? observer = null,
        CancellationToken cancellationToken = default);
}
