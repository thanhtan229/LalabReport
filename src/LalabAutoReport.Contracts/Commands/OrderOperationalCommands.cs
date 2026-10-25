using System.Text.Json;

namespace LalabAutoReport.Contracts.Commands;

public sealed record SetOrderDeliveredRequest(string OperationId, long ExpectedRevision, bool Delivered);
public sealed record SetOrderPrintedRequest(string OperationId, long ExpectedRevision, bool Printed);
public sealed record SetOrderNoteRequest(string OperationId, long ExpectedRevision, string? Note);

public sealed record OrderFieldStateDto(
    long OrderId,
    string FieldGroup,
    long Revision,
    JsonElement Value,
    bool Changed,
    string OperationId,
    string? SyncState);
