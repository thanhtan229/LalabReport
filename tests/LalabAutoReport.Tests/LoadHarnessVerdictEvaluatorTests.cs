using System;
using System.Collections.Generic;
using FluentAssertions;
using LalabAutoReport.LoadHarness;
using Xunit;

namespace LalabAutoReport.Tests;

public class LoadHarnessVerdictEvaluatorTests
{
    private static VerdictInputs CreateHealthyInputs() => new()
    {
        ReadsCount = 5000,
        CommandsCount = 2000,
        ReplaysAttempted = 300,
        ReplaysMatched = 300,
        GalleryRequestsExercised = 500,
        ContentionBurstsExecuted = 50,
        UnexpectedErrors = 0,
        ReadsP95Ms = 15.5,
        CommandsP95Ms = 120.0,
        SimulatorFailures = 0,
        PragmaIntegrityOk = true,
        ForeignKeyViolations = 0,
        PartialReceipts = 0,
        MissingAuditCount = 0,
        DuplicateRevisionEffects = 0,
        DuplicateAuditEffects = 0,
        DuplicateOutboxEffects = 0,
        FalseSuccessCount = 0,
        InventoryDryRunSuccess = true,
        InjectedAuthFailures = 0
    };

    [Fact]
    public void Evaluate_HealthyInputs_ReturnsPassed()
    {
        var inputs = CreateHealthyInputs();
        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeTrue();
        verdict.FailureReasons.Should().BeEmpty();
        verdict.PassedCriteria.Should().NotBeEmpty();
    }

    [Fact]
    public void Evaluate_ZeroReads_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 0,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Reads count was 0"));
    }

    [Fact]
    public void Evaluate_ZeroCommands_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 0,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Commands count was 0"));
    }

    [Fact]
    public void Evaluate_ZeroReplays_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 0,
            ReplaysMatched = 0,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Replays attempted was 0"));
    }

    [Fact]
    public void Evaluate_ZeroGallery_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 0,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Gallery requests exercised was 0"));
    }

    [Fact]
    public void Evaluate_ZeroContentionBursts_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 0,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Contention bursts executed was 0"));
    }

    [Fact]
    public void Evaluate_UnexpectedErrors_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 3,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("unexpected error(s) encountered"));
    }

    [Fact]
    public void Evaluate_ReplayMismatch_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 299,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Replay matches (299) did not equal attempts (300)"));
    }

    [Theory]
    [InlineData(1000.0, 120.0, "Reads p95")]
    [InlineData(1500.5, 120.0, "Reads p95")]
    [InlineData(15.5, 2000.0, "Commands p95")]
    [InlineData(15.5, 2500.0, "Commands p95")]
    public void Evaluate_P95OverTarget_ReturnsFailed(double readsP95, double commandsP95, string expectedMetric)
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = readsP95,
            CommandsP95Ms = commandsP95,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains(expectedMetric));
    }

    [Fact]
    public void Evaluate_SimulatorFailure_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 2,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("reported 2 unhandled exception(s)"));
    }

    [Fact]
    public void Evaluate_IntegrityFailure_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = false,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("PRAGMA integrity_check"));
    }

    [Fact]
    public void Evaluate_ForeignKeyViolation_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 1,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("foreign key violation"));
    }

    [Fact]
    public void Evaluate_PartialReceipts_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 1,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("incomplete/non-committed command receipt"));
    }

    [Fact]
    public void Evaluate_MissingAuditCount_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 2,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("missing corresponding audit log row"));
    }

    [Fact]
    public void Evaluate_DuplicateRevisionEffects_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 1,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("duplicate revision bump(s) detected in change_journal"));
    }

    [Fact]
    public void Evaluate_DuplicateAuditEffects_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 1,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("duplicate audit log row(s)"));
    }

    [Fact]
    public void Evaluate_DuplicateOutboxEffects_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 1,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("duplicate outbox row(s)"));
    }

    [Fact]
    public void Evaluate_FalseSuccessCount_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 1,
            InventoryDryRunSuccess = true
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("false success"));
    }

    [Fact]
    public void Evaluate_InventoryDryRunFailed_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 0,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = false
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Server inventory --dry-run exited with non-zero code"));
    }

    [Fact]
    public void Evaluate_InjectedAuthFailure_ReturnsFailed()
    {
        var inputs = new VerdictInputs
        {
            ReadsCount = 5000,
            CommandsCount = 2000,
            ReplaysAttempted = 300,
            ReplaysMatched = 300,
            GalleryRequestsExercised = 500,
            ContentionBurstsExecuted = 50,
            UnexpectedErrors = 1,
            ReadsP95Ms = 15.5,
            CommandsP95Ms = 120.0,
            SimulatorFailures = 0,
            PragmaIntegrityOk = true,
            ForeignKeyViolations = 0,
            PartialReceipts = 0,
            MissingAuditCount = 0,
            DuplicateRevisionEffects = 0,
            DuplicateAuditEffects = 0,
            DuplicateOutboxEffects = 0,
            FalseSuccessCount = 0,
            InventoryDryRunSuccess = true,
            InjectedAuthFailures = 1
        };

        var verdict = VerdictEvaluator.Evaluate(inputs);

        verdict.Passed.Should().BeFalse();
        verdict.FailureReasons.Should().Contain(r => r.Contains("Injected auth failure detected"));
    }
}
