using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Contracts;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.LoadHarness;

public class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // Minimal valid 1x1 JFIF JPEG byte array (134 bytes)
    private static readonly byte[] MinimalValidJpeg = new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
        0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
        0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
        0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20,
        0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
        0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32,
        0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
        0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00,
        0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F,
        0x00, 0x7F, 0x00, 0xFF, 0xD9
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("===============================================================================");
        Console.WriteLine("     Lalab Auto Report - Phase 2 B4 Load Harness & Contention Simulator        ");
        Console.WriteLine("===============================================================================");

        int durationSeconds = 60;
        int clientCount = 10;
        string outDir = Path.GetFullPath("docs/qa-evidence-phase2");
        bool keepTemp = false;
        string? customSourceDb = null;
        bool forceFreshFixture = false;
        bool injectAuthFailure = false;
        string? faultDeleteReceipts = null;
        string? faultCorruptReceiptResults = null;
        bool faultDropReceiptAfterHttp = false;
        bool faultOrphanAudit = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--duration", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int d) && d > 0) durationSeconds = d;
            }
            else if (string.Equals(args[i], "--clients", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int c) && c > 0) clientCount = c;
            }
            else if (string.Equals(args[i], "--out-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outDir = Path.GetFullPath(args[++i]);
            }
            else if (string.Equals(args[i], "--source-db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                customSourceDb = Path.GetFullPath(args[++i]);
            }
            else if (string.Equals(args[i], "--fresh-fixture", StringComparison.OrdinalIgnoreCase))
            {
                forceFreshFixture = true;
            }
            else if (string.Equals(args[i], "--inject-auth-failure", StringComparison.OrdinalIgnoreCase))
            {
                injectAuthFailure = true;
            }
            else if (string.Equals(args[i], "--fault-delete-receipts", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                faultDeleteReceipts = args[++i];
            }
            else if (string.Equals(args[i], "--fault-corrupt-receipt-results", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                faultCorruptReceiptResults = args[++i];
            }
            else if (string.Equals(args[i], "--fault-drop-receipt-after-http", StringComparison.OrdinalIgnoreCase))
            {
                faultDropReceiptAfterHttp = true;
            }
            else if (string.Equals(args[i], "--fault-orphan-audit", StringComparison.OrdinalIgnoreCase))
            {
                faultOrphanAudit = true;
            }
            else if (string.Equals(args[i], "--keep-temp", StringComparison.OrdinalIgnoreCase))
            {
                keepTemp = true;
            }
        }

        Console.WriteLine($"[Config] Run Duration: {durationSeconds}s | Concurrent Clients: {clientCount}");
        Console.WriteLine($"[Config] Evidence OutDir: {outDir}");
        Console.WriteLine($"[Config] Fresh Fixture: {forceFreshFixture} | Inject Auth Failure: {injectAuthFailure}");
        if (!string.IsNullOrEmpty(faultDeleteReceipts)) Console.WriteLine($"[Config] [FAULT TEST] Delete Receipts: {faultDeleteReceipts}");
        if (!string.IsNullOrEmpty(faultCorruptReceiptResults)) Console.WriteLine($"[Config] [FAULT TEST] Corrupt Receipt Results: {faultCorruptReceiptResults}");
        if (faultDropReceiptAfterHttp) Console.WriteLine("[Config] [FAULT TEST] Drop Receipt After HTTP: TRUE");
        if (faultOrphanAudit) Console.WriteLine("[Config] [FAULT TEST] Inject Orphan Audit: TRUE");

        string repoRoot = FindRepoRoot();

        // 1. Build and verify Server Release binary
        Console.WriteLine("[Setup] Building and verifying Server Release binary...");
        ServerBinaryInfo serverInfo;
        try
        {
            serverInfo = await EnsureServerBinaryAsync(repoRoot);
            Console.WriteLine($"[Setup] Server Release binary: {serverInfo.ExecutablePath}");
            Console.WriteLine($"[Setup] Server BuildId:        {serverInfo.BuildId}");
            Console.WriteLine($"[Setup] Repository Git SHA:    {serverInfo.GitSha}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to build/verify Server binary: {ex.Message}");
            return 1;
        }

        // 2. Resolve database source and fixture mode
        string defaultArtifactsDb = Path.Combine(repoRoot, "artifacts", "acceptance", "profile", "lalab_autoreport.db");
        bool useSyntheticFixture = forceFreshFixture;
        string? sourceDbToCopy = null;

        if (!forceFreshFixture)
        {
            if (!string.IsNullOrWhiteSpace(customSourceDb))
            {
                if (File.Exists(customSourceDb))
                {
                    sourceDbToCopy = customSourceDb;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] Specified --source-db not found at: {customSourceDb}");
                    return 1;
                }
            }
            else if (File.Exists(defaultArtifactsDb))
            {
                sourceDbToCopy = defaultArtifactsDb;
            }
            else
            {
                Console.WriteLine("[Setup] Clean checkout detected (artifacts DB absent). Enabling deterministic synthetic fixture generation.");
                useSyntheticFixture = true;
            }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_load_b4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempDbPath = Path.Combine(tempDir, "lalab_autoreport.db");
        Console.WriteLine($"[Setup] Isolated Temp DB dir: {tempDir}");

        Process? serverProcess = null;
        try
        {
            if (!useSyntheticFixture && sourceDbToCopy != null)
            {
                Console.WriteLine($"[Setup] Seeding from source database: {sourceDbToCopy}");
                File.Copy(sourceDbToCopy, tempDbPath, overwrite: true);
                if (File.Exists(sourceDbToCopy + "-wal"))
                {
                    File.Copy(sourceDbToCopy + "-wal", tempDbPath + "-wal", overwrite: true);
                }
                if (File.Exists(sourceDbToCopy + "-shm"))
                {
                    try { File.Copy(sourceDbToCopy + "-shm", tempDbPath + "-shm", overwrite: true); } catch { }
                }

                await SetupFixtureDatabaseAsync(tempDir, repoRoot);
            }
            else
            {
                Console.WriteLine("[Setup] Generating deterministic synthetic file structure and migrating clean database...");
                await GenerateSyntheticFixtureAsync(tempDir, tempDbPath);
            }

            // 3. Find free loopback ports
            int mobilePort = GetAvailablePort();
            int healthPort = GetAvailablePort();
            while (healthPort == mobilePort) healthPort = GetAvailablePort();

            Console.WriteLine($"[Setup] Allocated Loopback Ports -> Mobile API: {mobilePort}, Health: {healthPort}");

            // 4. Start Server.exe
            var psi = new ProcessStartInfo
            {
                FileName = serverInfo.ExecutablePath,
                Arguments = $"--data-dir \"{tempDir}\" --port {mobilePort} --health-port {healthPort} --lan-writes orders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            serverProcess = Process.Start(psi);
            if (serverProcess == null)
            {
                Console.Error.WriteLine("[ERROR] Failed to start Server.exe process.");
                return 1;
            }

            // 5. Poll /health/ready until 200 OK
            Console.WriteLine("[Setup] Waiting for Server /health/ready on loopback...");
            bool isReady = await WaitForServerReadyAsync(healthPort, timeoutSeconds: 30);
            if (!isReady)
            {
                Console.Error.WriteLine("[ERROR] Server failed to become ready within timeout.");
                return 1;
            }
            Console.WriteLine("[Setup] Server is READY.");

            // 6. Authenticate and retrieve Bearer token
            using var authClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{mobilePort}") };
            var loginResp = await authClient.PostAsJsonAsync("/api/v1/auth/sessions", new
            {
                pin = "888888",
                clientKind = "load_harness",
                deviceLabel = "B4-LoadHarness"
            });

            if (!loginResp.IsSuccessStatusCode)
            {
                string errText = await loginResp.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"[ERROR] Login failed: {loginResp.StatusCode} - {errText}");
                return 1;
            }

            var loginData = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
            string authToken = loginData.GetProperty("token").GetString()!;
            Console.WriteLine($"[Setup] Authenticated successfully as Admin (SessionId: {loginData.GetProperty("sessionId").GetString()})");

            // 7. Discover available orders for work_date 2026-09-29
            authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            var ordersResp = await authClient.GetAsync("/api/orders?date=2026-09-29");
            if (!ordersResp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[ERROR] Could not query /api/orders?date=2026-09-29: {ordersResp.StatusCode}");
                return 1;
            }

            var ordersArray = await ordersResp.Content.ReadFromJsonAsync<JsonElement>();
            var orderIds = new List<long>();
            foreach (var item in ordersArray.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp))
                {
                    orderIds.Add(idProp.GetInt64());
                }
            }

            if (orderIds.Count == 0)
            {
                Console.Error.WriteLine("[ERROR] No orders found for date 2026-09-29 in temp DB.");
                return 1;
            }
            Console.WriteLine($"[Setup] Discovered {orderIds.Count} order(s) for 2026-09-29 (IDs: {string.Join(", ", orderIds.Take(5))}{(orderIds.Count > 5 ? "..." : "")})");

            // 8. Start Load Test Execution
            Console.WriteLine($"\n[Load Test] Launching {clientCount} concurrent clients for {durationSeconds}s run...");
            Console.WriteLine("[Load Test] Workload mix: reads, state commands (delivered/printed/note), 409-retry, same-operation replay, scan-shaped DB contention.");

            var metrics = new ConcurrentBag<RequestMetric>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
            var clientTasks = new List<Task>();

            // 8a. Parallel scan-shaped write contention simulator
            int scanBurstsExecuted = 0;
            int simulatorFailures = 0;
            var scanContentionTask = Task.Run(async () =>
            {
                var rnd = new Random(1337);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(rnd.Next(250, 600), cts.Token);
                        using var conn = new SqliteConnection($"Data Source={tempDbPath};Default Timeout=5");
                        await conn.OpenAsync(cts.Token);
                        using var tx = conn.BeginTransaction();
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "UPDATE orders SET updated_at = @now WHERE id = (SELECT id FROM orders ORDER BY RANDOM() LIMIT 1);";
                        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                        await cmd.ExecuteNonQueryAsync(cts.Token);

                        // Hold write lock for brief duration (15-25ms) modeling scan write burst
                        await Task.Delay(rnd.Next(15, 25), cts.Token);
                        tx.Commit();
                        Interlocked.Increment(ref scanBurstsExecuted);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Background contention worker failures must not be silently swallowed
                        Interlocked.Increment(ref simulatorFailures);
                        Console.Error.WriteLine($"[Contention Simulator Error] {ex.GetType().Name}: {ex.Message}");
                    }
                }
            });

            // 8b. Launch the concurrent clients
            var replayMetrics = new ConcurrentBag<ReplayMetric>();
            var stale409Metrics = new ConcurrentBag<Stale409Metric>();
            var recordedOps = new ConcurrentBag<ExpectedOperation>();
            int injectedAuthFailureCount = 0;

            var stopwatch = Stopwatch.StartNew();

            for (int clientId = 0; clientId < clientCount; clientId++)
            {
                int cid = clientId;
                clientTasks.Add(Task.Run(async () =>
                {
                    using var client = new HttpClient
                    {
                        BaseAddress = new Uri($"http://127.0.0.1:{mobilePort}"),
                        Timeout = TimeSpan.FromSeconds(15)
                    };
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
                    client.DefaultRequestHeaders.Add("X-Lalab-Client-Api", "1.0");

                    var rnd = new Random(cid * 100 + 42);
                    bool injectedThisClient = false;

                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            long targetOrderId = orderIds[rnd.Next(orderIds.Count)];

                            // If auth failure injection is enabled, inject invalid token on one client
                            if (injectAuthFailure && cid == (clientCount > 1 ? 1 : 0) && !injectedThisClient)
                            {
                                injectedThisClient = true;
                                Interlocked.Increment(ref injectedAuthFailureCount);
                                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid_injected_token_xyz");
                            }

                            if (cid == 0)
                            {
                                // Client 0: Idempotency & Replay Client
                                await ExecuteReplayClientIterationAsync(client, targetOrderId, metrics, replayMetrics, recordedOps, rnd, cts.Token);
                            }
                            else if (cid is 1 or 2 or 3)
                            {
                                // Clients 1-3: Read-Heavy
                                await ExecuteReadIterationAsync(client, targetOrderId, metrics, rnd, cts.Token);
                            }
                            else if (cid is 4 or 5 or 6)
                            {
                                // Clients 4-6: State Command Writers with 409 retry
                                await ExecuteCommandWriterIterationAsync(client, targetOrderId, metrics, stale409Metrics, recordedOps, rnd, cts.Token);
                            }
                            else
                            {
                                // Clients 7-9: Mixed Read & Write
                                if (rnd.Next(2) == 0)
                                {
                                    await ExecuteReadIterationAsync(client, targetOrderId, metrics, rnd, cts.Token);
                                }
                                else
                                {
                                    await ExecuteCommandWriterIterationAsync(client, targetOrderId, metrics, stale409Metrics, recordedOps, rnd, cts.Token);
                                }
                            }

                            // Brief think-time between operations (15 - 50ms)
                            await Task.Delay(rnd.Next(15, 50), cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            metrics.Add(new RequestMetric
                            {
                                Endpoint = "EXCEPTION",
                                Category = "Error",
                                StatusCode = 0,
                                LatencyMs = 0,
                                ErrorMessage = ex.Message
                            });
                        }
                    }
                }));
            }

            // Wait for duration to elapse
            await Task.WhenAll(clientTasks);
            await scanContentionTask;
            stopwatch.Stop();

            Console.WriteLine($"[Load Test] Finished {durationSeconds}s run. Elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s.");
            Console.WriteLine($"[Load Test] Parallel scan write bursts executed: {scanBurstsExecuted}");
            if (simulatorFailures > 0)
            {
                Console.Error.WriteLine($"[Load Test] Parallel scan simulator failures: {simulatorFailures}");
            }

            // 9. Stop server cleanly
            Console.WriteLine("[Teardown] Stopping Server process tree cleanly...");
            if (!serverProcess.HasExited)
            {
                try
                {
                    serverProcess.Kill(entireProcessTree: true);
                    await serverProcess.WaitForExitAsync();
                }
                catch { }
            }
            SqliteConnection.ClearAllPools();
            Console.WriteLine("[Teardown] Server stopped.");

            // 9b. Apply Fault Injections (Test-only regressions)
            if (!string.IsNullOrEmpty(faultDeleteReceipts))
            {
                Console.WriteLine($"[Fault Injection] Applying --fault-delete-receipts '{faultDeleteReceipts}'...");
                using var faultConn = new SqliteConnection($"Data Source={tempDbPath}");
                await faultConn.OpenAsync();
                if (string.Equals(faultDeleteReceipts, "all", StringComparison.OrdinalIgnoreCase))
                {
                    int deleted = await faultConn.ExecuteAsync("DELETE FROM command_receipts;");
                    Console.WriteLine($"[Fault Injection] Deleted ALL ({deleted}) rows from command_receipts.");
                }
                else if (int.TryParse(faultDeleteReceipts, out int n) && n > 0)
                {
                    int deleted = await faultConn.ExecuteAsync(
                        "DELETE FROM command_receipts WHERE operation_id IN (SELECT operation_id FROM command_receipts LIMIT @n);",
                        new { n });
                    Console.WriteLine($"[Fault Injection] Deleted {deleted} row(s) from command_receipts.");
                }
            }

            if (!string.IsNullOrEmpty(faultCorruptReceiptResults))
            {
                Console.WriteLine($"[Fault Injection] Applying --fault-corrupt-receipt-results '{faultCorruptReceiptResults}': corrupting receipt result_json to '{{}}'...");
                using var faultConn = new SqliteConnection($"Data Source={tempDbPath}");
                await faultConn.OpenAsync();
                if (string.Equals(faultCorruptReceiptResults, "all", StringComparison.OrdinalIgnoreCase))
                {
                    int corrupted = await faultConn.ExecuteAsync("UPDATE command_receipts SET result_json = '{}';");
                    Console.WriteLine($"[Fault Injection] Corrupted ALL ({corrupted}) rows in command_receipts to '{{}}'.");
                }
                else if (int.TryParse(faultCorruptReceiptResults, out int n) && n > 0)
                {
                    int corrupted = await faultConn.ExecuteAsync(
                        "UPDATE command_receipts SET result_json = '{}' WHERE operation_id IN (SELECT operation_id FROM command_receipts LIMIT @n);",
                        new { n });
                    Console.WriteLine($"[Fault Injection] Corrupted {corrupted} row(s) in command_receipts to '{{}}'.");
                }
            }

            if (faultDropReceiptAfterHttp)
            {
                Console.WriteLine("[Fault Injection] Applying --fault-drop-receipt-after-http: dropping 1 receipt for an executed HTTP operation...");
                using var faultConn = new SqliteConnection($"Data Source={tempDbPath}");
                await faultConn.OpenAsync();
                int deleted = await faultConn.ExecuteAsync("DELETE FROM command_receipts WHERE operation_id IN (SELECT operation_id FROM command_receipts LIMIT 1);");
                Console.WriteLine($"[Fault Injection] Dropped {deleted} receipt row(s).");
            }

            if (faultOrphanAudit)
            {
                Console.WriteLine("[Fault Injection] Applying --fault-orphan-audit: injecting orphan audit_log row without a receipt...");
                using var faultConn = new SqliteConnection($"Data Source={tempDbPath}");
                await faultConn.OpenAsync();
                string orphanOp = "orphan_fault_" + Guid.NewGuid().ToString("N");
                await faultConn.ExecuteAsync(@"
                    INSERT INTO audit_log (
                        occurred_at_utc, action, outcome, reason,
                        actor_kind, actor_role, actor_session_id, actor_device_id, actor_label, client_ip,
                        entity_type, entity_id, operation_id, before_json, after_json
                    ) VALUES (
                        datetime('now'), 'order.operational', 'Committed', 'fault_test',
                        'client', 'Admin', 'fault-session', 'fault-device', 'fault-label', '127.0.0.1',
                        'order', 1, @Op, '{}', '{}'
                    );
                ", new { Op = orphanOp });
                Console.WriteLine($"[Fault Injection] Injected orphan audit row with operationId: {orphanOp}");
            }

            // 10. Post-Load Database Integrity & Concurrency Verification
            Console.WriteLine("\n[Verification] Verifying database integrity and concurrency effects after load...");
            var integrityResult = await VerifyDatabaseIntegrityAsync(tempDbPath, serverInfo.ExecutablePath, tempDir);
            Console.WriteLine($"[Verification] PRAGMA integrity_check: {integrityResult.PragmaIntegrity}");
            Console.WriteLine($"[Verification] Foreign key violations: {integrityResult.ForeignKeyViolations}");
            Console.WriteLine($"[Verification] Partial receipts (incomplete/non-committed): {integrityResult.PartialReceiptCount}");
            Console.WriteLine($"[Verification] Inconsistent audit entries: {integrityResult.InconsistentAuditCount}");
            Console.WriteLine($"[Verification] Duplicate revision bump effects: {integrityResult.DuplicateRevisionEffects}");
            Console.WriteLine($"[Verification] Duplicate audit log entries: {integrityResult.DuplicateAuditEffects}");
            Console.WriteLine($"[Verification] Duplicate outbox entries: {integrityResult.DuplicateOutboxEffects}");
            Console.WriteLine($"[Verification] False success entries: {integrityResult.FalseSuccessCount}");
            Console.WriteLine($"[Verification] Server inventory --dry-run: {(integrityResult.InventoryDryRunSuccess ? "PASS" : "FAIL")}");

            // 10b. Client Command Reconciliation against Database
            Console.WriteLine("\n[Reconciliation] Reconciling client-side recorded operations against database...");
            using var reconConn = new SqliteConnection($"Data Source={tempDbPath};Default Timeout=10");
            await reconConn.OpenAsync();
            var dbReceipts = (await reconConn.QueryAsync<DbReceiptRow>(@"
                SELECT 
                    operation_id AS OperationId,
                    capability AS Capability,
                    entity_type AS EntityType,
                    entity_id AS EntityId,
                    status AS Status,
                    result_json AS ResultJson,
                    payload_hash AS PayloadHash
                FROM command_receipts;
            ")).ToList();

            var dbAudits = (await reconConn.QueryAsync<DbAuditRow>(@"
                SELECT 
                    operation_id AS OperationId,
                    action AS Action,
                    outcome AS Outcome,
                    entity_id AS EntityId
                FROM audit_log
                WHERE operation_id IS NOT NULL;
            ")).ToList();

            var dbJournals = (await reconConn.QueryAsync<DbChangeJournalRow>(@"
                SELECT 
                    operation_id AS OperationId,
                    entity_type AS EntityType,
                    entity_id AS EntityId,
                    field_group AS FieldGroup,
                    revision AS Revision
                FROM change_journal
                WHERE operation_id IS NOT NULL;
            ")).ToList();

            var dbOutboxOpIds = (await reconConn.QueryAsync<string>(@"
                SELECT operation_id FROM cloud_operational_outbox WHERE operation_id IS NOT NULL;
            ")).ToList();

            var reconInputs = new ReconciliationInputs
            {
                ExpectedOperations = recordedOps.ToList(),
                DbReceipts = dbReceipts,
                DbAudits = dbAudits,
                DbChangeJournals = dbJournals,
                DbOutboxOperationIds = dbOutboxOpIds
            };

            var reconResult = CommandReconciliationEvaluator.Reconcile(reconInputs);
            Console.WriteLine($"[Reconciliation] Expected unique client operations: {reconResult.ExpectedOperationCount}");
            Console.WriteLine($"[Reconciliation] Matched receipts:                  {reconResult.MatchedReceiptCount}");
            Console.WriteLine($"[Reconciliation] Missing receipts:                  {reconResult.MissingReceiptCount}");
            Console.WriteLine($"[Reconciliation] Mismatched receipts:               {reconResult.MismatchedReceiptCount}");
            Console.WriteLine($"[Reconciliation] Missing audit rows:                {reconResult.MissingAuditCount}");
            Console.WriteLine($"[Reconciliation] Orphan audit rows:                 {reconResult.OrphanAuditCount}");
            Console.WriteLine($"[Reconciliation] Missing change journal rows:       {reconResult.MissingJournalEffectCount}");
            Console.WriteLine($"[Reconciliation] Result:                            {(reconResult.Passed ? "PASS" : "FAIL")}");

            // 11. Compute Metrics and Overall Verdict
            var allMetricsList = metrics.ToList();
            var readMetricsList = allMetricsList.Where(m => m.Category == "Read").ToList();
            var cmdMetricsList = allMetricsList.Where(m => m.Category == "Command").ToList();
            var replaysList = replayMetrics.ToList();

            var readStats = CalculatePercentiles(readMetricsList);
            var cmdStats = CalculatePercentiles(cmdMetricsList);
            var allStats = CalculatePercentiles(allMetricsList);

            int galleryCount = allMetricsList.Count(m => m.Endpoint.Contains("/gallery"));
            int unexpectedErrorsCount = allMetricsList.Count(m => m.StatusCode != 200 && m.StatusCode != 409 && m.StatusCode != 503);
            int replaysMatchedCount = replaysList.Count(r => r.Matched);

            var verdictInputs = new VerdictInputs
            {
                ReadsCount = readMetricsList.Count,
                CommandsCount = cmdMetricsList.Count,
                ReplaysAttempted = replaysList.Count,
                ReplaysMatched = replaysMatchedCount,
                GalleryRequestsExercised = galleryCount,
                ContentionBurstsExecuted = scanBurstsExecuted,
                UnexpectedErrors = unexpectedErrorsCount,
                ReadsP95Ms = readStats.P95,
                CommandsP95Ms = cmdStats.P95,
                SimulatorFailures = simulatorFailures,
                PragmaIntegrityOk = string.Equals(integrityResult.PragmaIntegrity, "ok", StringComparison.OrdinalIgnoreCase),
                ForeignKeyViolations = integrityResult.ForeignKeyViolations,
                PartialReceipts = integrityResult.PartialReceiptCount,
                MissingAuditCount = integrityResult.InconsistentAuditCount,
                DuplicateRevisionEffects = integrityResult.DuplicateRevisionEffects,
                DuplicateAuditEffects = integrityResult.DuplicateAuditEffects,
                DuplicateOutboxEffects = integrityResult.DuplicateOutboxEffects,
                FalseSuccessCount = integrityResult.FalseSuccessCount,
                InventoryDryRunSuccess = integrityResult.InventoryDryRunSuccess,
                InjectedAuthFailures = injectedAuthFailureCount,
                ReconciliationPassed = reconResult.Passed,
                MissingReceiptCount = reconResult.MissingReceiptCount,
                OrphanAuditCount = reconResult.OrphanAuditCount,
                MissingJournalEffectCount = reconResult.MissingJournalEffectCount,
                MismatchedReceiptCount = reconResult.MismatchedReceiptCount,
                ReconciliationFailures = reconResult.FailureDetails
            };

            var verdict = VerdictEvaluator.Evaluate(verdictInputs);

            var report = GenerateLoadReport(
                allMetricsList,
                replaysList,
                stale409Metrics.ToList(),
                scanBurstsExecuted,
                stopwatch.Elapsed.TotalSeconds,
                clientCount,
                integrityResult,
                reconResult,
                verdict,
                serverInfo,
                useSyntheticFixture);

            PrintConsoleSummary(report, verdict, reconResult);

            // 12. Write evidence files
            Directory.CreateDirectory(outDir);
            string mdPath = Path.Combine(outDir, "B4_LOAD_REPORT.md");
            string jsonPath = Path.Combine(outDir, "b4_load_evidence.json");
            string readmePath = Path.Combine(outDir, "README.md");

            await File.WriteAllTextAsync(mdPath, report.MarkdownContent, Encoding.UTF8);
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report.JsonEvidence, JsonOpts), Encoding.UTF8);
            await File.WriteAllTextAsync(readmePath, GenerateReadmeContent(), Encoding.UTF8);

            Console.WriteLine($"\n[Evidence] Wrote Markdown Report -> {mdPath}");
            Console.WriteLine($"[Evidence] Wrote JSON Evidence    -> {jsonPath}");
            Console.WriteLine($"[Evidence] Wrote Run Instructions -> {readmePath}");

            return verdict.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[FATAL ERROR] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally
        {
            if (serverProcess != null && !serverProcess.HasExited)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            SqliteConnection.ClearAllPools();
            if (!keepTemp)
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    #region Client Iteration Workloads

    private static async Task ExecuteReadIterationAsync(
        HttpClient client,
        long orderId,
        ConcurrentBag<RequestMetric> metrics,
        Random rnd,
        CancellationToken ct)
    {
        int action = rnd.Next(3);
        if (action == 0)
        {
            // GET /api/orders?date=2026-09-29
            long start = Stopwatch.GetTimestamp();
            var resp = await client.GetAsync("/api/orders?date=2026-09-29", ct);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            metrics.Add(new RequestMetric
            {
                Endpoint = "GET /api/orders",
                Category = "Read",
                StatusCode = (int)resp.StatusCode,
                LatencyMs = ms
            });
        }
        else if (action == 1)
        {
            // GET /api/v1/orders/{id}/operational-state
            long start = Stopwatch.GetTimestamp();
            var resp = await client.GetAsync($"/api/v1/orders/{orderId}/operational-state", ct);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            metrics.Add(new RequestMetric
            {
                Endpoint = "GET /api/v1/orders/{id}/operational-state",
                Category = "Read",
                StatusCode = (int)resp.StatusCode,
                LatencyMs = ms
            });
        }
        else
        {
            // Gallery manifest + page
            long start = Stopwatch.GetTimestamp();
            var reqResp = await client.PostAsync($"/api/orders/{orderId}/gallery/requests", new StringContent(""), ct);
            double msReq = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            metrics.Add(new RequestMetric
            {
                Endpoint = "POST /api/orders/{id}/gallery/requests",
                Category = "Read",
                StatusCode = (int)reqResp.StatusCode,
                LatencyMs = msReq
            });

            if (reqResp.IsSuccessStatusCode)
            {
                long startPage = Stopwatch.GetTimestamp();
                var pageResp = await client.GetAsync($"/api/orders/{orderId}/gallery?offset=0&limit=24", ct);
                double msPage = Stopwatch.GetElapsedTime(startPage).TotalMilliseconds;
                metrics.Add(new RequestMetric
                {
                    Endpoint = "GET /api/orders/{id}/gallery",
                    Category = "Read",
                    StatusCode = (int)pageResp.StatusCode,
                    LatencyMs = msPage
                });
            }
        }
    }

    private static async Task ExecuteCommandWriterIterationAsync(
        HttpClient client,
        long orderId,
        ConcurrentBag<RequestMetric> metrics,
        ConcurrentBag<Stale409Metric> staleMetrics,
        ConcurrentBag<ExpectedOperation> recordedOps,
        Random rnd,
        CancellationToken ct)
    {
        // 1. Read current operational state
        long readStart = Stopwatch.GetTimestamp();
        var stateResp = await client.GetAsync($"/api/v1/orders/{orderId}/operational-state", ct);
        double readMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
        metrics.Add(new RequestMetric
        {
            Endpoint = "GET /api/v1/orders/{id}/operational-state",
            Category = "Read",
            StatusCode = (int)stateResp.StatusCode,
            LatencyMs = readMs
        });

        if (!stateResp.IsSuccessStatusCode) return;

        var stateJson = await stateResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        int fieldType = rnd.Next(3); // 0=delivered, 1=printed, 2=note

        string subPath;
        object payloadObj;
        long expectedRev;

        if (fieldType == 0)
        {
            subPath = "delivered";
            var deliveredObj = stateJson.GetProperty("delivered");
            bool curVal = deliveredObj.GetProperty("value").GetBoolean();
            expectedRev = deliveredObj.GetProperty("revision").GetInt64();
            payloadObj = new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = expectedRev,
                delivered = !curVal
            };
        }
        else if (fieldType == 1)
        {
            subPath = "printed";
            var printedObj = stateJson.GetProperty("printed");
            bool curVal = printedObj.GetProperty("value").GetBoolean();
            expectedRev = printedObj.GetProperty("revision").GetInt64();
            payloadObj = new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = expectedRev,
                printed = !curVal
            };
        }
        else
        {
            subPath = "note";
            var noteObj = stateJson.GetProperty("note");
            expectedRev = noteObj.GetProperty("revision").GetInt64();
            payloadObj = new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = expectedRev,
                note = $"Load note update {Guid.NewGuid().ToString("N")[..8]}"
            };
        }

        string initialOpId = ((dynamic)payloadObj).operationId;

        // 2. Post command
        long cmdStart = Stopwatch.GetTimestamp();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/{subPath}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json")
        };
        var resp = await client.SendAsync(req, ct);
        double cmdMs = Stopwatch.GetElapsedTime(cmdStart).TotalMilliseconds;

        metrics.Add(new RequestMetric
        {
            Endpoint = $"POST /api/v1/orders/{{id}}/{subPath}",
            Category = "Command",
            StatusCode = (int)resp.StatusCode,
            LatencyMs = cmdMs
        });

        if (resp.IsSuccessStatusCode)
        {
            string resBody = await resp.Content.ReadAsStringAsync(ct);
            string effect = "Committed";
            try
            {
                using var doc = JsonDocument.Parse(resBody);
                if (doc.RootElement.TryGetProperty("changed", out var chProp))
                {
                    effect = chProp.GetBoolean() ? "Committed" : "NoChange";
                }
                else if (doc.RootElement.TryGetProperty("revision", out var rProp))
                {
                    effect = rProp.GetInt64() > expectedRev ? "Committed" : "NoChange";
                }
            }
            catch { }

            recordedOps.Add(new ExpectedOperation
            {
                OperationId = initialOpId,
                OrderId = orderId,
                FieldGroup = subPath,
                Capability = subPath switch
                {
                    "delivered" => Capabilities.OrdersSetDelivered,
                    "printed" => Capabilities.OrdersSetPrinted,
                    _ => Capabilities.OrdersEditNote
                },
                ExpectedEffect = effect,
                HttpStatusCode = (int)resp.StatusCode,
                ReturnedResultJson = resBody,
                IsReplay = false,
                IsRetry = false
            });
        }

        // 3. If 409 STALE_REVISION: re-read state and retry once
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            string conflictBody = await resp.Content.ReadAsStringAsync(ct);
            if (conflictBody.Contains(ApiErrorCodes.StaleRevision))
            {
                // Step A: Re-read state
                var retryStateResp = await client.GetAsync($"/api/v1/orders/{orderId}/operational-state", ct);
                if (retryStateResp.IsSuccessStatusCode)
                {
                    var retryStateJson = await retryStateResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                    long latestRev = retryStateJson.GetProperty(subPath).GetProperty("revision").GetInt64();

                    // Step B: Prepare retry with new operation ID and latest revision
                    string retryOpId = Guid.NewGuid().ToString("N");
                    object retryPayload = subPath switch
                    {
                        "delivered" => new { operationId = retryOpId, expectedRevision = latestRev, delivered = ((dynamic)payloadObj).delivered },
                        "printed" => new { operationId = retryOpId, expectedRevision = latestRev, printed = ((dynamic)payloadObj).printed },
                        _ => new { operationId = retryOpId, expectedRevision = latestRev, note = ((dynamic)payloadObj).note }
                    };

                    long retryStart = Stopwatch.GetTimestamp();
                    var retryReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/{subPath}")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(retryPayload), Encoding.UTF8, "application/json")
                    };
                    var retryResp = await client.SendAsync(retryReq, ct);
                    double retryMs = Stopwatch.GetElapsedTime(retryStart).TotalMilliseconds;

                    metrics.Add(new RequestMetric
                    {
                        Endpoint = $"POST /api/v1/orders/{{id}}/{subPath} (Retry)",
                        Category = "Command",
                        StatusCode = (int)retryResp.StatusCode,
                        LatencyMs = retryMs,
                        IsRetry = true
                    });

                    staleMetrics.Add(new Stale409Metric
                    {
                        OrderId = orderId,
                        FieldGroup = subPath,
                        OriginalOpId = initialOpId,
                        RetryOpId = retryOpId,
                        RetryStatusCode = (int)retryResp.StatusCode,
                        RetrySucceeded = retryResp.IsSuccessStatusCode
                    });

                    if (retryResp.IsSuccessStatusCode)
                    {
                        string retryBody = await retryResp.Content.ReadAsStringAsync(ct);
                        string retryEffect = "Committed";
                        try
                        {
                            using var doc = JsonDocument.Parse(retryBody);
                            if (doc.RootElement.TryGetProperty("changed", out var chProp))
                            {
                                retryEffect = chProp.GetBoolean() ? "Committed" : "NoChange";
                            }
                            else if (doc.RootElement.TryGetProperty("revision", out var rProp))
                            {
                                retryEffect = rProp.GetInt64() > latestRev ? "Committed" : "NoChange";
                            }
                        }
                        catch { }

                        recordedOps.Add(new ExpectedOperation
                        {
                            OperationId = retryOpId,
                            OrderId = orderId,
                            FieldGroup = subPath,
                            Capability = subPath switch
                            {
                                "delivered" => Capabilities.OrdersSetDelivered,
                                "printed" => Capabilities.OrdersSetPrinted,
                                _ => Capabilities.OrdersEditNote
                            },
                            ExpectedEffect = retryEffect,
                            HttpStatusCode = (int)retryResp.StatusCode,
                            ReturnedResultJson = retryBody,
                            IsReplay = false,
                            IsRetry = true
                        });
                    }
                }
            }
        }
    }

    private static async Task ExecuteReplayClientIterationAsync(
        HttpClient client,
        long orderId,
        ConcurrentBag<RequestMetric> metrics,
        ConcurrentBag<ReplayMetric> replayMetrics,
        ConcurrentBag<ExpectedOperation> recordedOps,
        Random rnd,
        CancellationToken ct)
    {
        // 1. Read state
        var stateResp = await client.GetAsync($"/api/v1/orders/{orderId}/operational-state", ct);
        if (!stateResp.IsSuccessStatusCode) return;

        var stateJson = await stateResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        int fieldType = rnd.Next(3); // 0=delivered, 1=printed, 2=note

        string subPath;
        object payload;
        long expectedRev;

        if (fieldType == 0)
        {
            subPath = "delivered";
            var deliveredObj = stateJson.GetProperty("delivered");
            bool curVal = deliveredObj.GetProperty("value").GetBoolean();
            expectedRev = deliveredObj.GetProperty("revision").GetInt64();
            payload = new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = expectedRev,
                delivered = !curVal
            };
        }
        else if (fieldType == 1)
        {
            subPath = "printed";
            var printedObj = stateJson.GetProperty("printed");
            bool curVal = printedObj.GetProperty("value").GetBoolean();
            expectedRev = printedObj.GetProperty("revision").GetInt64();
            payload = new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = expectedRev,
                printed = !curVal
            };
        }
        else
        {
            subPath = "note";
            var noteObj = stateJson.GetProperty("note");
            expectedRev = noteObj.GetProperty("revision").GetInt64();
            payload = new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = expectedRev,
                note = $"Replay Note {Guid.NewGuid().ToString("N")[..8]}"
            };
        }

        string opId = ((dynamic)payload).operationId;

        // 2. Initial Send
        long start1 = Stopwatch.GetTimestamp();
        var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/{subPath}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        var resp1 = await client.SendAsync(req1, ct);
        double ms1 = Stopwatch.GetElapsedTime(start1).TotalMilliseconds;

        metrics.Add(new RequestMetric
        {
            Endpoint = $"POST /api/v1/orders/{{id}}/{subPath}",
            Category = "Command",
            StatusCode = (int)resp1.StatusCode,
            LatencyMs = ms1
        });

        if (!resp1.IsSuccessStatusCode) return;
        string res1Body = await resp1.Content.ReadAsStringAsync(ct);

        string effect1 = "Committed";
        try
        {
            using var doc = JsonDocument.Parse(res1Body);
            if (doc.RootElement.TryGetProperty("changed", out var chProp))
            {
                effect1 = chProp.GetBoolean() ? "Committed" : "NoChange";
            }
            else if (doc.RootElement.TryGetProperty("revision", out var rProp))
            {
                effect1 = rProp.GetInt64() > expectedRev ? "Committed" : "NoChange";
            }
        }
        catch { }

        recordedOps.Add(new ExpectedOperation
        {
            OperationId = opId,
            OrderId = orderId,
            FieldGroup = subPath,
            Capability = subPath switch
            {
                "delivered" => Capabilities.OrdersSetDelivered,
                "printed" => Capabilities.OrdersSetPrinted,
                _ => Capabilities.OrdersEditNote
            },
            ExpectedEffect = effect1,
            HttpStatusCode = (int)resp1.StatusCode,
            ReturnedResultJson = res1Body,
            IsReplay = false,
            IsRetry = false
        });

        // 3. Exact Replay with same operationId, same expectedRevision, same payload
        long start2 = Stopwatch.GetTimestamp();
        var req2 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/{subPath}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        var resp2 = await client.SendAsync(req2, ct);
        double ms2 = Stopwatch.GetElapsedTime(start2).TotalMilliseconds;

        metrics.Add(new RequestMetric
        {
            Endpoint = $"POST /api/v1/orders/{{id}}/{subPath} (Replay)",
            Category = "Command",
            StatusCode = (int)resp2.StatusCode,
            LatencyMs = ms2,
            IsReplay = true
        });

        string res2Body = await resp2.Content.ReadAsStringAsync(ct);
        bool match = resp2.StatusCode == HttpStatusCode.OK && res1Body.Equals(res2Body, StringComparison.Ordinal);

        replayMetrics.Add(new ReplayMetric
        {
            OperationId = opId,
            OrderId = orderId,
            InitialStatusCode = (int)resp1.StatusCode,
            ReplayStatusCode = (int)resp2.StatusCode,
            Matched = match
        });

        if (resp2.IsSuccessStatusCode)
        {
            recordedOps.Add(new ExpectedOperation
            {
                OperationId = opId,
                OrderId = orderId,
                FieldGroup = subPath,
                Capability = subPath switch
                {
                    "delivered" => Capabilities.OrdersSetDelivered,
                    "printed" => Capabilities.OrdersSetPrinted,
                    _ => Capabilities.OrdersEditNote
                },
                ExpectedEffect = effect1,
                HttpStatusCode = (int)resp2.StatusCode,
                ReturnedResultJson = res2Body,
                IsReplay = true,
                IsRetry = false
            });
        }
    }

    #endregion

    #region Fixture Generation & Setup

    private static async Task GenerateSyntheticFixtureAsync(string tempDir, string tempDbPath)
    {
        string syntheticRoot = Path.Combine(tempDir, "synthetic_root");
        Directory.CreateDirectory(syntheticRoot);

        // Deterministic folder structure for date 2026-09-29:
        // Customer 1: Van An -> 13x18 in, 20x30 in
        CreateSyntheticOrderFolder(syntheticRoot, "2026-09-29", "Van An", "13x18 in", 4, "retouch");
        CreateSyntheticOrderFolder(syntheticRoot, "2026-09-29", "Van An", "20x30 in", 3, "retouch");

        // Customer 2: Anh An -> Album 20x20
        CreateSyntheticOrderFolder(syntheticRoot, "2026-09-29", "Anh An", "Album 20x20", 5, "retouch");

        // Customer 3: Kim Studio -> 13x18 in, Album 20x20
        CreateSyntheticOrderFolder(syntheticRoot, "2026-09-29", "Kim Studio", "13x18 in", 3, "in");
        CreateSyntheticOrderFolder(syntheticRoot, "2026-09-29", "Kim Studio", "Album 20x20", 4, "retouch");

        // Customer 4: Studio Minh -> 20x30 in
        CreateSyntheticOrderFolder(syntheticRoot, "2026-09-29", "Studio Minh", "20x30 in", 2, "final");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLalabCore(options =>
        {
            options.DataDirectory = tempDir;
            options.ThumbnailsDirectory = Path.Combine(tempDir, "Thumbnails");
        });

        using var sp = services.BuildServiceProvider();

        // Run migrations
        var migrator = sp.GetRequiredService<IDatabaseMigrator>();
        await migrator.MigrateAsync();

        // Configure Settings & Hashed PINs
        var settingsRepo = sp.GetRequiredService<ISettingsRepository>();
        var settings = await settingsRepo.GetSettingsAsync();
        settings.AdminPin = "888888";
        settings.StaffPin = "111111";
        settings.EnableMobileServer = true;
        settings.WorkshopName = "Lalab Synthetic Test Workshop";
        settings.RootFolder = syntheticRoot;
        await settingsRepo.SaveSettingsAsync(settings);

        // Seed product catalog if variants don't exist
        var productRepo = sp.GetRequiredService<IProductRepository>();
        var existingVariants = await productRepo.GetAllVariantsAsync();
        if (existingVariants.Count == 0)
        {
            var familyPhoto = await productRepo.CreateFamilyAsync(new ProductFamily
            {
                Name = "Ảnh in",
                BillingMethod = BillingMethod.FileCount
            });
            await productRepo.CreateVariantAsync(new ProductVariant
            {
                FamilyId = familyPhoto.Id,
                CanonicalName = "13x18 in",
                UnitPrice = 5000,
                CanonicalSize = "13x18"
            });
            await productRepo.CreateVariantAsync(new ProductVariant
            {
                FamilyId = familyPhoto.Id,
                CanonicalName = "20x30 in",
                UnitPrice = 15000,
                CanonicalSize = "20x30"
            });

            var familyAlbum = await productRepo.CreateFamilyAsync(new ProductFamily
            {
                Name = "Album",
                BillingMethod = BillingMethod.AlbumBasePlusExtra
            });
            await productRepo.CreateVariantAsync(new ProductVariant
            {
                FamilyId = familyAlbum.Id,
                CanonicalName = "Album 20x20",
                BasePrice = 350000,
                IncludedSheets = 10,
                ExtraSheetPrice = 25000,
                CanonicalSize = "20x20"
            });
        }

        // Configure root folders
        var rootRepo = sp.GetRequiredService<IRootFolderRepository>();
        await rootRepo.InsertAsync(new RootFolder
        {
            FullPath = syntheticRoot,
            Name = "Synthetic_Test_Root",
            IsActive = true,
            IsDefault = true
        });

        // Initial scan of date 2026-09-29
        var scanService = sp.GetRequiredService<IScanService>();
        await scanService.ScanDateAsync("2026-09-29");
    }

    private static void CreateSyntheticOrderFolder(string root, string date, string customer, string spec, int fileCount, string leafFolderName)
    {
        string specDir = Path.Combine(root, date, customer, spec);
        string leafDir = Path.Combine(specDir, leafFolderName);
        Directory.CreateDirectory(leafDir);

        for (int i = 1; i <= fileCount; i++)
        {
            // Source file directly under spec folder
            string srcPath = Path.Combine(specDir, $"src_{i:D2}.jpg");
            File.WriteAllBytes(srcPath, MinimalValidJpeg);

            // Print file under leaf folder
            string leafPath = Path.Combine(leafDir, $"print_{i:D2}.jpg");
            File.WriteAllBytes(leafPath, MinimalValidJpeg);
        }
    }

    private static async Task SetupFixtureDatabaseAsync(string tempDir, string repoRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLalabCore(options =>
        {
            options.DataDirectory = tempDir;
            options.ThumbnailsDirectory = Path.Combine(tempDir, "Thumbnails");
        });

        using var sp = services.BuildServiceProvider();

        // Run migrations
        var migrator = sp.GetRequiredService<IDatabaseMigrator>();
        await migrator.MigrateAsync();

        // Configure Settings & Hashed PINs
        var settingsRepo = sp.GetRequiredService<ISettingsRepository>();
        var settings = await settingsRepo.GetSettingsAsync();
        settings.AdminPin = "888888";
        settings.StaffPin = "111111";
        settings.EnableMobileServer = true;
        settings.WorkshopName = "Lalab Load Test Workshop";
        await settingsRepo.SaveSettingsAsync(settings);

        // Configure root folders if TEST_DATA_LALAB exists
        string testDataRoot = Path.Combine(repoRoot, "TEST_DATA_LALAB");
        if (Directory.Exists(testDataRoot))
        {
            var rootRepo = sp.GetRequiredService<IRootFolderRepository>();
            var existingRoots = await rootRepo.GetAllAsync();
            if (!existingRoots.Any(r => string.Equals(r.FullPath, testDataRoot, StringComparison.OrdinalIgnoreCase)))
            {
                await rootRepo.InsertAsync(new RootFolder
                {
                    FullPath = testDataRoot,
                    Name = "TEST_DATA_LALAB",
                    IsActive = true,
                    IsDefault = true
                });
            }

            // Check if orders for 2026-09-29 exist; if fewer than 3, perform initial fixture scan
            var orderRepo = sp.GetRequiredService<IOrderRepository>();
            var existingOrders = await orderRepo.GetOrdersByDateAsync("2026-09-29");
            if (existingOrders.Count < 3)
            {
                var scanService = sp.GetRequiredService<IScanService>();
                await scanService.ScanDateAsync("2026-09-29");
            }
        }
    }

    private static async Task<bool> WaitForServerReadyAsync(int healthPort, int timeoutSeconds)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            try
            {
                var resp = await client.GetAsync("/health/ready");
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Server still binding or initializing
            }

            await Task.Delay(250);
        }

        return false;
    }

    #endregion

    #region Server Binary Resolution & Verification

    public record ServerBinaryInfo(string ExecutablePath, string BuildId, string GitSha);

    private static async Task<ServerBinaryInfo> EnsureServerBinaryAsync(string repoRoot)
    {
        string serverProjPath = Path.Combine(repoRoot, "src", "LalabAutoReport.Server", "LalabAutoReport.Server.csproj");
        if (!File.Exists(serverProjPath))
        {
            throw new FileNotFoundException($"Server project not found at: {serverProjPath}");
        }

        // Build Server in Release configuration
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{serverProjPath}\" -c Release --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var proc = Process.Start(psi))
        {
            if (proc == null) throw new InvalidOperationException("Failed to launch dotnet build for Server project.");
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"Server Release build failed (exit code {proc.ExitCode}):\n{stderr}\n{stdout}");
            }
        }

        string serverExe = Path.Combine(repoRoot, "src", "LalabAutoReport.Server", "bin", "Release", "net8.0-windows", "LalabAutoReport.Server.exe");
        if (!File.Exists(serverExe))
        {
            throw new FileNotFoundException($"Server executable not found at: {serverExe}");
        }

        // Resolve Git SHA
        string gitSha = "unknown";
        try
        {
            var psiGit = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = repoRoot
            };
            using var gitProc = Process.Start(psiGit);
            if (gitProc != null)
            {
                string sha = (await gitProc.StandardOutput.ReadToEndAsync()).Trim();
                await gitProc.WaitForExitAsync();
                if (gitProc.ExitCode == 0 && !string.IsNullOrWhiteSpace(sha))
                {
                    gitSha = sha;
                }
            }
        }
        catch { }

        var versionInfo = FileVersionInfo.GetVersionInfo(serverExe);
        string shortSha = gitSha.Length >= 8 ? gitSha[..8] : gitSha;
        string timestamp = File.GetLastWriteTimeUtc(serverExe).ToString("yyyyMMddHHmmss");
        string buildId = $"{versionInfo.ProductVersion ?? "2.0.0"}+{shortSha}_{timestamp}";

        return new ServerBinaryInfo(serverExe, buildId, gitSha);
    }

    #endregion

    #region Integrity Checks

    private static async Task<IntegrityCheckResult> VerifyDatabaseIntegrityAsync(string dbPath, string serverExe, string dataDir)
    {
        string pragmaResult = "unknown";
        int fkCount = -1;
        int partialReceipts = -1;
        int missingAudit = -1;
        int duplicateRevisionEffects = -1;
        int duplicateAuditEffects = -1;
        int duplicateOutboxEffects = -1;
        int falseSuccessCount = -1;

        // Allow Windows OS kernel to completely release file handles of terminated Server process
        await Task.Delay(1000);

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Default Timeout=10");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA integrity_check;";
                var res = await cmd.ExecuteScalarAsync();
                pragmaResult = res?.ToString() ?? "null";

                cmd.CommandText = "PRAGMA foreign_key_check;";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    fkCount = 0;
                    while (await reader.ReadAsync()) fkCount++;
                }

                // Incomplete / non-committed receipts
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM command_receipts
                    WHERE completed_at_utc IS NULL
                       OR status != 'Committed'
                       OR operation_id IS NULL
                       OR operation_id = '';";
                partialReceipts = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                // Receipts missing audit log row
                cmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM command_receipts r
                    LEFT JOIN audit_log a ON r.operation_id = a.operation_id
                    WHERE a.id IS NULL;";
                missingAudit = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                // Duplicate revision bumps in change_journal for same operation_id
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM (
                        SELECT operation_id, COUNT(*) as cnt
                        FROM change_journal
                        WHERE operation_id IS NOT NULL AND operation_id != ''
                        GROUP BY operation_id
                        HAVING cnt > 1
                    );";
                duplicateRevisionEffects = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                // Duplicate audit log rows for same operation_id
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM (
                        SELECT operation_id, COUNT(*) as cnt
                        FROM audit_log
                        WHERE operation_id IS NOT NULL AND operation_id != ''
                        GROUP BY operation_id
                        HAVING cnt > 1
                    );";
                duplicateAuditEffects = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                // Duplicate outbox entries for same operation_id
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM (
                        SELECT operation_id, COUNT(*) as cnt
                        FROM cloud_operational_outbox
                        WHERE operation_id IS NOT NULL AND operation_id != ''
                        GROUP BY operation_id
                        HAVING cnt > 1
                    );";
                duplicateOutboxEffects = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                // False success: receipt status Committed where audit outcome was not Committed/NoChange, or where Committed mutation has no change_journal row
                cmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM command_receipts r
                    JOIN audit_log a ON r.operation_id = a.operation_id
                    LEFT JOIN change_journal c ON r.operation_id = c.operation_id
                    WHERE (a.outcome NOT IN ('Committed', 'NoChange'))
                       OR (a.outcome = 'Committed' AND c.seq IS NULL);";
                falseSuccessCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                break;
            }
            catch (SqliteException ex) when (attempt < 10)
            {
                Console.WriteLine($"[Verification] SQLite open/pragma retry {attempt}/10 ({ex.Message}). Waiting 500ms...");
                await Task.Delay(500);
            }
        }

        SqliteConnection.ClearAllPools();

        // Run server inventory --dry-run
        bool dryRunOk = false;
        string dryRunJson = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{dataDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                dryRunJson = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                dryRunOk = (proc.ExitCode == 0);
            }
        }
        catch { }

        bool passed = string.Equals(pragmaResult, "ok", StringComparison.OrdinalIgnoreCase) &&
                      fkCount == 0 &&
                      partialReceipts == 0 &&
                      missingAudit == 0 &&
                      duplicateRevisionEffects == 0 &&
                      duplicateAuditEffects == 0 &&
                      duplicateOutboxEffects == 0 &&
                      falseSuccessCount == 0 &&
                      dryRunOk;

        return new IntegrityCheckResult
        {
            Passed = passed,
            PragmaIntegrity = pragmaResult,
            ForeignKeyViolations = fkCount,
            PartialReceiptCount = partialReceipts,
            InconsistentAuditCount = missingAudit,
            DuplicateRevisionEffects = duplicateRevisionEffects,
            DuplicateAuditEffects = duplicateAuditEffects,
            DuplicateOutboxEffects = duplicateOutboxEffects,
            FalseSuccessCount = falseSuccessCount,
            InventoryDryRunSuccess = dryRunOk,
            InventoryReportJson = dryRunJson
        };
    }

    #endregion

    #region Reporting & Percentiles

    private static LoadReport GenerateLoadReport(
        List<RequestMetric> metrics,
        List<ReplayMetric> replays,
        List<Stale409Metric> staleMetrics,
        int scanBursts,
        double elapsedSec,
        int clients,
        IntegrityCheckResult integrity,
        ReconciliationResult reconResult,
        VerdictResult verdict,
        ServerBinaryInfo serverInfo,
        bool syntheticFixtureUsed)
    {
        var readMetrics = metrics.Where(m => m.Category == "Read").ToList();
        var cmdMetrics = metrics.Where(m => m.Category == "Command").ToList();

        var readStats = CalculatePercentiles(readMetrics);
        var cmdStats = CalculatePercentiles(cmdMetrics);
        var allStats = CalculatePercentiles(metrics);

        int count200 = metrics.Count(m => m.StatusCode == 200);
        int count409 = metrics.Count(m => m.StatusCode == 409);
        int count503 = metrics.Count(m => m.StatusCode == 503);
        int countOther = metrics.Count(m => m.StatusCode != 200 && m.StatusCode != 409 && m.StatusCode != 503);

        double throughput = elapsedSec > 0 ? metrics.Count / elapsedSec : 0;

        int replaysSuccess = replays.Count(r => r.Matched);
        int staleRetriesSuccess = staleMetrics.Count(s => s.RetrySucceeded);
        int galleryRequests = metrics.Count(m => m.Endpoint.Contains("/gallery"));

        var jsonEvidence = new
        {
            verdict = new
            {
                passed = verdict.Passed,
                summary = verdict.Passed ? "PASS" : "FAIL",
                failureReasons = verdict.FailureReasons,
                passedCriteria = verdict.PassedCriteria
            },
            serverBinary = new
            {
                buildId = serverInfo.BuildId,
                gitSha = serverInfo.GitSha,
                executablePath = serverInfo.ExecutablePath
            },
            fixtureMode = new
            {
                syntheticFixture = syntheticFixtureUsed,
                description = syntheticFixtureUsed ? "Clean deterministic synthetic fixture (zero external DB dependency)" : "Source database snapshot"
            },
            testRun = new
            {
                timestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                durationSeconds = elapsedSec,
                concurrentClients = clients,
                totalRequests = metrics.Count,
                throughputReqPerSec = throughput,
                parallelScanBurstsExecuted = scanBursts,
                galleryRequestsExercised = galleryRequests
            },
            statusCounts = new
            {
                ok200 = count200,
                staleRevision409 = count409,
                serverBusy503 = count503,
                otherErrors = countOther
            },
            latencies = new
            {
                reads = readStats,
                commands = cmdStats,
                overall = allStats
            },
            idempotencyAndConcurrency = new
            {
                totalReplays = replays.Count,
                replaysSucceeded = replaysSuccess,
                replayMatchRatePercent = replays.Count > 0 ? (double)replaysSuccess / replays.Count * 100 : 0,
                duplicateRevisionEffects = integrity.DuplicateRevisionEffects,
                duplicateAuditEffects = integrity.DuplicateAuditEffects,
                duplicateOutboxEffects = integrity.DuplicateOutboxEffects,
                falseSuccessCount = integrity.FalseSuccessCount,
                total409StaleRevisions = staleMetrics.Count,
                staleRetriesSucceeded = staleRetriesSuccess
            },
            databaseIntegrity = new
            {
                passed = integrity.Passed,
                pragmaIntegrity = integrity.PragmaIntegrity,
                foreignKeyViolations = integrity.ForeignKeyViolations,
                partialReceipts = integrity.PartialReceiptCount,
                inconsistentAudit = integrity.InconsistentAuditCount,
                inventoryDryRunPassed = integrity.InventoryDryRunSuccess
            },
            reconciliation = new
            {
                passed = reconResult.Passed,
                expectedOperations = reconResult.ExpectedOperationCount,
                matchedReceipts = reconResult.MatchedReceiptCount,
                missingReceipts = reconResult.MissingReceiptCount,
                duplicateReceipts = reconResult.DuplicateReceiptCount,
                mismatchedReceipts = reconResult.MismatchedReceiptCount,
                missingAudits = reconResult.MissingAuditCount,
                orphanAudits = reconResult.OrphanAuditCount,
                missingJournalEffects = reconResult.MissingJournalEffectCount,
                duplicateAudits = reconResult.DuplicateAuditCount,
                duplicateJournals = reconResult.DuplicateJournalCount,
                duplicateOutbox = reconResult.DuplicateOutboxCount,
                missingReceiptSampleIds = reconResult.MissingReceiptSampleIds,
                orphanAuditSampleIds = reconResult.OrphanAuditSampleIds,
                missingJournalSampleIds = reconResult.MissingJournalSampleIds,
                mismatchedSampleIds = reconResult.MismatchedSampleIds,
                failureDetails = reconResult.FailureDetails
            }
        };

        var sb = new StringBuilder();
        sb.AppendLine("# Lalab Auto Report — Phase 2 B4 Load Test Report");
        sb.AppendLine();
        sb.AppendLine($"> **Test Execution Timestamp:** {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"> **Harness Profile:** {clients} concurrent clients, {elapsedSec:F1}s bounded run, parallel scan-shaped SQLite contention");
        sb.AppendLine($"> **Write Mode:** `--lan-writes orders` (Loopback port binding)");
        sb.AppendLine($"> **Server Revision:** BuildId: `{serverInfo.BuildId}` | Git SHA: `{serverInfo.GitSha}`");
        sb.AppendLine($"> **Fixture Mode:** {(syntheticFixtureUsed ? "Deterministic Synthetic Fixture (Clean Checkout Compatible)" : "Source Database Snapshot")}");
        sb.AppendLine($"> **Overall Measured Verdict:** **{(verdict.Passed ? "PASS" : "FAIL")}**");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 1. Executive Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Actual Result | Target (Plan §13) | Evidence Status |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| **Read Latency (p95)** | **{readStats.P95:F1} ms** | < 1,000 ms | {(readStats.P95 < 1000 ? "PASS (Observed)" : "EXCEEDED TARGET")} |");
        sb.AppendLine($"| **Command Latency (p95)** | **{cmdStats.P95:F1} ms** | < 2,000 ms | {(cmdStats.P95 < 2000 ? "PASS (Observed)" : "EXCEEDED TARGET")} |");
        sb.AppendLine($"| **Total Requests** | **{metrics.Count:N0}** | — | Completed |");
        sb.AppendLine($"| **Throughput** | **{throughput:F1} req/s** | — | Completed |");
        sb.AppendLine($"| **Status 200 OK** | **{count200:N0}** ({((double)count200 / Math.Max(1, metrics.Count) * 100):F1}%) | High | Healthy |");
        sb.AppendLine($"| **Status 409 Conflict** | **{count409:N0}** | Handled | Optimistic Concurrency |");
        sb.AppendLine($"| **Status 503 SERVER_BUSY** | **{count503:N0}** | Handled | Contention Backstop |");
        sb.AppendLine($"| **Other / Unexpected Errors** | **{countOther:N0}** | 0 | {(countOther == 0 ? "Zero" : "INVESTIGATE")} |");
        sb.AppendLine($"| **Command Reconciliation** | **{reconResult.MatchedReceiptCount:N0} / {reconResult.ExpectedOperationCount:N0}** | 100% Matching | {(reconResult.Passed ? "PASS (100%)" : "FAIL")} |");
        sb.AppendLine($"| **Overall Verdict** | **{(verdict.Passed ? "PASS" : "FAIL")}** | All Criteria Met | {(verdict.Passed ? "PASS" : "FAIL")} |");
        sb.AppendLine();

        if (!verdict.Passed)
        {
            sb.AppendLine("### Verdict Failure Reasons:");
            foreach (var reason in verdict.FailureReasons)
            {
                sb.AppendLine($"- ❌ {reason}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 2. Latency Distributions (Milliseconds)");
        sb.AppendLine();
        sb.AppendLine("| Category | Requests | Min (ms) | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) | Avg (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        sb.AppendLine($"| **Reads** (`/api/orders`, `/operational-state`, `/gallery`) | {readMetrics.Count:N0} | {readStats.Min:F1} | {readStats.P50:F1} | {readStats.P95:F1} | {readStats.P99:F1} | {readStats.Max:F1} | {readStats.Avg:F1} |");
        sb.AppendLine($"| **Commands** (`/delivered`, `/printed`, `/note`) | {cmdMetrics.Count:N0} | {cmdStats.Min:F1} | {cmdStats.P50:F1} | {cmdStats.P95:F1} | {cmdStats.P99:F1} | {cmdStats.Max:F1} | {cmdStats.Avg:F1} |");
        sb.AppendLine($"| **Overall** | {metrics.Count:N0} | {allStats.Min:F1} | {allStats.P50:F1} | {allStats.P95:F1} | {allStats.P99:F1} | {allStats.Max:F1} | {allStats.Avg:F1} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 3. Idempotency & Concurrency Validation");
        sb.AppendLine();
        sb.AppendLine("- **Same-Operation Replays under Load:**");
        sb.AppendLine($"  - Total replays attempted: **{replays.Count}**");
        sb.AppendLine($"  - Exact match responses: **{replaysSuccess}** ({((double)replaysSuccess / Math.Max(1, replays.Count) * 100):F1}%)");
        sb.AppendLine($"  - Duplicate revision bump effects (queried from DB): **{integrity.DuplicateRevisionEffects}**");
        sb.AppendLine($"  - Duplicate audit log effects (queried from DB): **{integrity.DuplicateAuditEffects}**");
        sb.AppendLine($"  - Duplicate outbox effects (queried from DB): **{integrity.DuplicateOutboxEffects}**");
        sb.AppendLine();
        sb.AppendLine("- **Optimistic Concurrency & 409 STALE_REVISION Handling:**");
        sb.AppendLine($"  - Stale revision conflicts encountered: **{staleMetrics.Count}**");
        sb.AppendLine($"  - Re-read operational state + retry succeeded: **{staleRetriesSuccess}** ({((double)staleRetriesSuccess / Math.Max(1, staleMetrics.Count) * 100):F1}%)");
        sb.AppendLine($"  - False success entries (queried from DB): **{integrity.FalseSuccessCount}**");
        sb.AppendLine();
        sb.AppendLine("- **Scan-Shaped Write Contention Simulation (Q-B7):**");
        sb.AppendLine($"  - Parallel short-transaction write bursts executed: **{scanBursts}**");
        sb.AppendLine($"  - Resulting `503 SERVER_BUSY` responses: **{count503}** (Server translated `SQLITE_BUSY` to retryable `503 SERVER_BUSY` with zero crashed connections).");
        sb.AppendLine("  - Note: Real AutoScan coordinator is intentionally disabled in the server profile (Q-B7); this background worker modeled scan write pressure on the shared SQLite database.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 4. Post-Load Database Integrity Verification");
        sb.AppendLine();
        sb.AppendLine("| Check | Expected | Actual | Result |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| `PRAGMA integrity_check` | ok | {integrity.PragmaIntegrity} | {(string.Equals(integrity.PragmaIntegrity, "ok", StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| `PRAGMA foreign_key_check` | 0 rows | {integrity.ForeignKeyViolations} violations | {(integrity.ForeignKeyViolations == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Incomplete / Non-committed Receipts | 0 | {integrity.PartialReceiptCount} | {(integrity.PartialReceiptCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Receipts missing Audit Log row | 0 | {integrity.InconsistentAuditCount} | {(integrity.InconsistentAuditCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Duplicate Revision Bump Effects | 0 | {integrity.DuplicateRevisionEffects} | {(integrity.DuplicateRevisionEffects == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Duplicate Audit Log Effects | 0 | {integrity.DuplicateAuditEffects} | {(integrity.DuplicateAuditEffects == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Duplicate Outbox Effects | 0 | {integrity.DuplicateOutboxEffects} | {(integrity.DuplicateOutboxEffects == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| False Success Entries | 0 | {integrity.FalseSuccessCount} | {(integrity.FalseSuccessCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Server `inventory --dry-run` | Exit 0 | {(integrity.InventoryDryRunSuccess ? "Exit 0 (Success)" : "Non-zero")} | {(integrity.InventoryDryRunSuccess ? "PASS" : "FAIL")} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 5. Client Command Reconciliation & Double-Entry Integrity");
        sb.AppendLine();
        sb.AppendLine("| Verification Check | Target | Actual | Result |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| Expected Operations Recorded by Clients | > 0 | {reconResult.ExpectedOperationCount:N0} | Completed |");
        sb.AppendLine($"| Exact Matching Receipts in `command_receipts` | 100% | {reconResult.MatchedReceiptCount:N0} / {reconResult.ExpectedOperationCount:N0} | {(reconResult.MissingReceiptCount == 0 && reconResult.MismatchedReceiptCount == 0 ? "PASS (100%)" : "FAIL")} |");
        sb.AppendLine($"| Missing Command Receipts | 0 | {reconResult.MissingReceiptCount} | {(reconResult.MissingReceiptCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Duplicate Command Receipts | 0 | {reconResult.DuplicateReceiptCount} | {(reconResult.DuplicateReceiptCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Mismatched Receipt Status/Payload | 0 | {reconResult.MismatchedReceiptCount} | {(reconResult.MismatchedReceiptCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Missing Audit Log Entries | 0 | {reconResult.MissingAuditCount} | {(reconResult.MissingAuditCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Orphan Audit Log Entries (No Receipt) | 0 | {reconResult.OrphanAuditCount} | {(reconResult.OrphanAuditCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Missing Change Journal Effects (Committed) | 0 | {reconResult.MissingJournalEffectCount} | {(reconResult.MissingJournalEffectCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Duplicate Change Journal Entries | 0 | {reconResult.DuplicateJournalCount} | {(reconResult.DuplicateJournalCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine($"| Duplicate Outbox Entries | 0 | {reconResult.DuplicateOutboxCount} | {(reconResult.DuplicateOutboxCount == 0 ? "PASS" : "FAIL")} |");
        sb.AppendLine();
        sb.AppendLine("> **Reconciliation Matching Rule:** Client response JSON and stored DB `result_json` are compared using full recursive canonical JSON equality. Top-level keys (`orderId`, `fieldGroup`, `revision`, `changed`, `operationId`, `syncState`) and the full nested `value` object (`delivered`/`printed`/`note` metadata) are strictly validated for complete schema presence. Property names are compared case-insensitively (handling PascalCase in SQLite receipts vs camelCase in HTTP responses), keys are sorted deterministically, numbers are compared invariants, and empty `{}` or truncated objects are rejected.");
        sb.AppendLine();

        if (reconResult.FailureDetails.Count > 0)
        {
            sb.AppendLine("### Reconciliation Failure Details:");
            foreach (var fail in reconResult.FailureDetails)
            {
                sb.AppendLine($"- ❌ {fail}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 6. Artifacts and Raw Output");
        sb.AppendLine();
        sb.AppendLine("- JSON Evidence: `b4_load_evidence.json`");
        sb.AppendLine("- Server Inventory Snapshot after load:");
        sb.AppendLine("```json");
        sb.AppendLine(string.IsNullOrWhiteSpace(integrity.InventoryReportJson) ? "{}" : integrity.InventoryReportJson.Trim());
        sb.AppendLine("```");

        return new LoadReport
        {
            MarkdownContent = sb.ToString(),
            JsonEvidence = jsonEvidence,
            ReadStats = readStats,
            CommandStats = cmdStats,
            AllStats = allStats,
            Throughput = throughput,
            TotalCount = metrics.Count,
            Status200 = count200,
            Status409 = count409,
            Status503 = count503,
            StatusOther = countOther,
            Reconciliation = reconResult
        };
    }

    private static LatencyStats CalculatePercentiles(List<RequestMetric> metrics)
    {
        if (metrics.Count == 0) return new LatencyStats();
        var sorted = metrics.Select(m => m.LatencyMs).OrderBy(x => x).ToList();
        return new LatencyStats
        {
            Count = sorted.Count,
            Min = sorted[0],
            P50 = sorted[(int)Math.Floor(sorted.Count * 0.50)],
            P95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))],
            P99 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.99))],
            Max = sorted[^1],
            Avg = sorted.Average()
        };
    }

    private static void PrintConsoleSummary(LoadReport report, VerdictResult verdict, ReconciliationResult recon)
    {
        Console.WriteLine("\n===============================================================================");
        Console.WriteLine("                        B4 LOAD TEST SUMMARY RESULTS                           ");
        Console.WriteLine("===============================================================================");
        Console.WriteLine($"OVERALL VERDICT:          {(verdict.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Total Completed Requests: {report.TotalCount:N0} in {report.ReadStats.Count + report.CommandStats.Count} logged calls");
        Console.WriteLine($"Throughput:               {report.Throughput:F1} req/sec");
        Console.WriteLine($"Status Breakdown:         200 OK: {report.Status200} | 409 Conflict: {report.Status409} | 503 Busy: {report.Status503} | Other: {report.StatusOther}");
        Console.WriteLine($"Reconciliation:           {recon.MatchedReceiptCount:N0}/{recon.ExpectedOperationCount:N0} matched (Missing: {recon.MissingReceiptCount}, Mismatched: {recon.MismatchedReceiptCount}, Orphan Audits: {recon.OrphanAuditCount}, Missing Journals: {recon.MissingJournalEffectCount})");
        Console.WriteLine("-------------------------------------------------------------------------------");
        Console.WriteLine($"READS    (p50 / p95 / p99): {report.ReadStats.P50:F1} ms / {report.ReadStats.P95:F1} ms / {report.ReadStats.P99:F1} ms (Target p95 < 1000ms)");
        Console.WriteLine($"COMMANDS (p50 / p95 / p99): {report.CommandStats.P50:F1} ms / {report.CommandStats.P95:F1} ms / {report.CommandStats.P99:F1} ms (Target p95 < 2000ms)");
        Console.WriteLine($"OVERALL  (p50 / p95 / p99): {report.AllStats.P50:F1} ms / {report.AllStats.P95:F1} ms / {report.AllStats.P99:F1} ms");

        if (!verdict.Passed)
        {
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.WriteLine("VERDICT FAILURE REASONS:");
            foreach (var reason in verdict.FailureReasons)
            {
                Console.WriteLine($"  [!] {reason}");
            }
        }

        Console.WriteLine("===============================================================================");
    }

    private static string GenerateReadmeContent()
    {
        return @"# Phase 2 B4 Load Harness

## Overview
This console application executes the bounded 10-client concurrency and load test against a real `LalabAutoReport.Server.exe` process running in `--lan-writes orders` mode on free loopback ports.

The harness evaluates an explicit overall **PASS / FAIL** verdict derived from measured coverage, error rates, 100% idempotency replay fidelity, latency SLAs (reads < 1000ms, commands < 2000ms), zero duplicate effects in change journal/audit log/outbox, zero simulator exceptions, SQLite database integrity checks, and 100% client-to-DB command reconciliation.

## Clean-Checkout Compatibility
The harness works out-of-the-box from a fresh checkout with zero external dependencies:
1. If `--source-db` is specified, it uses that database.
2. Otherwise, if `artifacts/acceptance/profile/lalab_autoreport.db` exists, it uses it.
3. Otherwise (or when `--fresh-fixture` is set), it automatically creates a fresh SQLite database, seeds schema migrations, creates a deterministic synthetic root directory with valid test JPEGs, executes an initial scan date, and cleans up completely after execution. Live application databases (`%LOCALAPPDATA%`) are never touched.

## Workload Characteristics
- **Concurrency:** 10 concurrent async client tasks
- **Workload Mix:**
  - Reads: `GET /api/orders?date=2026-09-29`, `GET /api/v1/orders/{id}/operational-state`, `POST /api/orders/{id}/gallery/requests`, `GET /api/orders/{id}/gallery`
  - Commands: `POST /api/v1/orders/{id}/delivered`, `POST /api/v1/orders/{id}/printed`, `POST /api/v1/orders/{id}/note` with unique `operationId` + `expectedRevision`
  - Optimistic Concurrency: `409 STALE_REVISION` is handled by re-reading operational state and retrying once
  - Idempotency Validation: Client 0 executes immediate same-operation replays asserting identical 200 OK responses with zero duplicate revision increments
  - Simulated Scan Contention: Parallel background worker executes brief guarded write transactions to simulate scan write pressure on SQLite WAL (Q-B7)
- **Integrity & Reconciliation Validation:**
  - PRAGMA integrity_check & foreign_key_check
  - Zero partial/uncommitted receipts
  - Client-side recorded operations reconciled against `command_receipts`, `audit_log`, and `change_journal` (100% receipt presence, matching outcomes, and zero orphan audit rows)
  - Zero duplicate revision/audit/outbox effects
  - Server `inventory --dry-run`

## How to Run

From repository root:
```powershell
# 1. Clean-checkout simulation with fresh synthetic fixture (60s, 10 clients):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 60 --clients 10

# 2. Standard run (uses profile DB if available, else synthetic fixture):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release

# 3. Custom duration or client count:
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --duration 90 --clients 10

# 4. Injected auth failure verification (verifies non-zero exit code on auth error):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --inject-auth-failure --duration 5 --clients 2

# 5. Fault injection verification (verifies non-zero exit code when receipts or audits are compromised):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-delete-receipts 1
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-delete-receipts all
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-corrupt-receipt-results 1
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-corrupt-receipt-results all
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-orphan-audit
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-drop-receipt-after-http

# 6. Keep temporary directory for post-run forensic inspection:
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --keep-temp
```

## Evidence Output
Output files are written directly to `docs/qa-evidence-phase2/`:
- `B4_LOAD_REPORT.md` — Complete markdown report with percentiles, status counts, measured verdict, reconciliation details, and integrity proof.
- `b4_load_evidence.json` — Raw JSON metrics, server buildId, git SHA, reconciliation stats, and verdict criteria.
- `README.md` — Execution instructions and reproduction documentation.
";
    }

    #endregion

    #region Helpers & Models

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "LalabAutoReport.slnx")) || File.Exists(Path.Combine(dir, "LalabAutoReport.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        return Directory.GetCurrentDirectory();
    }

    public class RequestMetric
    {
        public required string Endpoint { get; init; }
        public required string Category { get; init; }
        public required int StatusCode { get; init; }
        public required double LatencyMs { get; init; }
        public bool IsReplay { get; init; }
        public bool IsRetry { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public class ReplayMetric
    {
        public required string OperationId { get; init; }
        public required long OrderId { get; init; }
        public required int InitialStatusCode { get; init; }
        public required int ReplayStatusCode { get; init; }
        public required bool Matched { get; init; }
    }

    public class Stale409Metric
    {
        public required long OrderId { get; init; }
        public required string FieldGroup { get; init; }
        public required string OriginalOpId { get; init; }
        public required string RetryOpId { get; init; }
        public required int RetryStatusCode { get; init; }
        public required bool RetrySucceeded { get; init; }
    }

    public class LatencyStats
    {
        public int Count { get; init; }
        public double Min { get; init; }
        public double P50 { get; init; }
        public double P95 { get; init; }
        public double P99 { get; init; }
        public double Max { get; init; }
        public double Avg { get; init; }
    }

    public class IntegrityCheckResult
    {
        public bool Passed { get; init; }
        public string PragmaIntegrity { get; init; } = "";
        public int ForeignKeyViolations { get; init; }
        public int PartialReceiptCount { get; init; }
        public int InconsistentAuditCount { get; init; }
        public int DuplicateRevisionEffects { get; init; }
        public int DuplicateAuditEffects { get; init; }
        public int DuplicateOutboxEffects { get; init; }
        public int FalseSuccessCount { get; init; }
        public bool InventoryDryRunSuccess { get; init; }
        public string InventoryReportJson { get; init; } = "";
    }

    public class LoadReport
    {
        public string MarkdownContent { get; init; } = "";
        public object JsonEvidence { get; init; } = new();
        public LatencyStats ReadStats { get; init; } = new();
        public LatencyStats CommandStats { get; init; } = new();
        public LatencyStats AllStats { get; init; } = new();
        public double Throughput { get; init; }
        public int TotalCount { get; init; }
        public int Status200 { get; init; }
        public int Status409 { get; init; }
        public int Status503 { get; init; }
        public int StatusOther { get; init; }
        public ReconciliationResult Reconciliation { get; init; } = new();
    }

    #endregion
}
