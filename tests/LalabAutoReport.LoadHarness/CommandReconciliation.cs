using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace LalabAutoReport.LoadHarness;

public sealed class ExpectedOperation
{
    public required string OperationId { get; init; }
    public required long OrderId { get; init; }
    public required string FieldGroup { get; init; } // "delivered", "printed", "note"
    public required string Capability { get; init; } // Capabilities.OrdersSetDelivered, etc.
    public required string ExpectedEffect { get; init; } // "Committed" or "NoChange"
    public required int HttpStatusCode { get; init; } // 200
    public required string ReturnedResultJson { get; init; }
    public bool IsReplay { get; init; }
    public bool IsRetry { get; init; }
}

public sealed class DbReceiptRow
{
    public string OperationId { get; init; } = "";
    public string Capability { get; init; } = "";
    public string EntityType { get; init; } = "";
    public long EntityId { get; init; }
    public string Status { get; init; } = "";
    public string ResultJson { get; init; } = "";
    public string PayloadHash { get; init; } = "";
}

public sealed class DbAuditRow
{
    public string OperationId { get; init; } = "";
    public string Action { get; init; } = "";
    public string Outcome { get; init; } = "";
    public long? EntityId { get; init; }
}

public sealed class DbChangeJournalRow
{
    public string OperationId { get; init; } = "";
    public string EntityType { get; init; } = "";
    public long EntityId { get; init; }
    public string FieldGroup { get; init; } = "";
    public long Revision { get; init; }
}

public sealed class ReconciliationInputs
{
    public required IReadOnlyList<ExpectedOperation> ExpectedOperations { get; init; }
    public required IReadOnlyList<DbReceiptRow> DbReceipts { get; init; }
    public required IReadOnlyList<DbAuditRow> DbAudits { get; init; }
    public required IReadOnlyList<DbChangeJournalRow> DbChangeJournals { get; init; }
    public required IReadOnlyList<string> DbOutboxOperationIds { get; init; }
}

public sealed class ReconciliationResult
{
    public bool Passed { get; init; }
    public int ExpectedOperationCount { get; init; }
    public int MatchedReceiptCount { get; init; }
    public int MissingReceiptCount { get; init; }
    public int DuplicateReceiptCount { get; init; }
    public int MismatchedReceiptCount { get; init; }
    public int MissingAuditCount { get; init; }
    public int OrphanAuditCount { get; init; }
    public int MissingJournalEffectCount { get; init; }
    public int DuplicateJournalCount { get; init; }
    public int DuplicateAuditCount { get; init; }
    public int DuplicateOutboxCount { get; init; }

    public IReadOnlyList<string> MissingReceiptSampleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MismatchedSampleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OrphanAuditSampleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingJournalSampleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailureDetails { get; init; } = Array.Empty<string>();
}

public abstract class CanonicalJsonNode : IEquatable<CanonicalJsonNode>
{
    public abstract string ToCanonicalString();

    public bool Equals(CanonicalJsonNode? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(ToCanonicalString(), other.ToCanonicalString(), StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as CanonicalJsonNode);

    public override int GetHashCode() => ToCanonicalString().GetHashCode();

    public override string ToString() => ToCanonicalString();
}

public sealed class CanonicalNullNode : CanonicalJsonNode
{
    public static readonly CanonicalNullNode Instance = new();
    public override string ToCanonicalString() => "null";
}

public sealed class CanonicalBoolNode : CanonicalJsonNode
{
    public bool Value { get; }
    public CanonicalBoolNode(bool value) => Value = value;
    public override string ToCanonicalString() => Value ? "true" : "false";
}

public sealed class CanonicalNumberNode : CanonicalJsonNode
{
    public decimal Value { get; }
    public CanonicalNumberNode(decimal value) => Value = value;

    public override string ToCanonicalString()
    {
        if (Value == 0m) return "0";
        return Value.ToString("0.############################", CultureInfo.InvariantCulture);
    }
}

public sealed class CanonicalStringNode : CanonicalJsonNode
{
    public string Value { get; }
    public CanonicalStringNode(string value) => Value = value ?? "";
    public override string ToCanonicalString() => JsonSerializer.Serialize(Value);
}

public sealed class CanonicalArrayNode : CanonicalJsonNode
{
    public List<CanonicalJsonNode> Items { get; } = new();

