using System;
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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class MobileSpaScriptSyntaxTests
{
    [Fact]
    public void GetIndexHtml_ContainsValidHtmlStructure()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();

        html.Should().NotBeNullOrWhiteSpace();
        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("<title>Lalab Mobile</title>");
        html.Should().Contain("<script>");
        html.Should().Contain("</script>");
        html.Should().Contain("id=\"ordersSection\"");
        html.Should().Contain("id=\"reportsSection\"");
        html.Should().Contain("id=\"debtsSection\"");
        html.Should().Contain("id=\"dateToast\"");
        html.Should().Contain("navigateDay(-1)");
        html.Should().Contain("navigateDay(1)");
    }

    [Fact]
    public void GetIndexHtml_JavaScriptBlock_HasBalancedBrackets()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();
        var match = Regex.Match(html, @"(?s)<script>(.*?)</script>");
        match.Success.Should().BeTrue("HTML must contain an inline <script> block");

        string script = match.Groups[1].Value;

        int openCurly = CountChar(script, '{');
        int closeCurly = CountChar(script, '}');
        openCurly.Should().Be(closeCurly, $"Curly braces must be balanced (Found {{: {openCurly}, }}: {closeCurly})");

        int openParen = CountChar(script, '(');
        int closeParen = CountChar(script, ')');
        openParen.Should().Be(closeParen, $"Parentheses must be balanced (Found (: {openParen}, ): {closeParen})");

        int openSquare = CountChar(script, '[');
        int closeSquare = CountChar(script, ']');
        openSquare.Should().Be(closeSquare, $"Square brackets must be balanced (Found [: {openSquare}, ]: {closeSquare})");
    }

    [Fact]
    public void GetIndexHtml_ContainsAllEssentialGlobalFunctions()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();
        var match = Regex.Match(html, @"(?s)<script>(.*?)</script>");
        string script = match.Groups[1].Value;

        var requiredFunctions = new[]
        {
            "checkAuthAndLoad",
            "submitPin",
            "logout",
            "setToday",
            "setThisMonth",
            "loadOrders",
            "renderOrders",
            "setFilterMode",
            "filterOrders",
            "navigateDay",
            "showDateToast",
            "setupSwipeGestures",
            "viewBill",
            "viewLabel",
            "viewOrderLabel",
            "printOrderLabel",
            "printBillLabel",
            "showBillQr",
            "closePreviewModal",
            "switchTab",
            "loadMonthlyReport",
            "loadDebts"
        };

        foreach (var fn in requiredFunctions)
        {
            script.Should().Contain($"function {fn}", $"Function '{fn}' must be defined in the mobile script");
        }
    }

    [Fact]
    public void GetIndexHtml_PrintedStatusIsReadOnly_AndHeaderDoesNotContainDeliveryBadgeBelowPrintedStatus()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();

        // Print status badge must be display-only, not a clickable button that toggles state
        html.Should().NotContain("togglePrinted(${o.id})", "Đã in/Chờ in badge must not be clickable to toggle state");
        html.Should().Contain("badge-printed", "Should still show printed badge class");
        html.Should().Contain("badge-pending", "Should still show pending badge class");

        // Delivery badge under printed status must be removed
        html.Should().NotContain("${deliveryBadge}", "Delivery badge under printed status header must be removed");

        // Action button at the bottom for delivery toggle must still exist
        html.Should().Contain("toggleDelivered(${o.id})", "Bottom action buttons should still allow toggling delivery");
    }

    [Fact]
    public void GetIndexHtml_JavaScriptSyntax_PassesNodeCheck()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();
        var match = Regex.Match(html, @"(?s)<script>(.*?)</script>");
        string script = match.Groups[1].Value;

        string tempJsPath = Path.Combine(Path.GetTempPath(), $"lalab_mobile_test_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempJsPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"--check \"{tempJsPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);
                proc.ExitCode.Should().Be(0, $"Node.js syntax validation failed: {err}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Node.js is optional in some environments; test gracefully skips external process check
        }
        finally
        {
            if (File.Exists(tempJsPath))
            {
                try { File.Delete(tempJsPath); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Staff")]
    public void ActualLanScript_SuccessfulLoginClosesPinModalAndLoadsOrders(string role)
    {
        string script = Regex.Match(MobileSpaHtmlProvider.GetIndexHtml(), @"(?s)<script>(.*?)</script>").Groups[1].Value;
        string fixture = @"
const assert = require('node:assert/strict');
const nodes = new Map();
global.window = {addEventListener(){}};
global.localStorage = {getItem(){return 'fixture-token'}};
global.document = {getElementById(id){if(!nodes.has(id)) nodes.set(id,{value:'',innerText:'',innerHTML:'',classList:{hidden:false,add(){this.hidden=true},remove(){this.hidden=false}},focus(){},addEventListener(){}});return nodes.get(id)}};
global.fetch = async () => ({ok:true,json:async()=>({role:ROLE,workshopName:'Fixture Workshop'})});
".Replace("ROLE", System.Text.Json.JsonSerializer.Serialize(role));
        string assertions = @"
let didLoad = false; loadOrders = () => {didLoad=true};
checkAuthAndLoad().then(()=>{assert.equal(nodes.get('pinModal').classList.hidden,true); assert.equal(didLoad,true); assert.match(nodes.get('roleBadge').innerHTML,/LAN/); assert.equal(nodes.get('workshopTitle').innerText,'Fixture Workshop')}).catch(e=>{console.error(e);process.exitCode=1});
";
        string path = Path.Combine(Path.GetTempPath(), "lalab_login_" + Guid.NewGuid().ToString("N") + ".cjs");
        try
        {
            File.WriteAllText(path, fixture + script + assertions);
            using var process = Process.Start(new ProcessStartInfo("node", $"\"{path}\"") { RedirectStandardError=true,RedirectStandardOutput=true,UseShellExecute=false,CreateNoWindow=true })!;
            string error = process.StandardError.ReadToEnd(); process.WaitForExit(5000);
            Assert.True(process.ExitCode == 0,error);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Staff")]
    public void ActualLanScript_ViewBillRendersAdminAndStaffWithoutUndefinedOrLeakage(string role)
    {
        string script = Regex.Match(MobileSpaHtmlProvider.GetIndexHtml(), @"(?s)<script>(.*?)</script>").Groups[1].Value;
        string fixture = @"
const assert = require('node:assert/strict');
const nodes = new Map();
global.currentRole = ROLE;
global.authToken = 'fixture-token';
global.localStorage = {getItem(){return 'fixture-token'}, setItem(){}};
global.navigator = {};
global.window = {addEventListener(){}};
global.document = {getElementById(id){if(!nodes.has(id)) nodes.set(id,{value:'',innerText:'',innerHTML:'',classList:{hidden:false,add(){this.hidden=true},remove(){this.hidden=false}},focus(){},addEventListener(){}});return nodes.get(id)}};
const billData = {
  id: 1, billNumber: 'AUDIT-1', billType: 'Customer', customerName: 'Audit <Safe> Customer',
  periodEnd: '2026-10-02', status: 'Draft', isPaid: false, paidAt: null,
  hasExportFile: ROLE === 'Admin', hasPaymentQr: ROLE === 'Admin',
  productSubtotal: ROLE === 'Admin' ? 75000 : null,
  adjustmentsTotal: ROLE === 'Admin' ? 0 : null,
  grandTotal: ROLE === 'Admin' ? 75000 : null,
  lines: [{ id: 1, description: 'Photo & Print', size: '10x15', quantity: 15, unitPrice: ROLE === 'Admin' ? 5000 : null, lineTotal: ROLE === 'Admin' ? 75000 : null }],
  adjustments: ROLE === 'Admin' ? [] : null
};
global.fetch = async () => ({ok: true, json: async () => billData});
";

        string assertions = @"
currentRole = ROLE;
viewBill(1).then(()=>{
  const body = nodes.get('previewModalBody').innerHTML;
  assert.ok(body.includes('Audit &lt;Safe&gt; Customer'), 'Escaped customer name must be present');
  assert.ok(body.includes('AUDIT-1'), 'Bill number must be present');
  assert.ok(body.includes('2026-10-02'), 'Period date must be present');
  assert.ok(body.includes('Photo &amp; Print'), 'Line description must be present and escaped');
  assert.ok(!body.includes('undefined'), 'No literal undefined');
  assert.ok(!body.includes('NaN'), 'No literal NaN');

  if (currentRole === 'Admin') {
    assert.ok(body.includes('75.000'), 'Admin sees total amount');
    assert.ok(body.includes('Xác nhận ĐÃ THU'), 'Admin sees payment button');
    assert.ok(body.includes('/api/bills/1/image?token=fixture-token'), 'Admin sees export image link');
    assert.ok(body.includes('showBillQr(1)'), 'Admin sees QR button');
  } else {
    assert.ok(!body.includes('75.000'), 'Staff must not see total amount');
    assert.ok(!body.includes('5.000'), 'Staff must not see unit price');
    assert.ok(!body.includes('Xác nhận ĐÃ THU'), 'Staff must not see payment button');
    assert.ok(!body.includes('/api/bills/1/image'), 'Staff must not see export image link');
    assert.ok(!body.includes('showBillQr'), 'Staff must not see QR button');
  }
}).catch(e=>{console.error(e);process.exitCode=1});
";
        string path = Path.Combine(Path.GetTempPath(), "lalab_viewbill_" + Guid.NewGuid().ToString("N") + ".cjs");
        try
        {
            string fullScript = (fixture + script + assertions).Replace("ROLE", System.Text.Json.JsonSerializer.Serialize(role));
            File.WriteAllText(path, fullScript);
            using var process = Process.Start(new ProcessStartInfo("node", $"\"{path}\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            Assert.True(process.ExitCode == 0, error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetIndexHtml_UsesApiV1SetStateEndpointsAndHeaders()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();
        var match = Regex.Match(html, @"(?s)<script>(.*?)</script>");
        match.Success.Should().BeTrue();
        string script = match.Groups[1].Value;

        // V1 command endpoints
        script.Should().Contain("/api/v1/orders/' + orderId + '/delivered");
        script.Should().Contain("/api/v1/orders/' + orderId + '/printed");
        script.Should().Contain("/api/v1/orders/' + orderId + '/note");
        script.Should().Contain("/api/v1/operations/");

        // Client API version header
        script.Should().Contain("'X-Lalab-Client-Api': '1.0'");

        // OperationId and revision tracking
        script.Should().Contain("operationId");
        script.Should().Contain("expectedRevision");
        script.Should().Contain("deliveredRevision");
        script.Should().Contain("printedRevision");
        script.Should().Contain("noteRevision");

        // Conflict resolution Vietnamese text
        script.Should().Contain("Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại.");
        html.Should().Contain("Ghi chú đã được đổi trên thiết bị khác — xem lại trước khi lưu");
        html.Should().Contain("id=\"noteConflictNotice\"");
        html.Should().Contain("id=\"noteConflictServerText\"");
    }

    [Fact]
    public void GetIndexHtml_GenerateOperationId_UsesGetRandomValuesAndNoRandomUuid()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();
        var match = Regex.Match(html, @"(?s)<script>(.*?)</script>");
        match.Success.Should().BeTrue();
        string script = match.Groups[1].Value;

        script.Should().NotContain("randomUUID");
        script.Should().Contain("crypto.getRandomValues");
    }

    [Fact]
    public void GetIndexHtml_NoQueryTokenUsedOnJsonOrWriteEndpoints()
    {
        string html = MobileSpaHtmlProvider.GetIndexHtml();
        var match = Regex.Match(html, @"(?s)<script>(.*?)</script>");
        match.Success.Should().BeTrue();
        string script = match.Groups[1].Value;

        // Find all fetch calls in the script
        var fetchMatches = Regex.Matches(script, @"fetch\s*\(([^,)]+)");
        foreach (Match fetchMatch in fetchMatches)
        {
            string urlExpr = fetchMatch.Groups[1].Value;
            // Token query parameter must never appear in fetch URLs
            urlExpr.Should().NotContain("token=", "fetch URL should not pass token as query parameter; use Authorization Bearer");
        }
    }

    [Fact]
    public void ActualLanScript_DeliveredAndNoteUseApiV1WithOperationIdAndRevision()
    {
        string script = Regex.Match(MobileSpaHtmlProvider.GetIndexHtml(), @"(?s)<script>(.*?)</script>").Groups[1].Value;
        string fixture = @"
const assert = require('node:assert/strict');
const nodes = new Map();
global.currentRole = 'Staff';
global.authToken = 'test-token-123';
global.localStorage = { getItem(k){ if (k === 'lalab_token') return 'test-token-123'; return null; }, setItem(){} };
global.navigator = { vibrate(){} };
global.window = { addEventListener(){} };
global.document = {
  getElementById(id) {
    if (!nodes.has(id)) nodes.set(id, {
      value: '', innerText: '', innerHTML: '',
      classList: { hidden: false, add(c){ if(c==='hidden') this.hidden=true; }, remove(c){ if(c==='hidden') this.hidden=false; }, contains(c){ return c==='hidden' ? !!this.hidden : false; } },
      focus(){}, addEventListener(){}
    });
    return nodes.get(id);
  }
};

const recordedRequests = [];
global.fetch = async (url, options) => {
  recordedRequests.push({ url, options });
  if (url.includes('/api/v1/orders/42/delivered')) {
    return {
      ok: true,
      json: async () => ({
        orderId: 42,
        fieldGroup: 'delivered',
        revision: 4,
        value: { delivered: true, deliveredAt: '2026-10-26T12:00:00Z', deliveredBy: 'Nhân viên' },
        changed: true
      })
    };
  }
  if (url.includes('/api/v1/orders/42/note')) {
    return {
      ok: true,
      json: async () => ({
        orderId: 42,
        fieldGroup: 'note',
        revision: 6,
        value: { note: 'New updated note' },
        changed: true
      })
    };
  }
  return { ok: true, json: async () => ({}) };
};
";
        string assertions = @"
(async () => {
  currentRole = 'Staff';
  authToken = 'test-token-123';
  cachedOrders = [{
    id: 42,
    customerName: 'Test Customer',
    folderName: '2026-10-26/Test',
    isDelivered: false,
    deliveredRevision: 3,
    isPrinted: false,
    printedRevision: 1,
    note: 'Existing note',
    noteRevision: 5,
    items: []
  }];

  // 1. Test toggleDelivered
  await toggleDelivered(42);
  assert.equal(recordedRequests.length, 1);
  const delReq = recordedRequests[0];
  assert.equal(delReq.url, '/api/v1/orders/42/delivered');
  assert.equal(delReq.options.method, 'POST');
  assert.equal(delReq.options.headers['X-Lalab-Client-Api'], '1.0');
  assert.equal(delReq.options.headers['Authorization'], 'Bearer test-token-123');
  const delBody = JSON.parse(delReq.options.body);
  assert.equal(delBody.expectedRevision, 3);
  assert.equal(delBody.delivered, true);
  assert.match(delBody.operationId, /^[0-9a-f-]{36}$/i);
  assert.equal(cachedOrders[0].isDelivered, true);
  assert.equal(cachedOrders[0].deliveredRevision, 4);

  // 2. Test saveNote
  openNoteModal(42);
  document.getElementById('noteInput').value = 'New updated note';
  await saveNote();
  assert.equal(recordedRequests.length, 2);
  const noteReq = recordedRequests[1];
  assert.equal(noteReq.url, '/api/v1/orders/42/note');
  assert.equal(noteReq.options.method, 'POST');
  assert.equal(noteReq.options.headers['X-Lalab-Client-Api'], '1.0');
  const noteBody = JSON.parse(noteReq.options.body);
  assert.equal(noteBody.expectedRevision, 5);
  assert.equal(noteBody.note, 'New updated note');
  assert.match(noteBody.operationId, /^[0-9a-f-]{36}$/i);
  assert.equal(cachedOrders[0].note, 'New updated note');
  assert.equal(cachedOrders[0].noteRevision, 6);
  assert.equal(document.getElementById('noteModal').classList.hidden, true);
})().catch(e => { console.error(e); process.exitCode = 1; });
";
        string path = Path.Combine(Path.GetTempPath(), "lalab_v1_ops_" + Guid.NewGuid().ToString("N") + ".cjs");
        try
        {
            File.WriteAllText(path, fixture + script + assertions);
            using var process = Process.Start(new ProcessStartInfo("node", $"\"{path}\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            Assert.True(process.ExitCode == 0, error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActualLanScript_409StaleRevisionPreservesNoteDraftAndShowsConflict()
    {
        string script = Regex.Match(MobileSpaHtmlProvider.GetIndexHtml(), @"(?s)<script>(.*?)</script>").Groups[1].Value;
        string fixture = @"
const assert = require('node:assert/strict');
const nodes = new Map();
global.currentRole = 'Staff';
global.authToken = 'test-token-123';
global.localStorage = { getItem(){ return 'test-token-123'; }, setItem(){} };
global.navigator = { vibrate(){} };
global.window = { addEventListener(){} };
global.document = {
  getElementById(id) {
    if (!nodes.has(id)) nodes.set(id, {
      value: '', innerText: '', innerHTML: '',
      classList: { hidden: false, add(c){ if(c==='hidden') this.hidden=true; }, remove(c){ if(c==='hidden') this.hidden=false; }, contains(c){ return c==='hidden' ? !!this.hidden : false; } },
      focus(){}, addEventListener(){}
    });
    return nodes.get(id);
  }
};

global.fetch = async (url, options) => {
  if (url.includes('/api/v1/orders/42/note')) {
    return {
      ok: false,
      status: 409,
      json: async () => ({
        code: 'STALE_REVISION',
        message: 'Đơn vừa được cập nhật trên thiết bị khác.',
        entityType: 'order',
        entityId: 42,
        fieldGroup: 'note',
        currentRevision: 3,
        currentValue: { note: 'Server updated note from other client' }
      })
    };
  }
  if (url.includes('/api/orders?date=')) {
    return {
      ok: true,
      json: async () => [{
        id: 42,
        customerName: 'Conflict Customer',
        folderName: '2026-10-26/Conflict',
        isDelivered: false,
        deliveredRevision: 1,
        note: 'Server updated note from other client',
        noteRevision: 3,
        items: []
      }]
    };
  }
  return { ok: true, json: async () => ({}) };
};
";
        string assertions = @"
(async () => {
  currentRole = 'Staff';
  authToken = 'test-token-123';
  cachedOrders = [{
    id: 42,
    customerName: 'Conflict Customer',
    folderName: '2026-10-26/Conflict',
    isDelivered: false,
    deliveredRevision: 1,
    note: 'Old note',
    noteRevision: 2,
    items: []
  }];

  openNoteModal(42);
  document.getElementById('noteInput').value = 'My unsaved local draft note';

  await saveNote();

  // Modal must NOT be closed on conflict
  assert.equal(document.getElementById('noteModal').classList.hidden, false, 'Modal must remain open on 409 conflict');

  // User draft in noteInput must be strictly preserved
  assert.equal(document.getElementById('noteInput').value, 'My unsaved local draft note', 'User draft text must not be overwritten');

  // Conflict notice must be visible
  assert.equal(document.getElementById('noteConflictNotice').classList.hidden, false, 'Conflict notice must be shown');
  assert.ok(document.getElementById('noteConflictServerText').innerText.includes('Server updated note from other client'));

  // Local cached order noteRevision should be updated to server revision so next save will succeed
  assert.equal(cachedOrders[0].noteRevision, 3);
})().catch(e => { console.error(e); process.exitCode = 1; });
";
        string path = Path.Combine(Path.GetTempPath(), "lalab_conflict_" + Guid.NewGuid().ToString("N") + ".cjs");
        try
        {
            File.WriteAllText(path, fixture + script + assertions);
            using var process = Process.Start(new ProcessStartInfo("node", $"\"{path}\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            Assert.True(process.ExitCode == 0, error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActualLanScript_NetworkFailureChecksOperationReceipt()
    {
        string script = Regex.Match(MobileSpaHtmlProvider.GetIndexHtml(), @"(?s)<script>(.*?)</script>").Groups[1].Value;
        string fixture = @"
const assert = require('node:assert/strict');
const nodes = new Map();
global.currentRole = 'Staff';
global.authToken = 'test-token-123';
global.localStorage = { getItem(){ return 'test-token-123'; }, setItem(){} };
global.navigator = { vibrate(){} };
global.window = { addEventListener(){} };
global.document = {
  getElementById(id) {
    if (!nodes.has(id)) nodes.set(id, {
      value: '', innerText: '', innerHTML: '',
      classList: { hidden: false, add(){}, remove(){}, contains(){ return false; } },
      focus(){}, addEventListener(){}
    });
    return nodes.get(id);
  }
};

let queriedOpId = null;
global.fetch = async (url, options) => {
  if (url.includes('/api/v1/orders/99/delivered')) {
    // Simulate network drop/timeout
    throw new Error('TypeError: Failed to fetch');
  }
  if (url.includes('/api/v1/operations/')) {
    queriedOpId = url.split('/').pop();
    return {
      ok: true,
      json: async () => ({
        status: 'committed',
        result: {
          orderId: 99,
          fieldGroup: 'delivered',
          revision: 2,
          value: { delivered: true, deliveredAt: '2026-10-26T14:00:00Z', deliveredBy: 'Nhân viên' }
        }
      })
    };
  }
  return { ok: true, json: async () => ({}) };
};
";
        string assertions = @"
(async () => {
  currentRole = 'Staff';
  authToken = 'test-token-123';
  cachedOrders = [{
    id: 99,
    customerName: 'Network Test',
    folderName: '2026-10-26/Net',
    isDelivered: false,
    deliveredRevision: 1,
    items: []
  }];

  await toggleDelivered(99);

  // Must have queried operation status
  assert.ok(queriedOpId !== null, 'Must query /api/v1/operations/{opId} on network error');
  assert.match(queriedOpId, /^[0-9a-f-]{36}$/i);

  // Must have applied committed result
  assert.equal(cachedOrders[0].isDelivered, true);
  assert.equal(cachedOrders[0].deliveredRevision, 2);
})().catch(e => { console.error(e); process.exitCode = 1; });
";
        string path = Path.Combine(Path.GetTempPath(), "lalab_retry_" + Guid.NewGuid().ToString("N") + ".cjs");
        try
        {
            File.WriteAllText(path, fixture + script + assertions);
            using var process = Process.Start(new ProcessStartInfo("node", $"\"{path}\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            Assert.True(process.ExitCode == 0, error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ManualRuntimeSmoke_TempDb_LanWritesOrdersAndNoneModes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // === STEP 1: --lan-writes none mode (default) ===
            int portNone = GetFreeTcpPort();
            var servicesNone = new ServiceCollection();
            servicesNone.AddLogging();
            servicesNone.AddLalabCore(opt =>
            {
                opt.DataDirectory = tempDir;
                opt.ThumbnailsDirectory = Path.Combine(tempDir, "Thumbnails");
                opt.EnableCloudSync = false;
                opt.EnableMobileWebServer = true;
                opt.MobileWebServerLoopbackOnly = true;
                opt.MobileWebServerPort = portNone;
                opt.MobileWebServerEnabledWriteCapabilities = new HashSet<string>(StringComparer.Ordinal); // empty = none
            });

            var provNone = servicesNone.BuildServiceProvider();
            var migratorNone = provNone.GetRequiredService<IDatabaseMigrator>();
            await migratorNone.MigrateAsync();

            var settingsRepoNone = provNone.GetRequiredService<ISettingsRepository>();
            var settingsNone = await settingsRepoNone.GetSettingsAsync();
            settingsNone.AdminPin = "888888";
            settingsNone.StaffPin = "111111";
            settingsNone.WorkshopName = "Smoke Test Workshop";
            await settingsRepoNone.SaveSettingsAsync(settingsNone);

            var orderRepoNone = provNone.GetRequiredService<IOrderRepository>();
            var seedOrder = new Order
            {
                WorkDate = "2026-10-26",
                OriginalFolderName = "SmokeCust",
                RelativePath = "2026-10-26\\SmokeCust",
                Status = OrderStatus.Scanned,
                IsDelivered = false,
                IsPrinted = false,
                Note = "Initial note"
            };
            await orderRepoNone.SaveOrderAsync(seedOrder, new ScanSnapshot { StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow });

            var serverNone = provNone.GetRequiredService<IMobileWebServer>();
            await serverNone.StartAsync();

            using (var clientNone = new HttpClient { BaseAddress = new Uri(serverNone.LocalUrl!) })
            {
                // Login to get valid token
                var loginResp = await clientNone.PostAsJsonAsync("/api/v1/auth/sessions", new { pin = "888888", clientKind = "pwa" });
                loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var loginData = await loginResp.Content.ReadFromJsonAsync<LoginResponseDto>();

                // Attempt write command in 'none' mode: must return 503 WRITES_DISABLED_UNTIL_PHASE2
                var writeReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{seedOrder.Id}/delivered")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operationId = Guid.NewGuid().ToString("N"),
                        expectedRevision = 0,
                        delivered = true
                    }), Encoding.UTF8, "application/json")
                };
                writeReq.Headers.Add("X-Lalab-Client-Api", "1.0");
                writeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginData!.Token);

                var writeResp = await clientNone.SendAsync(writeReq);
                writeResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                var errNone = await writeResp.Content.ReadFromJsonAsync<ApiErrorResponse>();
                errNone!.Code.Should().Be(ApiErrorCodes.WritesDisabledUntilPhase2);
            }

            await serverNone.StopAsync();
            SqliteConnection.ClearAllPools();

            // === STEP 2: --lan-writes orders mode ===
            int portOrders = GetFreeTcpPort();
            var servicesOrders = new ServiceCollection();
            servicesOrders.AddLogging();
            servicesOrders.AddLalabCore(opt =>
            {
                opt.DataDirectory = tempDir;
                opt.ThumbnailsDirectory = Path.Combine(tempDir, "Thumbnails");
                opt.EnableCloudSync = false;
                opt.EnableMobileWebServer = true;
                opt.MobileWebServerLoopbackOnly = true;
                opt.MobileWebServerPort = portOrders;
                opt.MobileWebServerEnabledWriteCapabilities = new HashSet<string>(StringComparer.Ordinal)
                {
                    Capabilities.OrdersSetDelivered,
                    Capabilities.OrdersSetPrinted,
                    Capabilities.OrdersEditNote
                };
            });

            var provOrders = servicesOrders.BuildServiceProvider();
            var serverOrders = provOrders.GetRequiredService<IMobileWebServer>();
            await serverOrders.StartAsync();

            using (var clientOrders = new HttpClient { BaseAddress = new Uri(serverOrders.LocalUrl!) })
            {
                // 1. Verify SPA root GET / serves updated PWA HTML
                var spaResp = await clientOrders.GetAsync("/");
                spaResp.StatusCode.Should().Be(HttpStatusCode.OK);
                string spaHtml = await spaResp.Content.ReadAsStringAsync();
                spaHtml.Should().Contain("X-Lalab-Client-Api");
                spaHtml.Should().Contain("noteConflictNotice");
                spaHtml.Should().Contain("/api/v1/orders/");

                // 2. Login
                var loginResp = await clientOrders.PostAsJsonAsync("/api/v1/auth/sessions", new { pin = "888888", clientKind = "pwa" });
                loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var loginData = await loginResp.Content.ReadFromJsonAsync<LoginResponseDto>();
                string token = loginData!.Token!;

                // 3. Set note
                string opNote = Guid.NewGuid().ToString("N");
                var noteReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{seedOrder.Id}/note")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operationId = opNote,
                        expectedRevision = 0,
                        note = "Smoke note committed by agent"
                    }), Encoding.UTF8, "application/json")
                };
                noteReq.Headers.Add("X-Lalab-Client-Api", "1.0");
                noteReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var noteResp = await clientOrders.SendAsync(noteReq);
                noteResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var noteDto = await noteResp.Content.ReadFromJsonAsync<OrderFieldStateDto>();
                noteDto!.Revision.Should().Be(1);
                noteDto.FieldGroup.Should().Be("note");

                // 4. Set delivered
                string opDel = Guid.NewGuid().ToString("N");
                var delReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{seedOrder.Id}/delivered")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operationId = opDel,
                        expectedRevision = 0,
                        delivered = true
                    }), Encoding.UTF8, "application/json")
                };
                delReq.Headers.Add("X-Lalab-Client-Api", "1.0");
                delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var delResp = await clientOrders.SendAsync(delReq);
                delResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var delDto = await delResp.Content.ReadFromJsonAsync<OrderFieldStateDto>();
                delDto!.Revision.Should().Be(1);
                delDto.FieldGroup.Should().Be("delivered");

                // 5. Stale conflict (two tabs): Tab 2 attempts update with stale revision 0
                string opStale = Guid.NewGuid().ToString("N");
                var staleReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{seedOrder.Id}/note")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operationId = opStale,
                        expectedRevision = 0, // Stale! Server is now at 1
                        note = "Tab 2 stale attempt"
                    }), Encoding.UTF8, "application/json")
                };
                staleReq.Headers.Add("X-Lalab-Client-Api", "1.0");
                staleReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var staleResp = await clientOrders.SendAsync(staleReq);
                staleResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
                var staleJson = await staleResp.Content.ReadAsStringAsync();
                staleJson.Should().Contain("STALE_REVISION");

                // 6. Operation replay by same operationId
                var replayReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{seedOrder.Id}/note")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operationId = opNote, // Same opId
                        expectedRevision = 0,
                        note = "Smoke note committed by agent"
                    }), Encoding.UTF8, "application/json")
                };
                replayReq.Headers.Add("X-Lalab-Client-Api", "1.0");
                replayReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var replayResp = await clientOrders.SendAsync(replayReq);
                replayResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var replayDto = await replayResp.Content.ReadFromJsonAsync<OrderFieldStateDto>();
                replayDto!.OperationId.Should().Be(opNote);
                replayDto.Revision.Should().Be(1);
                replayDto.FieldGroup.Should().Be("note");
            }

            await serverOrders.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int CountChar(string text, char target)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == target) count++;
        }
        return count;
    }
}
