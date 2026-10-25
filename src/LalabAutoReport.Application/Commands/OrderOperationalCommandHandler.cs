using System;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Commands;

namespace LalabAutoReport.ApplicationCore.Commands;

public sealed class OrderOperationalCommandHandler
{
    private readonly IOrderOperationalCommandStore _store;
    private readonly InFlightOperationRegistry _inFlightRegistry;
    private readonly IOperationLookupStore _lookupStore;

    public OrderOperationalCommandHandler(
        IOrderOperationalCommandStore store,
        InFlightOperationRegistry inFlightRegistry,
        IOperationLookupStore lookupStore)
    {
        _store = store;
        _inFlightRegistry = inFlightRegistry;
        _lookupStore = lookupStore;
    }

    public async Task<OrderCommandResult> SetDeliveredAsync(
        ActorContext actor,
        long orderId,
        SetOrderDeliveredRequest request,
        ICommandPipelineObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        actor.Require(Capabilities.OrdersSetDelivered);

        string opId = NormalizeOperationId(request.OperationId);
        if (orderId <= 0)
        {
            throw new CommandValidationException("Mã đơn hàng không hợp lệ.");
        }

        var (_, hashHex) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersSetDelivered,
            orderId,
            request.ExpectedRevision,
            "delivered",
            boolValue: request.Delivered);

        if (!_inFlightRegistry.TryBegin(opId))
        {
            throw new OperationInProgressException(opId);
        }

        try
        {
            return await _store.ExecuteOrderOperationalCommandAsync(
                actor,
                Capabilities.OrdersSetDelivered,
                orderId,
                opId,
                request.ExpectedRevision,
                hashHex,
                request.Delivered,
                observer,
                cancellationToken);
        }
        finally
        {
            _inFlightRegistry.End(opId);
        }
    }

    public async Task<OrderCommandResult> SetPrintedAsync(
        ActorContext actor,
        long orderId,
        SetOrderPrintedRequest request,
        ICommandPipelineObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        actor.Require(Capabilities.OrdersSetPrinted);

        string opId = NormalizeOperationId(request.OperationId);
        if (orderId <= 0)
        {
            throw new CommandValidationException("Mã đơn hàng không hợp lệ.");
        }

        var (_, hashHex) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersSetPrinted,
            orderId,
            request.ExpectedRevision,
            "printed",
            boolValue: request.Printed);

        if (!_inFlightRegistry.TryBegin(opId))
        {
            throw new OperationInProgressException(opId);
        }

        try
        {
            return await _store.ExecuteOrderOperationalCommandAsync(
                actor,
                Capabilities.OrdersSetPrinted,
                orderId,
                opId,
                request.ExpectedRevision,
                hashHex,
                request.Printed,
                observer,
                cancellationToken);
        }
        finally
        {
            _inFlightRegistry.End(opId);
        }
    }

    public async Task<OrderCommandResult> SetNoteAsync(
        ActorContext actor,
        long orderId,
        SetOrderNoteRequest request,
        ICommandPipelineObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        actor.Require(Capabilities.OrdersEditNote);

        string opId = NormalizeOperationId(request.OperationId);
        if (orderId <= 0)
        {
            throw new CommandValidationException("Mã đơn hàng không hợp lệ.");
        }

        string? normalizedNote = request.Note?.Trim();
        if (string.IsNullOrEmpty(normalizedNote))
        {
            normalizedNote = null;
        }
        else if (normalizedNote.Length > 4000)
        {
            throw new CommandValidationException("Ghi chú vượt quá giới hạn 4000 ký tự.");
        }

        var (_, hashHex) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersEditNote,
            orderId,
            request.ExpectedRevision,
            "note",
            stringValue: normalizedNote);

        if (!_inFlightRegistry.TryBegin(opId))
        {
            throw new OperationInProgressException(opId);
        }

        try
        {
            return await _store.ExecuteOrderOperationalCommandAsync(
                actor,
                Capabilities.OrdersEditNote,
                orderId,
                opId,
                request.ExpectedRevision,
                hashHex,
                normalizedNote,
                observer,
                cancellationToken);
        }
        finally
        {
            _inFlightRegistry.End(opId);
        }
    }

    public async Task<OperationLookupResult> LookupOperationAsync(
        ActorContext actor,
        string rawOperationId,
        CancellationToken cancellationToken = default)
    {
        string opId = NormalizeOperationId(rawOperationId);

        if (_inFlightRegistry.IsInFlight(opId))
        {
            return new OperationLookupResult(OperationLookupStatus.Pending);
        }

        var result = await _lookupStore.LookupOperationAsync(opId, cancellationToken);
        if (result.Status == OperationLookupStatus.Committed)
        {
            if (!string.IsNullOrEmpty(result.Capability) && !actor.Has(result.Capability))
            {
                // §3.9: Operation lookup only returns a receipt if caller holds the capability, else not_found
                return new OperationLookupResult(OperationLookupStatus.NotFound);
            }
        }

        return result;
    }

    private static string NormalizeOperationId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var guid))
        {
            throw new CommandValidationException("Mã thao tác (operationId) không đúng định dạng UUID.");
        }
        return guid.ToString("N");
    }
}