    public override string ToCanonicalString()
    {
        // Ordering-insensitive array canonicalization: sort items by their canonical string
        var sorted = Items.Select(i => i.ToCanonicalString()).OrderBy(s => s, StringComparer.Ordinal);
        return "[" + string.Join(",", sorted) + "]";
    }
}

public sealed class CanonicalObjectNode : CanonicalJsonNode
{
    public Dictionary<string, CanonicalJsonNode> Properties { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public override string ToCanonicalString()
    {
        // Deterministic sort by property name (case-insensitive) and lowercase keys
        var sorted = Properties
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"\"{p.Key.ToLowerInvariant()}\":{p.Value.ToCanonicalString()}");
        return "{" + string.Join(",", sorted) + "}";
    }
}

public static class CanonicalJson
{
    public static CanonicalJsonNode? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Canonicalize(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static CanonicalJsonNode? Canonicalize(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new CanonicalObjectNode();
                foreach (var prop in element.EnumerateObject())
                {
                    var child = Canonicalize(prop.Value);
                    if (child == null) return null;
                    if (obj.Properties.ContainsKey(prop.Name)) return null;
                    obj.Properties[prop.Name] = child;
                }
                return obj;

            case JsonValueKind.Array:
                var arr = new CanonicalArrayNode();
                foreach (var item in element.EnumerateArray())
                {
                    var child = Canonicalize(item);
                    if (child == null) return null;
                    arr.Items.Add(child);
                }
                return arr;

            case JsonValueKind.String:
                return new CanonicalStringNode(element.GetString() ?? "");

            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var dec))
                {
                    return new CanonicalNumberNode(dec);
                }
                if (decimal.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDec))
                {
                    return new CanonicalNumberNode(parsedDec);
                }
                return null;

            case JsonValueKind.True:
                return new CanonicalBoolNode(true);

            case JsonValueKind.False:
                return new CanonicalBoolNode(false);

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return CanonicalNullNode.Instance;

            default:
                return null;
        }
    }

    public static bool AreEquivalent(string? jsonA, string? jsonB)
    {
        if (string.IsNullOrWhiteSpace(jsonA) || string.IsNullOrWhiteSpace(jsonB)) return false;
        var nodeA = Parse(jsonA);
        var nodeB = Parse(jsonB);
        if (nodeA == null || nodeB == null) return false;
        return nodeA.Equals(nodeB);
    }
}

