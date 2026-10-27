using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Infrastructure.Data;

namespace LalabAutoReport.CrashHarness;

public class CrashObserver : ICommandPipelineObserver
{
    private readonly string _crashPoint;

    public CrashObserver(string crashPoint)
    {
        _crashPoint = crashPoint;
    }

    public Task OnAfterBusinessUpdateAsync(string operationId, string capability)
    {
        if (string.Equals(_crashPoint, "before_commit", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(134); // abrupt process death inside the open transaction (simulates crash without WER/dump wait)
        }
        return Task.CompletedTask;
    }

    public Task OnAfterCommitAsync(string operationId, string capability)
    {
        if (string.Equals(_crashPoint, "after_commit", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(134); // abrupt process death after commit (simulates crash without WER/dump wait)
        }
        return Task.CompletedTask;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? dbPath = null;
        string? crashPoint = null;
        long orderId = 0;
        string? opId = null;
        long expectedRev = 0;
        bool delivered = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--db" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] == "--crash-point" && i + 1 < args.Length) crashPoint = args[++i];
            else if (args[i] == "--order-id" && i + 1 < args.Length) orderId = long.Parse(args[++i]);
            else if (args[i] == "--op-id" && i + 1 < args.Length) opId = args[++i];
            else if (args[i] == "--expected-rev" && i + 1 < args.Length) expectedRev = long.Parse(args[++i]);
            else if (args[i] == "--delivered" && i + 1 < args.Length) delivered = bool.Parse(args[++i]);
        }

        if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(crashPoint) || orderId <= 0 || string.IsNullOrEmpty(opId))
        {
            Console.Error.WriteLine("Missing required arguments.");
            return 2;
        }

        var connectionFactory = new SqliteConnectionFactory(dbPath);
        var store = new SqliteOrderOperationalCommandStore(connectionFactory);
        var inFlight = new InFlightOperationRegistry();
        var lookup = new SqliteCommandReceiptReader(connectionFactory);
        var handler = new OrderOperationalCommandHandler(store, inFlight, lookup);

        var actor = new ActorContext("session", "Admin", new HashSet<string> { Capabilities.OrdersSetDelivered });
        var observer = new CrashObserver(crashPoint);

        var request = new SetOrderDeliveredRequest(opId, expectedRev, delivered);
        await handler.SetDeliveredAsync(actor, orderId, request, observer);

        return 0;
    }
}
