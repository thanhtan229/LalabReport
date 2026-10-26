using System;
using System.Collections.Generic;
using FluentAssertions;
using LalabAutoReport.Contracts;
using LalabAutoReport.LoadHarness;
using Xunit;

namespace LalabAutoReport.Tests;

public class CommandReconciliationEvaluatorTests
{
    private static ExpectedOperation CreateSampleOp(string opId, string effect = "Committed") => new()
    {
        OperationId = opId,
        OrderId = 101,
        FieldGroup = "delivered",
        Capability = Capabilities.OrdersSetDelivered,
        ExpectedEffect = effect,
        HttpStatusCode = 200,
        ReturnedResultJson = $"{{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":{(effect == "Committed" ? "true" : "false")},\"operationId\":\"{opId}\",\"syncState\":null,\"value\":{{\"delivered\":{(effect == "Committed" ? "true" : "false")},\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}}}",
        IsReplay = false,
        IsRetry = false
    };

    private static DbReceiptRow CreateDbReceipt(string opId, string effect = "Committed") => new()
    {
        OperationId = opId,
        Capability = Capabilities.OrdersSetDelivered,
        EntityType = "order",
        EntityId = 101,
        Status = "Committed",
        ResultJson = $"{{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":1,\"Changed\":{(effect == "Committed" ? "true" : "false")},\"OperationId\":\"{opId}\",\"SyncState\":null,\"Value\":{{\"delivered\":{(effect == "Committed" ? "true" : "false")},\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}}}",
        PayloadHash = "hash123"
    };

    private static DbAuditRow CreateDbAudit(string opId, string effect = "Committed") => new()
    {
        OperationId = opId,
        Action = Capabilities.OrdersSetDelivered,
        Outcome = effect,
        EntityId = 101
    };

    private static DbChangeJournalRow CreateDbJournal(string opId) => new()
    {
        OperationId = opId,
        EntityType = "order",
        EntityId = 101,
        FieldGroup = "delivered",
        Revision = 1
    };

    [Fact]
    public void Reconcile_AllReceiptsAuditsAndJournalsPresent_ReturnsPass()
    {
        var op1 = CreateSampleOp("op_1", "Committed");
        var op2 = CreateSampleOp("op_2", "NoChange");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1, op2 },
            DbReceipts = new[] { CreateDbReceipt("op_1", "Committed"), CreateDbReceipt("op_2", "NoChange") },
            DbAudits = new[] { CreateDbAudit("op_1", "Committed"), CreateDbAudit("op_2", "NoChange") },
            DbChangeJournals = new[] { CreateDbJournal("op_1") }, // op_2 is NoChange, no journal expected
            DbOutboxOperationIds = new[] { "op_1" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeTrue();
        result.MissingReceiptCount.Should().Be(0);
        result.MismatchedReceiptCount.Should().Be(0);
        result.MissingAuditCount.Should().Be(0);
        result.OrphanAuditCount.Should().Be(0);
        result.MissingJournalEffectCount.Should().Be(0);
        result.FailureDetails.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_OneMissingReceipt_ReturnsFail()
    {
        var op1 = CreateSampleOp("op_1", "Committed");
        var op2 = CreateSampleOp("op_2", "Committed");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1, op2 },
            DbReceipts = new[] { CreateDbReceipt("op_1", "Committed") }, // op_2 missing!
            DbAudits = new[] { CreateDbAudit("op_1", "Committed"), CreateDbAudit("op_2", "Committed") },
            DbChangeJournals = new[] { CreateDbJournal("op_1"), CreateDbJournal("op_2") },
            DbOutboxOperationIds = new[] { "op_1", "op_2" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeFalse();
        result.MissingReceiptCount.Should().Be(1);
        result.MissingReceiptSampleIds.Should().Contain("op_2");
        result.OrphanAuditCount.Should().Be(1); // op_2 audit row now has no receipt
        result.FailureDetails.Should().Contain(f => f.Contains("Missing command receipts"));
    }

    [Fact]
    public void Reconcile_AllMissingReceipts_ReturnsFail()
    {
        var op1 = CreateSampleOp("op_1", "Committed");
        var op2 = CreateSampleOp("op_2", "Committed");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1, op2 },
            DbReceipts = Array.Empty<DbReceiptRow>(), // All deleted!
            DbAudits = new[] { CreateDbAudit("op_1", "Committed"), CreateDbAudit("op_2", "Committed") },
            DbChangeJournals = new[] { CreateDbJournal("op_1"), CreateDbJournal("op_2") },
            DbOutboxOperationIds = new[] { "op_1", "op_2" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeFalse();
        result.MissingReceiptCount.Should().Be(2);
        result.OrphanAuditCount.Should().Be(2);
        result.FailureDetails.Should().Contain(f => f.Contains("Missing command receipts: 2"));
    }

    [Fact]
    public void Reconcile_MissingAuditRow_ReturnsFail()
    {
        var op1 = CreateSampleOp("op_1", "Committed");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1 },
            DbReceipts = new[] { CreateDbReceipt("op_1", "Committed") },
            DbAudits = Array.Empty<DbAuditRow>(), // Missing audit!
            DbChangeJournals = new[] { CreateDbJournal("op_1") },
            DbOutboxOperationIds = new[] { "op_1" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeFalse();
        result.MissingAuditCount.Should().Be(1);
        result.FailureDetails.Should().Contain(f => f.Contains("Missing audit log rows"));
    }

    [Fact]
    public void Reconcile_MissingJournalForCommitted_ReturnsFail()
    {
        var op1 = CreateSampleOp("op_1", "Committed");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1 },
            DbReceipts = new[] { CreateDbReceipt("op_1", "Committed") },
            DbAudits = new[] { CreateDbAudit("op_1", "Committed") },
            DbChangeJournals = Array.Empty<DbChangeJournalRow>(), // Missing journal for Committed!
            DbOutboxOperationIds = new[] { "op_1" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeFalse();
        result.MissingJournalEffectCount.Should().Be(1);
        result.MissingJournalSampleIds.Should().Contain("op_1");
        result.FailureDetails.Should().Contain(f => f.Contains("Missing change journal effects"));
    }

    [Fact]
    public void Reconcile_NoChangeWithoutJournal_ReturnsPass()
    {
        var op1 = CreateSampleOp("op_1", "NoChange");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1 },
            DbReceipts = new[] { CreateDbReceipt("op_1", "NoChange") },
            DbAudits = new[] { CreateDbAudit("op_1", "NoChange") },
            DbChangeJournals = Array.Empty<DbChangeJournalRow>(), // NoChange has no journal row by design
            DbOutboxOperationIds = Array.Empty<string>()
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeTrue();
        result.MissingJournalEffectCount.Should().Be(0);
    }

