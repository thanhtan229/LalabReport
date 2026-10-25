using System;
using System.Collections.Concurrent;

namespace LalabAutoReport.ApplicationCore.Commands;

public sealed class InFlightOperationRegistry
{
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public bool TryBegin(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return false;
        return _inFlight.TryAdd(operationId, 0);
    }

    public void End(string operationId)
    {
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            _inFlight.TryRemove(operationId, out _);
        }
    }

    public bool IsInFlight(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return false;
        return _inFlight.ContainsKey(operationId);
    }
}
