using System;
using System.Text.Json;

namespace LalabAutoReport.ApplicationCore.Commands;

public class CommandValidationException : Exception
{
    public string Code => "VALIDATION_FAILED";
    public CommandValidationException(string message) : base(message) { }
}

public class IdempotencyConflictException : Exception
{
    public string Code => "IDEMPOTENCY_CONFLICT";
    public IdempotencyConflictException(string message = "Thao tác đã được thực hiện với nội dung khác.")
        : base(message) { }
}

public class OperationInProgressException : Exception
{
    public string Code => "OPERATION_IN_PROGRESS";
    public string OperationId { get; }
    public OperationInProgressException(string operationId, string message = "Thao tác đang được xử lý.")
        : base(message)
    {
        OperationId = operationId;
    }
}

public class StaleRevisionException : Exception
{
    public string Code => "STALE_REVISION";
    public string EntityType { get; }
    public long EntityId { get; }
    public string FieldGroup { get; }
    public long CurrentRevision { get; }
    public JsonElement CurrentValue { get; }

    public StaleRevisionException(
        string entityType,
        long entityId,
        string fieldGroup,
        long currentRevision,
        JsonElement currentValue,
        string message = "Đơn vừa được cập nhật trên thiết bị khác.")
        : base(message)
    {
        EntityType = entityType;
        EntityId = entityId;
        FieldGroup = fieldGroup;
        CurrentRevision = currentRevision;
        CurrentValue = currentValue;
    }
}

public class ServerBusyException : Exception
{
    public string Code => "SERVER_BUSY";
    public bool Retryable => true;
    public ServerBusyException(string message = "Hệ thống đang bận, vui lòng thử lại.")
        : base(message) { }
}

public class OrderNotFoundException : Exception
{
    public string Code => "NOT_FOUND";
    public OrderNotFoundException(string message = "Không tìm thấy đơn hàng.")
        : base(message) { }
}
