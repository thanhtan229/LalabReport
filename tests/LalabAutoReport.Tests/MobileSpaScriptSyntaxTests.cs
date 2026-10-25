using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using LalabAutoReport.Infrastructure.Web;
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
