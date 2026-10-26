using System;
using System.Collections.Generic;

namespace LalabAutoReport.LoadHarness;

public sealed class VerdictInputs
{
    public int ReadsCount { get; init; }
    public int CommandsCount { get; init; }
    public int ReplaysAttempted { get; init; }
    public int ReplaysMatched { get; init; }
    public int GalleryRequestsExercised { get; init; }
    public int ContentionBurstsExecuted { get; init; }
    public int UnexpectedErrors { get; init; }
    public double ReadsP95Ms { get; init; }
    public double CommandsP95Ms { get; init; }
    public int SimulatorFailures { get; init; }
    public bool PragmaIntegrityOk { get; init; }
    public int ForeignKeyViolations { get; init; }
    public int PartialReceipts { get; init; }
    public int MissingAuditCount { get; init; }
    public int DuplicateRevisionEffects { get; init; }
    public int DuplicateAuditEffects { get; init; }
    public int DuplicateOutboxEffects { get; init; }
    public int FalseSuccessCount { get; init; }
    public bool InventoryDryRunSuccess { get; init; }
    public int InjectedAuthFailures { get; init; }
    public bool ReconciliationPassed { get; init; } = true;
    public int MissingReceiptCount { get; init; }
    public int OrphanAuditCount { get; init; }
    public int MissingJournalEffectCount { get; init; }
    public int MismatchedReceiptCount { get; init; }
    public IReadOnlyList<string> ReconciliationFailures { get; init; } = Array.Empty<string>();
}

public sealed class VerdictResult
{
    public bool Passed { get; init; }
    public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PassedCriteria { get; init; } = Array.Empty<string>();
}

public static class VerdictEvaluator
{
    public const double MaxReadsP95Ms = 1000.0;
    public const double MaxCommandsP95Ms = 2000.0;

