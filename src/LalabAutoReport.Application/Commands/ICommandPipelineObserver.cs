using System.Threading.Tasks;

namespace LalabAutoReport.ApplicationCore.Commands;

public interface ICommandPipelineObserver
{
    Task OnBeforeTransactionAsync(string operationId, string capability) => Task.CompletedTask;
    Task OnAfterBeginTransactionAsync(string operationId, string capability) => Task.CompletedTask;
    Task OnAfterBusinessUpdateAsync(string operationId, string capability) => Task.CompletedTask;
    Task OnBeforeInsertReceiptAsync(string operationId, string capability) => Task.CompletedTask;
    Task OnBeforeCommitAsync(string operationId, string capability) => Task.CompletedTask;
    Task OnAfterCommitAsync(string operationId, string capability) => Task.CompletedTask;
}

public sealed class NullCommandPipelineObserver : ICommandPipelineObserver
{
    public static readonly NullCommandPipelineObserver Instance = new();
}
