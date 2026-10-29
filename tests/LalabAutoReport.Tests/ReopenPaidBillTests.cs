using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class ReopenPaidBillTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;

    public ReopenPaidBillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LalabReopenPaid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "reopen.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch { }
    }

    private async Task<long> SeedLockedBillAsync(bool isPaid)
    {
        await new DatabaseMigrator(_factory).MigrateAsync();
        string now = DateTimeOffset.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("INSERT OR REPLACE INTO app_settings (key, value) VALUES ('EnableCloudSync', 'true');");
        long custId = await conn.QuerySingleAsync<long>(
            "INSERT INTO customers (canonical_name, price_tier, created_at, updated_at) VALUES ('Reopen', 'Retail', @Now, @Now); SELECT last_insert_rowid();",
            new { Now = now });
        return await conn.QuerySingleAsync<long>(@"
            INSERT INTO customer_bills (bill_number, customer_id, customer_name_snapshot, period_start, period_end, status,
                product_subtotal, adjustments_total, grand_total, is_paid, paid_at, created_at, updated_at)
            VALUES ('BILL-REOPEN-1', @CustId, 'Reopen', '2026-10-26', '2026-10-26', 'Locked',
                100000, 0, 100000, @IsPaid, @PaidAt, @Now, @Now);
            SELECT last_insert_rowid();",
            new { CustId = custId, IsPaid = isPaid ? 1 : 0, PaidAt = isPaid ? now : null, Now = now });
    }

    [Fact]
    public async Task Reopen_PaidBill_ClearsPaymentAndQueuesCloudUpdate()
    {
        long billId = await SeedLockedBillAsync(isPaid: true);
        var repo = new SqliteCustomerBillRepository(_factory);
        var bill = await repo.GetByIdAsync(billId);
        bill!.Status = CustomerBillStatus.Draft;

        await repo.ReopenCustomerBillAtomicAsync(bill, Array.Empty<long>(), Array.Empty<long>());

        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleAsync("SELECT status, is_paid, paid_at FROM customer_bills WHERE id = @Id", new { Id = billId });
        string status = row.status;
        long isPaid = row.is_paid;
        ((object?)row.paid_at).Should().BeNull();
        status.Should().Be("Draft");
        isPaid.Should().Be(0);

        var outbox = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM cloud_operational_outbox WHERE entity_type = 'bill_payment' AND entity_id = @Id AND value_json = 'false'",
            new { Id = billId });
        outbox.Should().Be(1, "cloud must learn that the payment was cancelled");
    }

    [Fact]
    public async Task Reopen_UnpaidBill_LeavesPaymentStateUntouched()
    {
        long billId = await SeedLockedBillAsync(isPaid: false);
        var repo = new SqliteCustomerBillRepository(_factory);
        var bill = await repo.GetByIdAsync(billId);
        bill!.Status = CustomerBillStatus.Draft;

        await repo.ReopenCustomerBillAtomicAsync(bill, Array.Empty<long>(), Array.Empty<long>());

        using var conn = _factory.CreateConnection();
        long outbox = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM cloud_operational_outbox WHERE entity_type = 'bill_payment' AND entity_id = @Id",
            new { Id = billId });
        outbox.Should().Be(0);
    }
}
