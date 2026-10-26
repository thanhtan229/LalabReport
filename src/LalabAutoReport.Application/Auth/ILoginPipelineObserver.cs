using System.Threading;
using System.Threading.Tasks;

namespace LalabAutoReport.ApplicationCore.Auth;

/// <summary>
/// Hook invoked during the login pipeline between credential verification
/// and atomic session issuance. Used for deterministic interleaving tests (e.g. concurrent credential rotation).
/// </summary>
public interface ILoginPipelineObserver
{
    Task OnAfterVerifyBeforeCreateSessionAsync(
        string role,
        long verifiedEpoch,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpLoginPipelineObserver : ILoginPipelineObserver
{
    public static readonly NoOpLoginPipelineObserver Instance = new();

    private NoOpLoginPipelineObserver() { }

    public Task OnAfterVerifyBeforeCreateSessionAsync(
        string role,
        long verifiedEpoch,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