public static class CommandReconciliationEvaluator
{
    private static readonly HashSet<string> AllowedRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "orderid",
        "fieldgroup",
        "revision",
        "changed",
        "committed",
        "operationid",
        "value",
        "syncstate"
    };

    private static readonly HashSet<string> DeliveredValueKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "delivered",
        "deliveredat",
        "deliveredby"
    };

    private static readonly HashSet<string> PrintedValueKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "printed",
        "printedat",
        "printprogress"
    };

    private static readonly HashSet<string> NoteValueKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "note"
    };

    public static bool TryCanonicalizeAndValidate(string json, out CanonicalObjectNode? canonical)
    {
        canonical = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        CanonicalJsonNode? parsed;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            parsed = CanonicalJson.Canonicalize(doc.RootElement);
        }
        catch
        {
            return false;
        }

        if (parsed is not CanonicalObjectNode root) return false;

        // Reject extra unexpected properties at root
        foreach (var k in root.Properties.Keys)
        {
            if (!AllowedRootKeys.Contains(k)) return false;
        }

        // Require complete schema properties
        if (!root.Properties.ContainsKey("orderid") ||
            !root.Properties.ContainsKey("fieldgroup") ||
            !root.Properties.ContainsKey("revision") ||
            !root.Properties.ContainsKey("operationid") ||
            !root.Properties.ContainsKey("value") ||
            (!root.Properties.ContainsKey("changed") && !root.Properties.ContainsKey("committed")))
        {
            return false;
        }

        // Validate types
        if (root.Properties["orderid"] is not CanonicalNumberNode) return false;
        if (root.Properties["fieldgroup"] is not CanonicalStringNode) return false;
        if (root.Properties["revision"] is not CanonicalNumberNode) return false;
        if (root.Properties["operationid"] is not CanonicalStringNode) return false;
        if (root.Properties["value"] is not CanonicalObjectNode valObj) return false;

        // Normalize committed -> changed
        if (root.Properties.TryGetValue("committed", out var comNode))
        {
            if (comNode is not CanonicalBoolNode) return false;
            if (!root.Properties.ContainsKey("changed"))
            {
                root.Properties["changed"] = comNode;
            }
            root.Properties.Remove("committed");
        }
        if (root.Properties["changed"] is not CanonicalBoolNode) return false;

        // Normalize syncstate
        if (!root.Properties.ContainsKey("syncstate"))
        {
            root.Properties["syncstate"] = CanonicalNullNode.Instance;
        }
        else if (root.Properties["syncstate"] is not (CanonicalStringNode or CanonicalNullNode))
        {
            return false;
        }

        // Normalize operationid casing (GUIDs compared case-insensitively)
        var opNode = (CanonicalStringNode)root.Properties["operationid"];
        root.Properties["operationid"] = new CanonicalStringNode(opNode.Value.ToLowerInvariant());

        // Normalize fieldgroup string
        var fgNode = (CanonicalStringNode)root.Properties["fieldgroup"];
        string fg = fgNode.Value.ToLowerInvariant();
        root.Properties["fieldgroup"] = new CanonicalStringNode(fg);

        // Validate value object based on fieldgroup
        if (fg == "delivered")
        {
            if (valObj.Properties.Count != 3) return false;
            foreach (var k in valObj.Properties.Keys)
            {
                if (!DeliveredValueKeys.Contains(k)) return false;
            }
            if (valObj.Properties["delivered"] is not CanonicalBoolNode) return false;
            if (valObj.Properties["deliveredat"] is not (CanonicalStringNode or CanonicalNullNode)) return false;
            if (valObj.Properties["deliveredby"] is not (CanonicalStringNode or CanonicalNullNode)) return false;
        }
        else if (fg == "printed")
        {
            if (valObj.Properties.Count != 3) return false;
            foreach (var k in valObj.Properties.Keys)
            {
                if (!PrintedValueKeys.Contains(k)) return false;
            }
            if (valObj.Properties["printed"] is not CanonicalBoolNode) return false;
            if (valObj.Properties["printedat"] is not (CanonicalStringNode or CanonicalNullNode)) return false;
            if (valObj.Properties["printprogress"] is not CanonicalNumberNode) return false;
        }
        else if (fg == "note")
        {
            if (valObj.Properties.Count != 1) return false;
            foreach (var k in valObj.Properties.Keys)
            {
                if (!NoteValueKeys.Contains(k)) return false;
            }
            if (valObj.Properties["note"] is not (CanonicalStringNode or CanonicalNullNode)) return false;
        }
        else
        {
            // Non-standard fieldgroup: value must be a non-empty object
            if (valObj.Properties.Count == 0) return false;
        }

        canonical = root;
        return true;
    }

    public static bool ResultsMatch(string? expectedJson, string? dbJson)
    {
        if (string.IsNullOrWhiteSpace(expectedJson) || string.IsNullOrWhiteSpace(dbJson))
        {
            return false;
        }

        if (!TryCanonicalizeAndValidate(expectedJson, out var canonicalExpected) || canonicalExpected == null)
        {
            return false;
        }

        if (!TryCanonicalizeAndValidate(dbJson, out var canonicalDb) || canonicalDb == null)
        {
            return false;
        }

        return canonicalExpected.Equals(canonicalDb);
    }

    public static ReconciliationResult Reconcile(ReconciliationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var failures = new List<string>();

        // Deduplicate expected operations by OperationId (idempotent replays share OperationId)
        var expectedByOpId = new Dictionary<string, ExpectedOperation>(StringComparer.Ordinal);
        foreach (var op in inputs.ExpectedOperations)
        {
            if (!expectedByOpId.ContainsKey(op.OperationId))
            {
                expectedByOpId[op.OperationId] = op;
            }
        }

        // Index DB receipts by OperationId
        var receiptsByOpId = inputs.DbReceipts
            .GroupBy(r => r.OperationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Index DB audits by OperationId
        var auditsByOpId = inputs.DbAudits
            .Where(a => !string.IsNullOrEmpty(a.OperationId))
            .GroupBy(a => a.OperationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Index DB change journal by OperationId
        var journalsByOpId = inputs.DbChangeJournals
            .Where(j => !string.IsNullOrEmpty(j.OperationId))
            .GroupBy(j => j.OperationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Index DB outbox by OperationId
        var outboxByOpId = inputs.DbOutboxOperationIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        int matchedReceipts = 0;
        var missingReceipts = new List<string>();
        var duplicateReceipts = new List<string>();
        var mismatchedReceipts = new List<string>();
        var missingAudits = new List<string>();
        var missingJournals = new List<string>();
        var duplicateAudits = new List<string>();
        var duplicateJournals = new List<string>();
        var duplicateOutbox = new List<string>();

        // 1. Validate every expected operation
        foreach (var (opId, expected) in expectedByOpId)
        {
            // A. Receipt presence & integrity
            if (!receiptsByOpId.TryGetValue(opId, out var receiptList) || receiptList.Count == 0)
            {
                missingReceipts.Add(opId);
            }
            else
            {
                if (receiptList.Count > 1)
                {
                    duplicateReceipts.Add(opId);
                }

                var receipt = receiptList[0];
                if (!string.Equals(receipt.Status, "Committed", StringComparison.OrdinalIgnoreCase))
                {
                    mismatchedReceipts.Add($"{opId} (status '{receipt.Status}' != 'Committed')");
                }
                else if (!string.IsNullOrEmpty(expected.Capability) && !string.Equals(receipt.Capability, expected.Capability, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchedReceipts.Add($"{opId} (capability '{receipt.Capability}' != '{expected.Capability}')");
                }
                else if (!ResultsMatch(expected.ReturnedResultJson, receipt.ResultJson))
                {
                    mismatchedReceipts.Add($"{opId} (returned result does not match stored receipt result)");
                }
                else
                {
                    matchedReceipts++;
                }
            }

            // B. Audit log presence
            if (!auditsByOpId.TryGetValue(opId, out var auditList) || auditList.Count == 0)
            {
                missingAudits.Add(opId);
            }
            else
            {
                if (auditList.Count > 1)
                {
                    duplicateAudits.Add(opId);
                }

                var audit = auditList[0];
                if (!string.Equals(audit.Outcome, expected.ExpectedEffect, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchedReceipts.Add($"{opId} (audit outcome '{audit.Outcome}' != expected '{expected.ExpectedEffect}')");
                }
            }

            // C. Change journal presence for Committed mutations
            if (string.Equals(expected.ExpectedEffect, "Committed", StringComparison.OrdinalIgnoreCase))
            {
                if (!journalsByOpId.TryGetValue(opId, out var journalList) || journalList.Count == 0)
                {
                    missingJournals.Add(opId);
                }
                else if (journalList.Count > 1)
                {
                    duplicateJournals.Add(opId);
                }
            }
        }

        // 2. Detect orphan audit rows (audit rows whose operationId has no corresponding receipt or expected op)
        var orphanAudits = new List<string>();
        foreach (var (auditOpId, _) in auditsByOpId)
        {
            if (!receiptsByOpId.ContainsKey(auditOpId))
            {
                orphanAudits.Add(auditOpId);
            }
        }

        // 3. Outbox duplicate check
        foreach (var (outboxOpId, count) in outboxByOpId)
        {
            if (count > 1)
            {
                duplicateOutbox.Add(outboxOpId);
            }
        }

        // Formulate failures
        if (missingReceipts.Count > 0)
        {
            failures.Add($"Missing command receipts: {missingReceipts.Count} expected operation(s) have no receipt in command_receipts (samples: {string.Join(", ", missingReceipts.Take(3))}).");
        }

        if (duplicateReceipts.Count > 0)
        {
            failures.Add($"Duplicate command receipts: {duplicateReceipts.Count} operation(s) have multiple receipt rows.");
        }

        if (mismatchedReceipts.Count > 0)
        {
            failures.Add($"Mismatched command receipts: {mismatchedReceipts.Count} receipt(s) do not match expected outcomes (samples: {string.Join("; ", mismatchedReceipts.Take(3))}).");
        }

        if (missingAudits.Count > 0)
        {
            failures.Add($"Missing audit log rows: {missingAudits.Count} expected operation(s) have no corresponding audit_log row (samples: {string.Join(", ", missingAudits.Take(3))}).");
        }

        if (orphanAudits.Count > 0)
        {
            failures.Add($"Orphan audit log rows: {orphanAudits.Count} audit row(s) exist without a corresponding command_receipts row (samples: {string.Join(", ", orphanAudits.Take(3))}).");
        }

        if (missingJournals.Count > 0)
        {
            failures.Add($"Missing change journal effects: {missingJournals.Count} committed operation(s) have no change_journal row (samples: {string.Join(", ", missingJournals.Take(3))}).");
        }

        if (duplicateJournals.Count > 0)
        {
            failures.Add($"Duplicate change journal rows: {duplicateJournals.Count} operation(s) have multiple change_journal entries.");
        }

        if (duplicateAudits.Count > 0)
        {
            failures.Add($"Duplicate audit log rows: {duplicateAudits.Count} operation(s) have multiple audit_log entries.");
        }

        if (duplicateOutbox.Count > 0)
        {
            failures.Add($"Duplicate outbox rows: {duplicateOutbox.Count} operation(s) have multiple outbox entries.");
        }

        bool passed = failures.Count == 0;

        return new ReconciliationResult
        {
            Passed = passed,
            ExpectedOperationCount = expectedByOpId.Count,
            MatchedReceiptCount = matchedReceipts,
            MissingReceiptCount = missingReceipts.Count,
            DuplicateReceiptCount = duplicateReceipts.Count,
            MismatchedReceiptCount = mismatchedReceipts.Count,
            MissingAuditCount = missingAudits.Count,
            OrphanAuditCount = orphanAudits.Count,
            MissingJournalEffectCount = missingJournals.Count,
            DuplicateJournalCount = duplicateJournals.Count,
            DuplicateAuditCount = duplicateAudits.Count,
            DuplicateOutboxCount = duplicateOutbox.Count,
            MissingReceiptSampleIds = missingReceipts.Take(5).ToList(),
            MismatchedSampleIds = mismatchedReceipts.Take(5).ToList(),
            OrphanAuditSampleIds = orphanAudits.Take(5).ToList(),
            MissingJournalSampleIds = missingJournals.Take(5).ToList(),
            FailureDetails = failures
        };
    }
}