    [Fact]
    public void Reconcile_ExtraOrphanAuditRow_ReturnsFail()
    {
        var op1 = CreateSampleOp("op_1", "Committed");

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1 },
            DbReceipts = new[] { CreateDbReceipt("op_1", "Committed") },
            DbAudits = new[] { CreateDbAudit("op_1", "Committed"), CreateDbAudit("orphan_op_999", "Committed") }, // Extra orphan audit!
            DbChangeJournals = new[] { CreateDbJournal("op_1") },
            DbOutboxOperationIds = new[] { "op_1" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeFalse();
        result.OrphanAuditCount.Should().Be(1);
        result.OrphanAuditSampleIds.Should().Contain("orphan_op_999");
        result.FailureDetails.Should().Contain(f => f.Contains("Orphan audit log rows"));
    }

    [Fact]
    public void Reconcile_ResultJsonMismatch_ReturnsFail()
    {
        var op1 = CreateSampleOp("op_1", "Committed");
        var receipt = new DbReceiptRow
        {
            OperationId = "op_1",
            Capability = Capabilities.OrdersSetDelivered,
            EntityType = "order",
            EntityId = 101,
            Status = "Committed",
            ResultJson = "{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":99,\"Changed\":true,\"OperationId\":\"op_1\",\"SyncState\":null,\"Value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}", // revision 99 != 1
            PayloadHash = "hash123"
        };

        var inputs = new ReconciliationInputs
        {
            ExpectedOperations = new[] { op1 },
            DbReceipts = new[] { receipt },
            DbAudits = new[] { CreateDbAudit("op_1", "Committed") },
            DbChangeJournals = new[] { CreateDbJournal("op_1") },
            DbOutboxOperationIds = new[] { "op_1" }
        };

        var result = CommandReconciliationEvaluator.Reconcile(inputs);

        result.Passed.Should().BeFalse();
        result.MismatchedReceiptCount.Should().Be(1);
        result.FailureDetails.Should().Contain(f => f.Contains("Mismatched command receipts"));
    }

