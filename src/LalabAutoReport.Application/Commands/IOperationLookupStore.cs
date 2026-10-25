using System.Threading;
using System.Threading.Tasks;

namespace LalabAutoReport.ApplicationCore.Commands;

public enum OperationLookupStatus
{
    NotFound,
    Pending,
    Committed
}

public sealed record OperationLookupResult(
    OperationLookupStatus Status,
    string? Capability = null,
    string? EntityType = null,
    long? EntityId = null,
    string? ResultJson = null);

public interface IOperationLookupStore
{
    Task<OperationLookupResult> LookupOperationAsync(string operationId, CancellationToken cancellationToken = default);
}
