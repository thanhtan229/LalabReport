using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;

namespace LalabAutoReport.Infrastructure.Data;

public class SqliteCustomerBillRepository : ICustomerBillRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteCustomerBillRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<CustomerBill?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDto = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(
            "SELECT * FROM customer_bills WHERE id = @Id", new { Id = id });

        if (billDto == null) return null;
        return await PopulateBillDetailsAsync(connection, billDto);
    }

    public async Task<CustomerBill?> GetByBillNumberAsync(string billNumber, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDto = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(
            "SELECT * FROM customer_bills WHERE bill_number = @BillNumber", new { BillNumber = billNumber });

        if (billDto == null) return null;
        return await PopulateBillDetailsAsync(connection, billDto);
    }

    public async Task<IReadOnlyList<CustomerBill>> GetBillsByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDtos = (await connection.QueryAsync<CustomerBillDto>(
            "SELECT * FROM customer_bills WHERE customer_id = @CustomerId ORDER BY id DESC",
            new { CustomerId = customerId })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    public async Task<CustomerBill?> GetLastLockedOrExportedBillByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDto = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(@"
            SELECT * FROM customer_bills
            WHERE customer_id = @CustomerId AND status IN ('Locked', 'Exported')
            ORDER BY period_end DESC, id DESC
            LIMIT 1
        ", new { CustomerId = customerId });

        if (billDto == null) return null;
        return await PopulateBillDetailsAsync(connection, billDto);
    }

    public async Task<CustomerBill?> GetLockedBillByOrderIdAsync(long orderId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDto = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(@"
            SELECT DISTINCT cb.* FROM customer_bills cb
            INNER JOIN customer_bill_orders cbo ON cb.id = cbo.bill_id
            WHERE cbo.order_id = @OrderId AND cb.status IN ('Locked', 'Exported')
            ORDER BY cb.id DESC
            LIMIT 1
        ", new { OrderId = orderId });

        if (billDto == null) return null;
        return await PopulateBillDetailsAsync(connection, billDto);
    }

    public async Task<CustomerBill?> GetBillByOrderIdAsync(long orderId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDto = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(@"
            SELECT DISTINCT cb.* FROM customer_bills cb
            INNER JOIN customer_bill_orders cbo ON cb.id = cbo.bill_id
            WHERE cbo.order_id = @OrderId
            ORDER BY cb.id DESC
            LIMIT 1
        ", new { OrderId = orderId });

        if (billDto == null) return null;
        return await PopulateBillDetailsAsync(connection, billDto);
    }

    public async Task<CustomerBill?> GetActiveDraftByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDto = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(@"
            SELECT * FROM customer_bills
            WHERE customer_id = @CustomerId AND status = 'Draft'
            ORDER BY id DESC
            LIMIT 1
        ", new { CustomerId = customerId });

        if (billDto == null) return null;
        return await PopulateBillDetailsAsync(connection, billDto);
    }

    public async Task<IReadOnlyList<CustomerBill>> GetAllBillsAsync(BillType? typeFilter = null, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = typeFilter.HasValue
            ? "SELECT * FROM customer_bills WHERE bill_type = @Type ORDER BY id DESC"
            : "SELECT * FROM customer_bills ORDER BY id DESC";

        var billDtos = (await connection.QueryAsync<CustomerBillDto>(
            sql, new { Type = typeFilter?.ToString() })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    public async Task<IReadOnlyList<CustomerBill>> GetBillsByDateAsync(string date, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDtos = (await connection.QueryAsync<CustomerBillDto>(@"
            SELECT DISTINCT cb.* FROM customer_bills cb
            LEFT JOIN customer_bill_orders cbo ON cb.id = cbo.bill_id
            WHERE cb.status IN ('Locked', 'Exported')
              AND (cbo.order_date_snapshot = @Date OR cb.period_end = @Date OR substr(cb.locked_at, 1, 10) = @Date)
            ORDER BY cb.id DESC
        ", new { Date = date })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    public async Task<IReadOnlyList<CustomerBill>> GetBillsByMonthAsync(string yearMonth, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        string pattern = yearMonth + "%";
        var billDtos = (await connection.QueryAsync<CustomerBillDto>(@"
            SELECT DISTINCT cb.* FROM customer_bills cb
            LEFT JOIN customer_bill_orders cbo ON cb.id = cbo.bill_id
            WHERE cb.status IN ('Locked', 'Exported')
              AND (cbo.order_date_snapshot LIKE @Pattern OR cb.period_end LIKE @Pattern OR cb.locked_at LIKE @Pattern)
            ORDER BY cb.id DESC
        ", new { Pattern = pattern })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    public async Task<IReadOnlyList<CustomerBill>> GetBillsByDateRangeAsync(string startDate, string endDate, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var billDtos = (await connection.QueryAsync<CustomerBillDto>(@"
            SELECT DISTINCT cb.* FROM customer_bills cb
            LEFT JOIN customer_bill_orders cbo ON cb.id = cbo.bill_id
            WHERE cb.status IN ('Locked', 'Exported')
              AND ((cbo.order_date_snapshot >= @StartDate AND cbo.order_date_snapshot <= @EndDate)
                   OR (cb.period_end >= @StartDate AND cb.period_end <= @EndDate)
                   OR (substr(cb.locked_at, 1, 10) >= @StartDate AND substr(cb.locked_at, 1, 10) <= @EndDate))
            ORDER BY cb.id DESC
        ", new { StartDate = startDate, EndDate = endDate })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    public async Task<IReadOnlyList<CustomerBill>> GetBillsBySourceFolderPathAsync(string normalizedFolderPath, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        string norm = PathNormalizer.Normalize(normalizedFolderPath);
        var billDtos = (await connection.QueryAsync<CustomerBillDto>(@"
            SELECT DISTINCT b.* FROM customer_bills b
            LEFT JOIN guest_bill_source_folders s ON b.id = s.bill_id
            LEFT JOIN customer_bill_orders o ON b.id = o.bill_id
            WHERE s.normalized_folder_path = @NormalizedPath
               OR LOWER(o.source_folder_path) = @NormalizedPath
            ORDER BY b.id DESC
        ", new { NormalizedPath = norm })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    public async Task<IReadOnlyList<GuestBillSourceFolder>> GetSourceFoldersByBillIdAsync(long billId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var dtos = await connection.QueryAsync<GuestBillSourceFolderDto>(
            "SELECT * FROM guest_bill_source_folders WHERE bill_id = @BillId ORDER BY id",
            new { BillId = billId });

        return dtos.Select(d => new GuestBillSourceFolder
        {
            Id = d.id,
            BillId = d.bill_id,
            FolderPath = d.folder_path,
            NormalizedFolderPath = d.normalized_folder_path,
            CreatedAt = DateTimeOffset.Parse(d.created_at)
        }).ToList();
    }

    public async Task SaveBillAsync(CustomerBill bill, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        await SaveBillInternalAsync(connection, transaction, bill);
        transaction.Commit();
    }

    public async Task SplitBillAtomicAsync(CustomerBill original, CustomerBill created,
        IReadOnlyList<long> movedOrderIds, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(
                "SELECT * FROM customer_bills WHERE id=@Id", new { original.Id }, transaction);
            if (stored == null)
                throw new KeyNotFoundException($"Không tìm thấy hóa đơn ID {original.Id}.");
            if (stored.status != "Draft" || original.Status != CustomerBillStatus.Draft || created.Status != CustomerBillStatus.Draft || stored.is_paid == 1 || original.IsPaid)
                throw new InvalidOperationException("Hãy mở lại hoặc hủy thanh toán hóa đơn trước khi tách đơn.");
            if (stored.revision != original.Revision || created.Id != 0)
                throw new InvalidOperationException("Hóa đơn đã thay đổi. Hãy tải lại trước khi tách đơn.");
            var currentIds = (await connection.QueryAsync<long>(
                "SELECT order_id FROM customer_bill_orders WHERE bill_id=@Id", new { original.Id }, transaction)).ToHashSet();
            var movingIds = movedOrderIds.ToHashSet();
            if (movingIds.Count == 0 || movingIds.Count >= currentIds.Count || !movingIds.IsSubsetOf(currentIds) ||
                !created.Orders.Select(o => o.OrderId).ToHashSet().SetEquals(movingIds) ||
                !original.Orders.Select(o => o.OrderId).ToHashSet().SetEquals(currentIds.Except(movingIds)))
                throw new InvalidOperationException("Danh sách đơn hàng đã thay đổi. Hãy tải lại hóa đơn.");
            await SaveBillInternalAsync(connection, transaction, created);
            await SaveBillInternalAsync(connection, transaction, original);
            await connection.ExecuteAsync(
                "UPDATE order_item_scans SET customer_bill_id=@Id WHERE order_id IN @MovedOrderIds",
                new { Id = created.Id, MovedOrderIds = movedOrderIds }, transaction);
            var jobs = created.Lines.Select(l => l.ProductJobId).Where(id => id > 0).Distinct().ToArray();
            if (jobs.Length > 0)
                await connection.ExecuteAsync("UPDATE order_item_scans SET customer_bill_id=@Id WHERE id IN @Jobs",
                    new { Id = created.Id, Jobs = jobs }, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch { transaction.Rollback(); created.Id = 0; throw; }
    }

    public async Task LockCustomerBillAtomicAsync(
        CustomerBill bill,
        IReadOnlyList<long> productJobIds,
        IReadOnlyList<long> orderIds,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            await SaveBillInternalAsync(connection, transaction, bill);

            // (a) order-level claim guard: no included order already in ANOTHER Locked/Exported bill
            if (orderIds.Count > 0)
            {
                int conflictingOrderCount = await connection.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM customer_bill_orders cbo
                    JOIN customer_bills cb ON cb.id = cbo.bill_id
                    WHERE cbo.order_id IN @OrderIds AND cbo.is_included = 1
                      AND cbo.bill_id <> @BillId AND cb.status IN ('Locked','Exported')
                ", new { OrderIds = orderIds, BillId = bill.Id }, transaction: transaction);

                if (conflictingOrderCount > 0)
                {
                    throw new BillClaimConflictException("Một hoặc nhiều đơn hàng đã được chốt trong hóa đơn khác.");
                }
            }

            // (b) job-level claim guard: take over from NULL, self, or a Draft owner; never from Locked/Exported
            if (productJobIds.Count > 0)
            {
                var distinctJobIds = productJobIds.Distinct().ToArray();
                int affected = await connection.ExecuteAsync(@"
                    UPDATE order_item_scans
                    SET customer_bill_id = @BillId
                    WHERE id IN @Ids
                      AND (customer_bill_id IS NULL OR customer_bill_id = @BillId
                           OR customer_bill_id IN (SELECT id FROM customer_bills WHERE status = 'Draft'))
                ", new { BillId = bill.Id, Ids = distinctJobIds }, transaction: transaction);

                if (affected != distinctJobIds.Length)
                {
                    throw new BillClaimConflictException("Một hoặc nhiều hạng mục in đã thuộc hóa đơn đã khóa/xuất khác.");
                }
            }

            if (orderIds.Count > 0)
            {
                await connection.ExecuteAsync(@"
                    UPDATE orders
                    SET status = 'Billed', updated_at = @UpdatedAt
                    WHERE id IN @Ids
                ", new { UpdatedAt = DateTimeOffset.UtcNow.ToString("o"), Ids = orderIds }, transaction: transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task ReopenCustomerBillAtomicAsync(
        CustomerBill bill,
        IReadOnlyList<long> productJobIds,
        IReadOnlyList<long> orderIds,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            await SaveBillInternalAsync(connection, transaction, bill, allowReopen: true);

            if (productJobIds.Count > 0)
            {
                await connection.ExecuteAsync(@"
                    UPDATE order_item_scans
                    SET customer_bill_id = NULL
                    WHERE id IN @Ids
                ", new { Ids = productJobIds }, transaction: transaction);
            }

            if (orderIds.Count > 0)
            {
                await connection.ExecuteAsync(@"
                    UPDATE orders
                    SET status = 'Ready', updated_at = @UpdatedAt
                    WHERE id IN @Ids
                ", new { UpdatedAt = DateTimeOffset.UtcNow.ToString("o"), Ids = orderIds }, transaction: transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task SaveBillInternalAsync(IDbConnection connection, IDbTransaction transaction, CustomerBill bill, bool allowReopen = false)
    {
        if (bill.Id != 0)
        {
            var stored = await connection.QuerySingleOrDefaultAsync<CustomerBillDto>(
                "SELECT * FROM customer_bills WHERE id = @Id", new { bill.Id }, transaction);
            if (stored == null) throw new InvalidOperationException("Hóa đơn không còn tồn tại.");
            // Payment has its own mutation path; a stale review window must not undo it.
            bill.IsPaid = stored.is_paid == 1;
            bill.PaidAt = string.IsNullOrEmpty(stored.paid_at) ? null : DateTimeOffset.Parse(stored.paid_at);
            if (stored.status != "Draft")
            {
                var snapshot = await PopulateBillDetailsAsync(connection, stored, transaction);
                if (SnapshotContent(snapshot) != SnapshotContent(bill))
                    throw new InvalidOperationException("Hóa đơn đã chốt. Hãy mở lại trước khi sửa nội dung.");
                if (allowReopen)
                {
                    if (bill.Status != CustomerBillStatus.Draft)
                        throw new InvalidOperationException("Mở lại phải chuyển hóa đơn về Draft.");
                }
                else
                {
                    if (bill.Status == CustomerBillStatus.Draft ||
                        (stored.status == "Exported" && bill.Status != CustomerBillStatus.Exported))
                        throw new InvalidOperationException("Phải dùng thao tác mở lại hóa đơn.");
                    await connection.ExecuteAsync(@"UPDATE customer_bills SET status=@Status,
                        export_file_path=@ExportFilePath, exported_at=@ExportedAt, updated_at=@UpdatedAt WHERE id=@Id",
                        new { bill.Id, Status = bill.Status.ToString(), bill.ExportFilePath,
                            ExportedAt = bill.ExportedAt?.ToString("o"), UpdatedAt = DateTimeOffset.UtcNow.ToString("o") }, transaction);
                    return; // Preserve child IDs and the historical snapshot.
                }
            }
            else if (allowReopen) throw new InvalidOperationException("Hóa đơn đã là Draft.");
        }

        if (bill.Id == 0)
        {
            long billId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO customer_bills (
                    bill_number, bill_type, customer_id, customer_name_snapshot, phone_snapshot,
                    shipping_address_snapshot, period_start, period_end, status, product_subtotal,
                    adjustments_total, grand_total, note, export_file_path, locked_at, exported_at,
                    is_paid, paid_at, created_at, updated_at
                ) VALUES (
                    @BillNumber, @BillType, @CustomerId, @CustomerNameSnapshot, @PhoneSnapshot,
                    @ShippingAddressSnapshot, @PeriodStart, @PeriodEnd, @Status, @ProductSubtotal,
                    @AdjustmentsTotal, @GrandTotal, @Note, @ExportFilePath, @LockedAt, @ExportedAt,
                    @IsPaid, @PaidAt, @CreatedAt, @UpdatedAt
                );
                SELECT last_insert_rowid();
            ", new
            {
                BillNumber = bill.BillNumber,
                BillType = bill.BillType.ToString(),
                CustomerId = bill.CustomerId,
                CustomerNameSnapshot = bill.CustomerNameSnapshot,
                PhoneSnapshot = bill.PhoneSnapshot,
                ShippingAddressSnapshot = bill.ShippingAddressSnapshot,
                PeriodStart = bill.PeriodStart,
                PeriodEnd = bill.PeriodEnd,
                Status = bill.Status.ToString(),
                ProductSubtotal = bill.ProductSubtotal,
                AdjustmentsTotal = bill.AdjustmentsTotal,
                GrandTotal = bill.GrandTotal,
                Note = bill.Note,
                ExportFilePath = bill.ExportFilePath,
                LockedAt = bill.LockedAt?.ToString("o"),
                ExportedAt = bill.ExportedAt?.ToString("o"),
                IsPaid = bill.IsPaid ? 1 : 0,
                PaidAt = bill.PaidAt?.ToString("o"),
                CreatedAt = bill.CreatedAt.ToString("o"),
                UpdatedAt = bill.UpdatedAt.ToString("o")
            }, transaction: transaction);

            bill.Id = billId;
        }
        else
        {
            await connection.ExecuteAsync(@"
                UPDATE customer_bills
                SET bill_number = @BillNumber,
                    bill_type = @BillType,
                    customer_id = @CustomerId,
                    customer_name_snapshot = @CustomerNameSnapshot,
                    phone_snapshot = @PhoneSnapshot,
                    shipping_address_snapshot = @ShippingAddressSnapshot,
                    period_start = @PeriodStart,
                    period_end = @PeriodEnd,
                    status = @Status,
                    product_subtotal = @ProductSubtotal,
                    adjustments_total = @AdjustmentsTotal,
                    grand_total = @GrandTotal,
                    note = @Note,
                    export_file_path = @ExportFilePath,
                    locked_at = @LockedAt,
                    exported_at = @ExportedAt,
                    is_paid = @IsPaid,
                    paid_at = @PaidAt,
                    updated_at = @UpdatedAt
                WHERE id = @Id
            ", new
            {
                Id = bill.Id,
                BillNumber = bill.BillNumber,
                BillType = bill.BillType.ToString(),
                CustomerId = bill.CustomerId,
                CustomerNameSnapshot = bill.CustomerNameSnapshot,
                PhoneSnapshot = bill.PhoneSnapshot,
                ShippingAddressSnapshot = bill.ShippingAddressSnapshot,
                PeriodStart = bill.PeriodStart,
                PeriodEnd = bill.PeriodEnd,
                Status = bill.Status.ToString(),
                ProductSubtotal = bill.ProductSubtotal,
                AdjustmentsTotal = bill.AdjustmentsTotal,
                GrandTotal = bill.GrandTotal,
                Note = bill.Note,
                ExportFilePath = bill.ExportFilePath,
                LockedAt = bill.LockedAt?.ToString("o"),
                ExportedAt = bill.ExportedAt?.ToString("o"),
                IsPaid = bill.IsPaid ? 1 : 0,
                PaidAt = bill.PaidAt?.ToString("o"),
                UpdatedAt = bill.UpdatedAt.ToString("o")
            }, transaction: transaction);

            // Clear child tables for update
            await connection.ExecuteAsync("DELETE FROM customer_bill_orders WHERE bill_id = @BillId", new { BillId = bill.Id }, transaction: transaction);
            await connection.ExecuteAsync("DELETE FROM customer_bill_lines WHERE bill_id = @BillId", new { BillId = bill.Id }, transaction: transaction);
            await connection.ExecuteAsync("DELETE FROM customer_bill_adjustments WHERE bill_id = @BillId", new { BillId = bill.Id }, transaction: transaction);
            await connection.ExecuteAsync("DELETE FROM guest_bill_source_folders WHERE bill_id = @BillId", new { BillId = bill.Id }, transaction: transaction);
        }

        // Insert Orders
        foreach (var order in bill.Orders)
        {
            order.BillId = bill.Id;
            long orderRowId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO customer_bill_orders (
                    bill_id, order_id, order_code_snapshot, order_name_snapshot, order_date_snapshot,
                    original_folder_name_snapshot, subtotal, sort_order, is_included,
                    is_from_previous_period, source_folder_path
                ) VALUES (
                    @BillId, @OrderId, @OrderCodeSnapshot, @OrderNameSnapshot, @OrderDateSnapshot,
                    @OriginalFolderNameSnapshot, @Subtotal, @SortOrder, @IsIncluded,
                    @IsFromPreviousPeriod, @SourceFolderPath
                );
                SELECT last_insert_rowid();
            ", new
            {
                BillId = order.BillId,
                OrderId = order.OrderId,
                OrderCodeSnapshot = order.OrderCodeSnapshot,
                OrderNameSnapshot = order.OrderNameSnapshot,
                OrderDateSnapshot = order.OrderDateSnapshot,
                OriginalFolderNameSnapshot = order.OriginalFolderNameSnapshot,
                Subtotal = order.Subtotal,
                SortOrder = order.SortOrder,
                IsIncluded = order.IsIncluded ? 1 : 0,
                IsFromPreviousPeriod = order.IsFromPreviousPeriod ? 1 : 0,
                SourceFolderPath = order.SourceFolderPath
            }, transaction: transaction);

            order.Id = orderRowId;
        }

        // Insert Lines
        var linesToInsert = bill.Lines.Count > 0 ? bill.Lines : bill.Orders.SelectMany(o => o.Lines).ToList();
        foreach (var line in linesToInsert)
        {
            line.BillId = bill.Id;
            long lineRowId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO customer_bill_lines (
                    bill_id, order_id, product_job_id, product_specification_id,
                    specification_folder_name, product_name_snapshot, variant_snapshot, billing_method_snapshot,
                    scanned_quantity, billed_quantity, quantity_override_reason,
                    sheet_count, included_sheets_snapshot, extra_sheet_count,
                    base_price_snapshot, extra_sheet_price_snapshot,
                    configured_unit_price, billed_unit_price, price_override_reason,
                    line_total, is_included, sort_order, final_print_folder_path,
                    folder_resolution_mode_snapshot, issue_message, note
                ) VALUES (
                    @BillId, @OrderId, @ProductJobId, @ProductSpecificationId,
                    @SpecificationFolderName, @ProductNameSnapshot, @VariantSnapshot, @BillingMethodSnapshot,
                    @ScannedQuantity, @BilledQuantity, @QuantityOverrideReason,
                    @SheetCount, @IncludedSheetsSnapshot, @ExtraSheetCount,
                    @BasePriceSnapshot, @ExtraSheetPriceSnapshot,
                    @ConfiguredUnitPrice, @BilledUnitPrice, @PriceOverrideReason,
                    @LineTotal, @IsIncluded, @SortOrder, @FinalPrintFolderPath,
                    @FolderResolutionModeSnapshot, @IssueMessage, @Note
                );
                SELECT last_insert_rowid();
            ", new
            {
                BillId = line.BillId,
                OrderId = line.OrderId,
                ProductJobId = line.ProductJobId,
                ProductSpecificationId = line.ProductSpecificationId,
                SpecificationFolderName = line.SpecificationFolderName,
                ProductNameSnapshot = line.ProductNameSnapshot,
                VariantSnapshot = line.VariantSnapshot,
                BillingMethodSnapshot = line.BillingMethodSnapshot.ToString(),
                ScannedQuantity = line.ScannedQuantity,
                BilledQuantity = line.BilledQuantity,
                QuantityOverrideReason = line.QuantityOverrideReason,
                SheetCount = line.SheetCount,
                IncludedSheetsSnapshot = line.IncludedSheetsSnapshot,
                ExtraSheetCount = line.ExtraSheetCount,
                BasePriceSnapshot = line.BasePriceSnapshot,
                ExtraSheetPriceSnapshot = line.ExtraSheetPriceSnapshot,
                ConfiguredUnitPrice = line.ConfiguredUnitPrice,
                BilledUnitPrice = line.BilledUnitPrice,
                PriceOverrideReason = line.PriceOverrideReason,
                LineTotal = line.LineTotal,
                IsIncluded = line.IsIncluded ? 1 : 0,
                SortOrder = line.SortOrder,
                FinalPrintFolderPath = line.FinalPrintFolderPath,
                FolderResolutionModeSnapshot = line.FolderResolutionModeSnapshot.ToString(),
                IssueMessage = line.IssueMessage,
                Note = line.Note
            }, transaction: transaction);

            line.Id = lineRowId;
        }

        // Insert Adjustments
        foreach (var adj in bill.Adjustments)
        {
            adj.BillId = bill.Id;
            long adjRowId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO customer_bill_adjustments (
                    bill_id, type, label, direction, amount, note, sort_order
                ) VALUES (
                    @BillId, @Type, @Label, @Direction, @Amount, @Note, @SortOrder
                );
                SELECT last_insert_rowid();
            ", new
            {
                BillId = adj.BillId,
                Type = adj.Type.ToString(),
                Label = adj.Label,
                Direction = adj.Direction.ToString(),
                Amount = adj.Amount,
                Note = adj.Note,
                SortOrder = adj.SortOrder
            }, transaction: transaction);

            adj.Id = adjRowId;
        }

        // Insert Source Folders
        foreach (var sf in bill.SourceFolders)
        {
            sf.BillId = bill.Id;
            long sfId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO guest_bill_source_folders (
                    bill_id, folder_path, normalized_folder_path, created_at
                ) VALUES (
                    @BillId, @FolderPath, @NormalizedFolderPath, @CreatedAt
                );
                SELECT last_insert_rowid();
            ", new
            {
                BillId = sf.BillId,
                FolderPath = sf.FolderPath,
                NormalizedFolderPath = sf.NormalizedFolderPath,
                CreatedAt = sf.CreatedAt.ToString("o")
            }, transaction: transaction);

            sf.Id = sfId;
        }
    }

    public async Task DeleteDraftBillAsync(long billId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var status = await connection.QuerySingleOrDefaultAsync<string>("SELECT status FROM customer_bills WHERE id=@Id", new { Id = billId }, transaction);
        if (status != "Draft") return;
        await connection.ExecuteAsync("DELETE FROM guest_bill_source_folders WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bill_adjustments WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bill_lines WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bill_orders WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bills WHERE id = @Id AND status = 'Draft'", new { Id = billId }, transaction: transaction);
        transaction.Commit();
    }

    public async Task DeleteBillAsync(long billId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var status = await connection.QuerySingleOrDefaultAsync<string>("SELECT status FROM customer_bills WHERE id=@Id", new { Id = billId }, transaction);
        if (status != null && status != "Draft")
            throw new InvalidOperationException("Hãy mở lại hóa đơn trước khi xóa.");
        await connection.ExecuteAsync("DELETE FROM guest_bill_source_folders WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bill_adjustments WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bill_lines WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bill_orders WHERE bill_id = @Id", new { Id = billId }, transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM customer_bills WHERE id = @Id", new { Id = billId }, transaction: transaction);
        transaction.Commit();
    }

    public async Task<string> GenerateNextBillNumberAsync(string date, CancellationToken cancellationToken = default)
    {
        // Date e.g. "2026-09-29" -> compact "20260929"
        string compactDate = date.Replace("-", string.Empty);
        if (compactDate.Length != 8)
        {
            compactDate = DateTime.UtcNow.ToString("yyyyMMdd");
        }

        string prefix = $"BILL-{compactDate}-";
        using var connection = _connectionFactory.CreateConnection();

        var existingNumbers = (await connection.QueryAsync<string>(
            "SELECT bill_number FROM customer_bills WHERE bill_number LIKE @Prefix",
            new { Prefix = prefix + "%" })).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int seq = existingNumbers.Count + 1;
        string candidate = $"{prefix}{seq:D4}";

        while (existingNumbers.Contains(candidate))
        {
            seq++;
            candidate = $"{prefix}{seq:D4}";
        }

        return candidate;
    }

    public async Task SetPaymentStatusAsync(long billId, bool isPaid, DateTimeOffset? paidAt = null, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        string? paidAtStr = isPaid ? (paidAt ?? DateTimeOffset.UtcNow).ToString("o") : null;
        int affected = await connection.ExecuteAsync(@"
            UPDATE customer_bills
            SET is_paid = @IsPaid,
                paid_at = @PaidAt,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = billId,
            IsPaid = isPaid ? 1 : 0,
            PaidAt = paidAtStr,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        }, transaction);
        if (affected != 1) throw new InvalidOperationException("Bill not found.");
        await SqliteCloudSyncStateRepository.EnqueueAsync(connection, transaction, "bill_payment", billId, isPaid);
        transaction.Commit();
    }

    public async Task<long> GetCustomerTotalDebtAsync(long customerId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        var total = await connection.ExecuteScalarAsync<long?>(@"
            SELECT SUM(grand_total)
            FROM customer_bills
            WHERE customer_id = @CustomerId
              AND status IN ('Locked', 'Exported')
              AND is_paid = 0
        ", new { CustomerId = customerId });

        return total ?? 0L;
    }

    public async Task<IReadOnlyList<CustomerBill>> GetUnpaidBillsAsync(long? customerId = null, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = customerId.HasValue
            ? @"SELECT * FROM customer_bills
                WHERE status IN ('Locked', 'Exported')
                  AND is_paid = 0
                  AND customer_id = @CustomerId
                ORDER BY id DESC"
            : @"SELECT * FROM customer_bills
                WHERE status IN ('Locked', 'Exported')
                  AND is_paid = 0
                ORDER BY id DESC";

        var billDtos = (await connection.QueryAsync<CustomerBillDto>(
            sql, new { CustomerId = customerId })).ToList();

        var bills = new List<CustomerBill>();
        foreach (var dto in billDtos)
        {
            bills.Add(await PopulateBillDetailsAsync(connection, dto));
        }

        return bills;
    }

    private static async Task<CustomerBill> PopulateBillDetailsAsync(IDbConnection connection, CustomerBillDto dto, IDbTransaction? transaction = null)
    {
        var bill = MapBill(dto);
        bill.HasPendingCloudChanges = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cloud_operational_outbox WHERE entity_type='bill_payment' AND entity_id=@Id", new { bill.Id }, transaction) > 0;

        var orderDtos = await connection.QueryAsync<CustomerBillOrderDto>(
            "SELECT * FROM customer_bill_orders WHERE bill_id = @BillId ORDER BY sort_order, id",
            new { BillId = bill.Id }, transaction);

        var lineDtos = await connection.QueryAsync<CustomerBillLineDto>(
            "SELECT * FROM customer_bill_lines WHERE bill_id = @BillId ORDER BY sort_order, id",
            new { BillId = bill.Id }, transaction);

        var adjDtos = await connection.QueryAsync<CustomerBillAdjustmentDto>(
            "SELECT * FROM customer_bill_adjustments WHERE bill_id = @BillId ORDER BY sort_order, id",
            new { BillId = bill.Id }, transaction);

        var sfDtos = await connection.QueryAsync<GuestBillSourceFolderDto>(
            "SELECT * FROM guest_bill_source_folders WHERE bill_id = @BillId ORDER BY id",
            new { BillId = bill.Id }, transaction);

        var lines = lineDtos.Select(MapLine).ToList();
        var orders = orderDtos.Select(MapOrder).ToList();
        var adjustments = adjDtos.Select(MapAdjustment).ToList();
        var sourceFolders = sfDtos.Select(MapSourceFolder).ToList();

        // Connect lines into orders
        var linesByOrderId = lines.ToLookup(l => l.OrderId);
        foreach (var order in orders)
        {
            order.Lines = linesByOrderId[order.OrderId].ToList();
        }

        bill.Orders = orders;
        bill.Lines = lines;
        bill.Adjustments = adjustments;
        bill.SourceFolders = sourceFolders;

        return bill;
    }

    private static string SnapshotContent(CustomerBill bill)
    {
        var node = JsonSerializer.SerializeToNode(bill)!.AsObject();
        foreach (var key in new[] { "Status", "LockedAt", "ExportedAt", "ExportFilePath", "IsPaid", "PaidAt", "CreatedAt", "UpdatedAt", "PriceTierSnapshot", "HasPendingCloudChanges", "Revision" })
            node.Remove(key);
        void Strip(JsonNode? item)
        {
            if (item is JsonObject obj)
            {
                obj.Remove("Id"); obj.Remove("BillId"); obj.Remove("CreatedAt");
                // Order.Lines is a convenience view of bill.Lines, not another snapshot table.
                if (obj.ContainsKey("OrderNameSnapshot")) obj.Remove("Lines");
                foreach (var child in obj.ToList()) Strip(child.Value);
            }
            else if (item is JsonArray array) foreach (var child in array) Strip(child);
        }
        Strip(node);
        return node.ToJsonString();
    }

    private static CustomerBill MapBill(CustomerBillDto dto) => new()
    {
        Id = dto.id,
        BillNumber = dto.bill_number,
        BillType = Enum.TryParse<BillType>(dto.bill_type, out var bt) ? bt : BillType.Customer,
        CustomerId = dto.customer_id,
        CustomerNameSnapshot = dto.customer_name_snapshot,
        PhoneSnapshot = dto.phone_snapshot,
        ShippingAddressSnapshot = dto.shipping_address_snapshot,
        PeriodStart = dto.period_start,
        PeriodEnd = dto.period_end,
        Status = Enum.TryParse<CustomerBillStatus>(dto.status, out var st) ? st : CustomerBillStatus.Draft,
        ProductSubtotal = dto.product_subtotal,
        AdjustmentsTotal = dto.adjustments_total,
        GrandTotal = dto.grand_total,
        Note = dto.note,
        ExportFilePath = dto.export_file_path,
        LockedAt = !string.IsNullOrEmpty(dto.locked_at) ? DateTimeOffset.Parse(dto.locked_at) : null,
        ExportedAt = !string.IsNullOrEmpty(dto.exported_at) ? DateTimeOffset.Parse(dto.exported_at) : null,
        IsPaid = dto.is_paid == 1,
        PaidAt = !string.IsNullOrEmpty(dto.paid_at) ? DateTimeOffset.Parse(dto.paid_at) : null,
        Revision = dto.revision,
        CreatedAt = DateTimeOffset.Parse(dto.created_at),
        UpdatedAt = DateTimeOffset.Parse(dto.updated_at)
    };

    private static CustomerBillOrder MapOrder(CustomerBillOrderDto dto) => new()
    {
        Id = dto.id,
        BillId = dto.bill_id,
        OrderId = dto.order_id,
        OrderCodeSnapshot = dto.order_code_snapshot,
        OrderNameSnapshot = dto.order_name_snapshot,
        OrderDateSnapshot = dto.order_date_snapshot,
        OriginalFolderNameSnapshot = dto.original_folder_name_snapshot,
        Subtotal = dto.subtotal,
        SortOrder = (int)dto.sort_order,
        IsIncluded = dto.is_included == 1,
        IsFromPreviousPeriod = dto.is_from_previous_period == 1,
        SourceFolderPath = dto.source_folder_path
    };

    private static GuestBillSourceFolder MapSourceFolder(GuestBillSourceFolderDto dto) => new()
    {
        Id = dto.id,
        BillId = dto.bill_id,
        FolderPath = dto.folder_path,
        NormalizedFolderPath = dto.normalized_folder_path,
        CreatedAt = DateTimeOffset.Parse(dto.created_at)
    };

    private static CustomerBillLine MapLine(CustomerBillLineDto dto) => new()
    {
        Id = dto.id,
        BillId = dto.bill_id,
        OrderId = dto.order_id,
        ProductJobId = dto.product_job_id,
        ProductSpecificationId = dto.product_specification_id,
        SpecificationFolderName = dto.specification_folder_name,
        ProductNameSnapshot = dto.product_name_snapshot,
        VariantSnapshot = dto.variant_snapshot,
        BillingMethodSnapshot = Enum.TryParse<BillingMethod>(dto.billing_method_snapshot, out var bm) ? bm : BillingMethod.FileCount,
        ScannedQuantity = (int)dto.scanned_quantity,
        BilledQuantity = (int)dto.billed_quantity,
        QuantityOverrideReason = dto.quantity_override_reason,
        SheetCount = (int?)dto.sheet_count,
        IncludedSheetsSnapshot = (int?)dto.included_sheets_snapshot,
        ExtraSheetCount = (int?)dto.extra_sheet_count,
        BasePriceSnapshot = dto.base_price_snapshot,
        ExtraSheetPriceSnapshot = dto.extra_sheet_price_snapshot,
        ConfiguredUnitPrice = dto.configured_unit_price,
        BilledUnitPrice = dto.billed_unit_price,
        PriceOverrideReason = dto.price_override_reason,
        LineTotal = dto.line_total,
        IsIncluded = dto.is_included == 1,
        SortOrder = (int)dto.sort_order,
        FinalPrintFolderPath = dto.final_print_folder_path,
        FolderResolutionModeSnapshot = !string.IsNullOrEmpty(dto.folder_resolution_mode_snapshot) && Enum.TryParse<BillingFolderResolutionMode>(dto.folder_resolution_mode_snapshot, out var f) ? f : BillingFolderResolutionMode.AutoResolved,
        IssueMessage = dto.issue_message,
        Note = dto.note
    };

    private static BillAdjustment MapAdjustment(CustomerBillAdjustmentDto dto) => new()
    {
        Id = dto.id,
        BillId = dto.bill_id,
        Type = Enum.TryParse<AdjustmentType>(dto.type, out var t) ? t : AdjustmentType.Custom,
        Label = dto.label,
        Direction = Enum.TryParse<AdjustmentDirection>(dto.direction, out var d) ? d : AdjustmentDirection.Add,
        Amount = dto.amount,
        Note = dto.note,
        SortOrder = (int)dto.sort_order
    };

    private class CustomerBillDto
    {
        public long id { get; set; }
        public string bill_number { get; set; } = string.Empty;
        public string? bill_type { get; set; }
        public long? customer_id { get; set; }
        public string customer_name_snapshot { get; set; } = string.Empty;
        public string? phone_snapshot { get; set; }
        public string? shipping_address_snapshot { get; set; }
        public string period_start { get; set; } = string.Empty;
        public string period_end { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public long product_subtotal { get; set; }
        public long adjustments_total { get; set; }
        public long grand_total { get; set; }
        public string? note { get; set; }
        public string? export_file_path { get; set; }
        public string? locked_at { get; set; }
        public string? exported_at { get; set; }
        public long is_paid { get; set; }
        public string? paid_at { get; set; }
        public long revision { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }

    private class CustomerBillOrderDto
    {
        public long id { get; set; }
        public long bill_id { get; set; }
        public long order_id { get; set; }
        public string? order_code_snapshot { get; set; }
        public string order_name_snapshot { get; set; } = string.Empty;
        public string order_date_snapshot { get; set; } = string.Empty;
        public string original_folder_name_snapshot { get; set; } = string.Empty;
        public long subtotal { get; set; }
        public long sort_order { get; set; }
        public long is_included { get; set; }
        public long is_from_previous_period { get; set; }
        public string? source_folder_path { get; set; }
    }

    private class CustomerBillLineDto
    {
        public long id { get; set; }
        public long bill_id { get; set; }
        public long order_id { get; set; }
        public long product_job_id { get; set; }
        public long? product_specification_id { get; set; }
        public string? specification_folder_name { get; set; }
        public string product_name_snapshot { get; set; } = string.Empty;
        public string? variant_snapshot { get; set; }
        public string? billing_method_snapshot { get; set; }
        public long scanned_quantity { get; set; }
        public long billed_quantity { get; set; }
        public string? quantity_override_reason { get; set; }
        public long? sheet_count { get; set; }
        public long? included_sheets_snapshot { get; set; }
        public long? extra_sheet_count { get; set; }
        public long? base_price_snapshot { get; set; }
        public long? extra_sheet_price_snapshot { get; set; }
        public long configured_unit_price { get; set; }
        public long billed_unit_price { get; set; }
        public string? price_override_reason { get; set; }
        public long line_total { get; set; }
        public long is_included { get; set; }
        public long sort_order { get; set; }
        public string? final_print_folder_path { get; set; }
        public string? folder_resolution_mode_snapshot { get; set; }
        public string? issue_message { get; set; }
        public string? note { get; set; }
    }

    private class CustomerBillAdjustmentDto
    {
        public long id { get; set; }
        public long bill_id { get; set; }
        public string type { get; set; } = string.Empty;
        public string label { get; set; } = string.Empty;
        public string direction { get; set; } = string.Empty;
        public long amount { get; set; }
        public string? note { get; set; }
        public long sort_order { get; set; }
    }

    private class GuestBillSourceFolderDto
    {
        public long id { get; set; }
        public long bill_id { get; set; }
        public string folder_path { get; set; } = string.Empty;
        public string normalized_folder_path { get; set; } = string.Empty;
        public string created_at { get; set; } = string.Empty;
    }
}