    public static VerdictResult Evaluate(VerdictInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var failures = new List<string>();
        var passed = new List<string>();

        // 1. Required Coverage Categories
        if (inputs.ReadsCount > 0)
        {
            passed.Add($"Reads executed: {inputs.ReadsCount:N0}");
        }
        else
        {
            failures.Add("Coverage failure: Reads count was 0; read workload was not exercised.");
        }

        if (inputs.CommandsCount > 0)
        {
            passed.Add($"Commands executed: {inputs.CommandsCount:N0}");
        }
        else
        {
            failures.Add("Coverage failure: Commands count was 0; state command workload was not exercised.");
        }

        if (inputs.ReplaysAttempted > 0)
        {
            passed.Add($"Replays attempted: {inputs.ReplaysAttempted:N0}");
        }
        else
        {
            failures.Add("Coverage failure: Replays attempted was 0; idempotency replay workload was not exercised.");
        }

        if (inputs.GalleryRequestsExercised > 0)
        {
            passed.Add($"Gallery requests exercised: {inputs.GalleryRequestsExercised:N0}");
        }
        else
        {
            failures.Add("Coverage failure: Gallery requests exercised was 0; gallery endpoint was not exercised.");
        }

        if (inputs.ContentionBurstsExecuted > 0)
        {
            passed.Add($"Contention bursts executed: {inputs.ContentionBurstsExecuted:N0}");
        }
        else
        {
            failures.Add("Coverage failure: Contention bursts executed was 0; background write contention was not exercised.");
        }

        // 2. Unexpected Errors (HTTP status codes other than 200, 409, 503 or exceptions)
        if (inputs.UnexpectedErrors == 0)
        {
            passed.Add("Unexpected errors: 0");
        }
        else
        {
            failures.Add($"Error failure: {inputs.UnexpectedErrors} unexpected error(s) encountered (excluding expected 409 and 503).");
        }

        // 3. Injected Auth Failures
        if (inputs.InjectedAuthFailures > 0)
        {
            failures.Add($"Injected auth failure detected: {inputs.InjectedAuthFailures} unauthorized attempt(s).");
        }

        // 4. Idempotency Replay Match Rate (100% required)
        if (inputs.ReplaysAttempted > 0 && inputs.ReplaysMatched == inputs.ReplaysAttempted)
        {
            passed.Add($"Replay match rate: 100% ({inputs.ReplaysMatched}/{inputs.ReplaysAttempted})");
        }
        else if (inputs.ReplaysAttempted > 0)
        {
            failures.Add($"Idempotency failure: Replay matches ({inputs.ReplaysMatched}) did not equal attempts ({inputs.ReplaysAttempted}).");
        }

        // 5. No Duplicate Revision / Outbox / Audit Effects
        if (inputs.DuplicateRevisionEffects == 0)
        {
            passed.Add("Duplicate revision bump effects: 0");
        }
        else
        {
            failures.Add($"Duplicate effect failure: {inputs.DuplicateRevisionEffects} duplicate revision bump(s) detected in change_journal.");
        }

        if (inputs.DuplicateAuditEffects == 0)
        {
            passed.Add("Duplicate audit log effects: 0");
        }
        else
        {
            failures.Add($"Duplicate effect failure: {inputs.DuplicateAuditEffects} duplicate audit log row(s) detected for replayed operations.");
        }

        if (inputs.DuplicateOutboxEffects == 0)
        {
            passed.Add("Duplicate outbox effects: 0");
        }
        else
        {
            failures.Add($"Duplicate effect failure: {inputs.DuplicateOutboxEffects} duplicate outbox row(s) detected for replayed operations.");
        }

        if (inputs.FalseSuccessCount == 0)
        {
            passed.Add("False success entries: 0");
        }
        else
        {
            failures.Add($"Data integrity failure: {inputs.FalseSuccessCount} false success entry/entries detected.");
        }

        // 6. Latency Thresholds (Reads p95 < 1000ms, Commands p95 < 2000ms)
        if (inputs.ReadsP95Ms < MaxReadsP95Ms)
        {
            passed.Add($"Reads p95: {inputs.ReadsP95Ms:F1}ms (< {MaxReadsP95Ms:F0}ms target)");
        }
        else
        {
            failures.Add($"Latency SLA exceeded: Reads p95 ({inputs.ReadsP95Ms:F1}ms) >= {MaxReadsP95Ms:F0}ms target.");
        }

        if (inputs.CommandsP95Ms < MaxCommandsP95Ms)
        {
            passed.Add($"Commands p95: {inputs.CommandsP95Ms:F1}ms (< {MaxCommandsP95Ms:F0}ms target)");
        }
        else
        {
            failures.Add($"Latency SLA exceeded: Commands p95 ({inputs.CommandsP95Ms:F1}ms) >= {MaxCommandsP95Ms:F0}ms target.");
        }

        // 7. Background Simulator Failures
        if (inputs.SimulatorFailures == 0)
        {
            passed.Add("Simulator/background exceptions: 0");
        }
        else
        {
            failures.Add($"Simulator failure: Background contention simulator reported {inputs.SimulatorFailures} unhandled exception(s).");
        }

        // 8. Database Integrity Checks
        if (inputs.PragmaIntegrityOk)
        {
            passed.Add("PRAGMA integrity_check: ok");
        }
        else
        {
            failures.Add("Integrity check failure: PRAGMA integrity_check did not return 'ok'.");
        }

        if (inputs.ForeignKeyViolations == 0)
        {
            passed.Add("PRAGMA foreign_key_check: 0 violations");
        }
        else
        {
            failures.Add($"Integrity check failure: {inputs.ForeignKeyViolations} foreign key violation(s) found.");
        }

        if (inputs.PartialReceipts == 0)
        {
            passed.Add("Incomplete / partial command receipts: 0");
        }
        else
        {
            failures.Add($"Integrity check failure: {inputs.PartialReceipts} incomplete/non-committed command receipt(s) found.");
        }

        if (inputs.MissingAuditCount == 0)
        {
            passed.Add("Receipts missing audit log rows: 0");
        }
        else
        {
            failures.Add($"Integrity check failure: {inputs.MissingAuditCount} command receipt(s) missing corresponding audit log row.");
        }

        if (inputs.InventoryDryRunSuccess)
        {
            passed.Add("Server inventory --dry-run: PASS (Exit 0)");
        }
        else
        {
            failures.Add("Integrity check failure: Server inventory --dry-run exited with non-zero code.");
        }

        // 9. Command Reconciliation against Database
        if (inputs.ReconciliationPassed && inputs.MissingReceiptCount == 0 && inputs.OrphanAuditCount == 0 && inputs.MissingJournalEffectCount == 0 && inputs.MismatchedReceiptCount == 0)
        {
            passed.Add("Command reconciliation: 100% expected receipts, audits, and change journal effects verified.");
        }
        else
        {
            if (inputs.ReconciliationFailures.Count > 0)
            {
                foreach (var fail in inputs.ReconciliationFailures)
                {
                    failures.Add($"Reconciliation failure: {fail}");
                }
            }
            else
            {
                failures.Add($"Reconciliation failure: missing receipts={inputs.MissingReceiptCount}, orphan audits={inputs.OrphanAuditCount}, missing journal={inputs.MissingJournalEffectCount}, mismatched={inputs.MismatchedReceiptCount}.");
            }
        }

        return new VerdictResult
        {
            Passed = failures.Count == 0,
            FailureReasons = failures,
            PassedCriteria = passed
        };
    }
}
