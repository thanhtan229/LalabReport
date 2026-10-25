namespace LalabAutoReport.Contracts;

public static class ApiErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string SessionRevoked = "SESSION_REVOKED";
    public const string SessionUpgradeRequired = "SESSION_UPGRADE_REQUIRED";
    public const string ForbiddenCapability = "FORBIDDEN_CAPABILITY";
    public const string LoginRateLimited = "LOGIN_RATE_LIMITED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string OperationInProgress = "OPERATION_IN_PROGRESS";
    public const string StaleRevision = "STALE_REVISION";
    public const string NotFound = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ServerBusy = "SERVER_BUSY";
    public const string WritesDisabledUntilPhase2 = "WRITES_DISABLED_UNTIL_PHASE2";
    public const string ClientTooOld = "CLIENT_TOO_OLD";
    public const string ServerTooOld = "SERVER_TOO_OLD";
    public const string BillClaimConflict = "BILL_CLAIM_CONFLICT";
}