    [Fact]
    public void ResultsMatch_EmptyObjectVsValidExpected_ReturnsFalse()
    {
        const string validExpected = "{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":true,\"operationId\":\"op_1\",\"syncState\":null,\"value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}";
        const string emptyDb = "{}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(validExpected, emptyDb);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_MissingNestedValueProp_ReturnsFalse()
    {
        const string validExpected = "{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":true,\"operationId\":\"op_1\",\"syncState\":null,\"value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}";
        // Missing deliveredBy in value object:
        const string missingPropDb = "{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":1,\"Changed\":true,\"OperationId\":\"op_1\",\"SyncState\":null,\"Value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\"}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(validExpected, missingPropDb);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_ChangedDeliveredValueWithSameMetadata_ReturnsFalse()
    {
        const string expected = "{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":true,\"operationId\":\"op_1\",\"syncState\":null,\"value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}";
        // delivered is false instead of true
        const string db = "{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":1,\"Changed\":true,\"OperationId\":\"op_1\",\"SyncState\":null,\"Value\":{\"delivered\":false,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(expected, db);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_ChangedPrintedValueWithSameMetadata_ReturnsFalse()
    {
        const string expected = "{\"orderId\":101,\"fieldGroup\":\"printed\",\"revision\":3,\"changed\":true,\"operationId\":\"op_2\",\"syncState\":null,\"value\":{\"printed\":true,\"printedAt\":\"2026-10-26T10:00:00Z\",\"printProgress\":10}}";
        // printed is false and progress is 0
        const string db = "{\"OrderId\":101,\"FieldGroup\":\"printed\",\"Revision\":3,\"Changed\":true,\"OperationId\":\"op_2\",\"SyncState\":null,\"Value\":{\"printed\":false,\"printedAt\":\"2026-10-26T10:00:00Z\",\"printProgress\":0}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(expected, db);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_ChangedNoteValue_ReturnsFalse()
    {
        const string expected = "{\"orderId\":101,\"fieldGroup\":\"note\",\"revision\":2,\"changed\":true,\"operationId\":\"op_3\",\"syncState\":null,\"value\":{\"note\":\"Original Note\"}}";
        const string db = "{\"OrderId\":101,\"FieldGroup\":\"note\",\"Revision\":2,\"Changed\":true,\"OperationId\":\"op_3\",\"SyncState\":null,\"Value\":{\"note\":\"Modified Note\"}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(expected, db);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_DifferingRevision_ReturnsFalse()
    {
        const string expected = "{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":true,\"operationId\":\"op_1\",\"syncState\":null,\"value\":{\"delivered\":true,\"deliveredAt\":null,\"deliveredBy\":null}}";
        const string db = "{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":2,\"Changed\":true,\"OperationId\":\"op_1\",\"SyncState\":null,\"Value\":{\"delivered\":true,\"deliveredAt\":null,\"deliveredBy\":null}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(expected, db);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_DifferingChangedFlag_ReturnsFalse()
    {
        const string expected = "{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":true,\"operationId\":\"op_1\",\"syncState\":null,\"value\":{\"delivered\":true,\"deliveredAt\":null,\"deliveredBy\":null}}";
        const string db = "{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":1,\"Changed\":false,\"OperationId\":\"op_1\",\"SyncState\":null,\"Value\":{\"delivered\":true,\"deliveredAt\":null,\"deliveredBy\":null}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(expected, db);

        matches.Should().BeFalse();
    }

    [Fact]
    public void ResultsMatch_EquivalentValidResultsInCamelCaseVsPascalCase_ReturnsTrue()
    {
        const string expectedCamel = "{\"orderId\":101,\"fieldGroup\":\"delivered\",\"revision\":1,\"changed\":true,\"operationId\":\"a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d\",\"syncState\":null,\"value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}";
        const string dbPascal = "{\"OrderId\":101,\"FieldGroup\":\"delivered\",\"Revision\":1,\"Changed\":true,\"OperationId\":\"A1B2C3D4-E5F6-7A8B-9C0D-1E2F3A4B5C6D\",\"SyncState\":null,\"Value\":{\"delivered\":true,\"deliveredAt\":\"2026-10-26T10:00:00Z\",\"deliveredBy\":\"Nhân viên\"}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(expectedCamel, dbPascal);

        matches.Should().BeTrue();
    }

    [Fact]
    public void ResultsMatch_ArraysAndObjectsOrderingInsensitiveCanonicalization_ReturnsTrue()
    {
        const string json1 = "{\"orderId\":101,\"fieldGroup\":\"custom\",\"revision\":1,\"changed\":true,\"operationId\":\"op_x\",\"syncState\":null,\"value\":{\"tags\":[\"beta\",\"alpha\"],\"nested\":{\"z\":26,\"a\":1}}}";
        const string json2 = "{\"OrderId\":101,\"FieldGroup\":\"custom\",\"Revision\":1,\"Changed\":true,\"OperationId\":\"op_x\",\"SyncState\":null,\"Value\":{\"nested\":{\"a\":1.0,\"z\":26.0},\"tags\":[\"alpha\",\"beta\"]}}";

        bool matches = CommandReconciliationEvaluator.ResultsMatch(json1, json2);

        matches.Should().BeTrue();
    }
}
