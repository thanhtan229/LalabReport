using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;

namespace LalabAutoReport.Infrastructure.Data;

public class SqliteOrderRepository : IOrderRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteOrderRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private const string OrderItemSelectSql = @"
        SELECT i.*, ps.canonical_name AS spec_canonical_name, ps.unit_price AS unit_price,
               ps.category AS category, ps.billing_method AS billing_method,
               ps.included_sheets AS included_sheets, ps.base_price AS base_price,
               ps.extra_sheet_price AS extra_sheet_price
        FROM order_item_scans i
        LEFT JOIN print_specifications ps ON i.print_specification_id = ps.id
        WHERE i.order_id = @OrderId
        AND i.scan_snapshot_id = (SELECT MAX(id) FROM scan_snapshots WHERE order_id = @OrderId)
    ";

    private const string SnapshotItemSelectSql = @"
        SELECT i.*, ps.canonical_name AS spec_canonical_name, ps.unit_price AS unit_price,
               ps.category AS category, ps.billing_method AS billing_method,
               ps.included_sheets AS included_sheets, ps.base_price AS base_price,
               ps.extra_sheet_price AS extra_sheet_price
        FROM order_item_scans i
        LEFT JOIN print_specifications ps ON i.print_specification_id = ps.id
        WHERE i.scan_snapshot_id = @SnapshotId
    ";

    private const string BaseOrderSelectSql = @"
        SELECT o.*, EXISTS(SELECT 1 FROM cloud_operational_outbox pending WHERE pending.entity_id=o.id AND pending.entity_type IN ('order_delivered','order_note')) AS pending_cloud_changes, c.canonical_name AS customer_canonical_name, rf.name AS root_folder_name
        FROM orders o
        LEFT JOIN customers c ON o.customer_id = c.id
        LEFT JOIN root_folders rf ON o.root_folder_id = rf.id
    ";

    public async Task<Order?> GetOrderByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDto = await connection.QuerySingleOrDefaultAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.id = @Id",
            new { Id = id }
        );

        if (orderDto == null) return null;

        var order = MapOrder(orderDto);

        var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });

        order.Items = items.Select(MapOrderItem).ToList();
        return order;
    }

    public async Task<Order?> GetOrderByCodeAsync(string orderCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return null;

        using var connection = _connectionFactory.CreateConnection();

        var orderDto = await connection.QuerySingleOrDefaultAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.order_code = @OrderCode",
            new { OrderCode = orderCode.Trim() }
        );

        if (orderDto == null) return null;

        var order = MapOrder(orderDto);
        var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });
        order.Items = items.Select(MapOrderItem).ToList();
        return order;
    }

    public async Task<int> GetMaxOrderSequenceForDateAsync(string workDate, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var codes = await connection.QueryAsync<string>(@"
            SELECT order_code 
            FROM orders 
            WHERE work_date = @WorkDate 
              AND order_code IS NOT NULL 
              AND order_code != ''",
            new { WorkDate = workDate });

        int maxSeq = 0;
        foreach (var code in codes)
        {
            if (OrderCodeGenerator.TryParseSequence(code, workDate, out int seq))
            {
                if (seq > maxSeq) maxSeq = seq;
            }
        }

        return maxSeq;
    }

    public async Task<Order?> GetOrderByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDto = await connection.QuerySingleOrDefaultAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.relative_path = @RelativePath",
            new { RelativePath = relativePath }
        );

        if (orderDto == null) return null;

        var order = MapOrder(orderDto);

        // Load items from latest scan
        var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });

        order.Items = items.Select(MapOrderItem).ToList();
        return order;
    }

    public async Task<Order?> GetOrderByRootAndRelativePathAsync(long rootFolderId, string relativePath, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDto = await connection.QuerySingleOrDefaultAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.root_folder_id = @RootFolderId AND o.relative_path = @RelativePath",
            new { RootFolderId = rootFolderId, RelativePath = relativePath }
        );

        if (orderDto == null) return null;

        var order = MapOrder(orderDto);
        var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });
        order.Items = items.Select(MapOrderItem).ToList();
        return order;
    }

    public async Task<IReadOnlyList<Order>> GetOrdersByDateAsync(string date, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDtos = await connection.QueryAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.work_date = @WorkDate ORDER BY o.original_folder_name",
            new { WorkDate = date }
        );

        var orders = new List<Order>();
        foreach (var dto in orderDtos)
        {
            var order = MapOrder(dto);
            var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });
            order.Items = items.Select(MapOrderItem).ToList();
            orders.Add(order);
        }

        return orders;
    }

    public async Task<IReadOnlyList<Order>> GetOrdersByDateAndRootAsync(string date, long rootFolderId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDtos = await connection.QueryAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.work_date = @WorkDate AND o.root_folder_id = @RootFolderId ORDER BY o.original_folder_name",
            new { WorkDate = date, RootFolderId = rootFolderId }
        );

        var orders = new List<Order>();
        foreach (var dto in orderDtos)
        {
            var order = MapOrder(dto);
            var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });
            order.Items = items.Select(MapOrderItem).ToList();
            orders.Add(order);
        }

        return orders;
    }

    public async Task<int> GetOrderCountByRootFolderIdAsync(long rootFolderId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM orders WHERE root_folder_id = @RootFolderId;",
            new { RootFolderId = rootFolderId }
        );
    }

    public async Task<IReadOnlyList<Order>> GetOrdersByDateRangeAsync(string startDate, string endDate, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDtos = await connection.QueryAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.work_date >= @StartDate AND o.work_date <= @EndDate ORDER BY o.work_date, o.original_folder_name",
            new { StartDate = startDate, EndDate = endDate }
        );

        var orders = new List<Order>();
        foreach (var dto in orderDtos)
        {
            var order = MapOrder(dto);
            var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });
            order.Items = items.Select(MapOrderItem).ToList();
            orders.Add(order);
        }

        return orders;
    }

    public async Task<IReadOnlyList<Order>> GetOrdersByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var orderDtos = await connection.QueryAsync<OrderDto>(
            BaseOrderSelectSql + " WHERE o.customer_id = @CustomerId ORDER BY o.work_date, o.original_folder_name",
            new { CustomerId = customerId }
        );

        var orders = new List<Order>();
        foreach (var dto in orderDtos)
        {
            var order = MapOrder(dto);
            var items = await connection.QueryAsync<OrderItemDto>(OrderItemSelectSql, new { OrderId = order.Id });
            order.Items = items.Select(MapOrderItem).ToList();
            orders.Add(order);
        }

        return orders;
    }


    public async Task SaveOrderAsync(Order order, ScanSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        // 1. Insert or update order
        if (order.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(order.OrderCode))
            {
                var existingCodes = await connection.QueryAsync<string>(@"
                    SELECT order_code 
                    FROM orders 
                    WHERE work_date = @WorkDate 
                      AND order_code IS NOT NULL 
                      AND order_code != ''",
                    new { WorkDate = order.WorkDate }, transaction: transaction);

                int maxSeq = 0;
                foreach (var code in existingCodes)
                {
                    if (OrderCodeGenerator.TryParseSequence(code, order.WorkDate, out int seq))
                    {
                        if (seq > maxSeq) maxSeq = seq;
                    }
                }
                order.OrderCode = OrderCodeGenerator.Generate(order.WorkDate, maxSeq + 1);
            }

            long orderId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO orders (order_code, work_date, customer_id, order_kind, order_name, original_folder_name, relative_path, status, is_printed, printed_at, print_progress, is_delivered, delivered_at, delivered_by, note, filesystem_changed_after_lock, fingerprint, last_scan_at, created_at, updated_at, root_folder_id, thumbnail_candidate_relative_path, delivered_revision, printed_revision, note_revision)
                VALUES (@OrderCode, @WorkDate, @CustomerId, @OrderKind, @OrderName, @OriginalFolderName, @RelativePath, @Status, @IsPrinted, @PrintedAt, @PrintProgress, @IsDelivered, @DeliveredAt, @DeliveredBy, @Note, @FilesystemChangedAfterLock, @Fingerprint, @LastScanAt, @CreatedAt, @UpdatedAt, @RootFolderId, @ThumbnailCandidateRelativePath, @DeliveredRevision, @PrintedRevision, @NoteRevision);
                SELECT last_insert_rowid();
            ", new
            {
                OrderCode = order.OrderCode,
                WorkDate = order.WorkDate,
                CustomerId = order.CustomerId,
                OrderKind = order.OrderKind.ToString(),
                OrderName = order.OrderName,
                OriginalFolderName = order.OriginalFolderName,
                RelativePath = order.RelativePath,
                Status = order.Status.ToString(),
                DeliveredRevision = order.DeliveredRevision,
                PrintedRevision = order.PrintedRevision,
                NoteRevision = order.NoteRevision,
                ObservedPrintedRevision = order.PrintedRevision,
                IsPrinted = order.IsPrinted ? 1 : 0,
                PrintedAt = order.PrintedAt?.ToString("o"),
                PrintProgress = (int)order.PrintProgress,
                IsDelivered = order.IsDelivered ? 1 : 0,
                DeliveredAt = order.DeliveredAt?.ToString("o"),
                DeliveredBy = order.DeliveredBy,
                Note = order.Note,
                FilesystemChangedAfterLock = order.FilesystemChangedAfterLock ? 1 : 0,
                Fingerprint = order.Fingerprint,
                LastScanAt = order.LastScanAt?.ToString("o"),
                CreatedAt = order.CreatedAt.ToString("o"),
                UpdatedAt = order.UpdatedAt.ToString("o"),
                RootFolderId = order.RootFolderId,
                ThumbnailCandidateRelativePath = order.ThumbnailCandidateRelativePath
            }, transaction: transaction);

            order.Id = orderId;
        }
        else
        {
            await connection.ExecuteAsync(@"
                UPDATE orders
                SET order_code = CASE WHEN (order_code IS NULL OR order_code = '') AND @OrderCode != '' THEN @OrderCode ELSE order_code END,
                    customer_id = @CustomerId,
                    order_kind = @OrderKind,
                    order_name = @OrderName,
                    status = @Status,
                    is_printed = CASE WHEN printed_revision = @ObservedPrintedRevision THEN @IsPrinted ELSE is_printed END,
                    printed_at = CASE WHEN printed_revision = @ObservedPrintedRevision THEN @PrintedAt ELSE printed_at END,
                    print_progress = CASE WHEN printed_revision = @ObservedPrintedRevision THEN @PrintProgress ELSE print_progress END,
                    filesystem_changed_after_lock = @FilesystemChangedAfterLock,
                    fingerprint = @Fingerprint,
                    last_scan_at = @LastScanAt,
                    updated_at = @UpdatedAt,
                    root_folder_id = CASE WHEN @RootFolderId IS NOT NULL THEN @RootFolderId ELSE root_folder_id END,
                    thumbnail_candidate_relative_path = @ThumbnailCandidateRelativePath
                WHERE id = @Id
            ", new
            {
                Id = order.Id,
                OrderCode = order.OrderCode ?? string.Empty,
                CustomerId = order.CustomerId,
                OrderKind = order.OrderKind.ToString(),
                OrderName = order.OrderName,
                Status = order.Status.ToString(),
                ObservedPrintedRevision = order.PrintedRevision,
                IsPrinted = order.IsPrinted ? 1 : 0,
                PrintedAt = order.PrintedAt?.ToString("o"),
                PrintProgress = (int)order.PrintProgress,
                IsDelivered = order.IsDelivered ? 1 : 0,
                DeliveredAt = order.DeliveredAt?.ToString("o"),
                DeliveredBy = order.DeliveredBy,
                Note = order.Note,
                FilesystemChangedAfterLock = order.FilesystemChangedAfterLock ? 1 : 0,
                Fingerprint = order.Fingerprint,
                LastScanAt = order.LastScanAt?.ToString("o"),
                UpdatedAt = order.UpdatedAt.ToString("o"),
                RootFolderId = order.RootFolderId,
                ThumbnailCandidateRelativePath = order.ThumbnailCandidateRelativePath
            }, transaction: transaction);
        }

        // 2. Insert scan snapshot
        snapshot.OrderId = order.Id;
        long snapshotId = await connection.QuerySingleAsync<long>(@"
            INSERT INTO scan_snapshots (order_id, started_at, completed_at, scan_scope, status, error_message, app_version)
            VALUES (@OrderId, @StartedAt, @CompletedAt, @ScanScope, @Status, @ErrorMessage, @AppVersion);
            SELECT last_insert_rowid();
        ", new
        {
            OrderId = snapshot.OrderId,
            StartedAt = snapshot.StartedAt.ToString("o"),
            CompletedAt = snapshot.CompletedAt?.ToString("o"),
            ScanScope = snapshot.Scope.ToString(),
            Status = snapshot.Status.ToString(),
            ErrorMessage = snapshot.ErrorMessage,
            AppVersion = snapshot.AppVersion
        }, transaction: transaction);

        snapshot.Id = snapshotId;

        // 3. Insert order item scans
        foreach (var item in order.Items)
        {
            item.ScanSnapshotId = snapshotId;
            item.OrderId = order.Id;

            string candidatesJson = JsonSerializer.Serialize(item.CandidatePrintFolderRelativePaths);

            long itemId = await connection.QuerySingleAsync<long>(@"
                INSERT INTO order_item_scans (
                    scan_snapshot_id, order_id, print_specification_id, specification_folder_name,
                    specification_relative_path, source_count, print_count, printable_file_count,
                    selected_print_folder_relative_path, print_folder_status, mismatch_count,
                    is_printed, printed_at,
                    scan_status, error_message, candidate_print_folders, bill_quantity,
                    quantity_resolution_mode, quantity_resolution_note, billing_metadata_json,
                    folder_resolution_mode, customer_bill_id
                ) VALUES (
                    @ScanSnapshotId, @OrderId, @PrintSpecificationId, @SpecificationFolderName,
                    @SpecificationRelativePath, @SourceCount, @PrintCount, @PrintableFileCount,
                    @SelectedPrintFolderRelativePath, @PrintFolderStatus, @MismatchCount,
                    @IsPrinted, @PrintedAt,
                    @ScanStatus, @ErrorMessage, @CandidatePrintFolders, @BillQuantity,
                    @QuantityResolutionMode, @QuantityResolutionNote, @BillingMetadataJson,
                    @FolderResolutionMode, @CustomerBillId
                );
                SELECT last_insert_rowid();
            ", new
            {
                ScanSnapshotId = item.ScanSnapshotId,
                OrderId = item.OrderId,
                PrintSpecificationId = item.PrintSpecificationId,
                SpecificationFolderName = item.SpecificationFolderName,
                SpecificationRelativePath = item.SpecificationRelativePath,
                SourceCount = item.SourceCount,
                PrintCount = item.PrintCount,
                PrintableFileCount = item.PrintableFileCount,
                SelectedPrintFolderRelativePath = item.SelectedPrintFolderRelativePath,
                PrintFolderStatus = item.PrintFolderStatus.ToString(),
                MismatchCount = item.MismatchCount,
                IsPrinted = item.IsPrinted ? 1 : 0,
                PrintedAt = item.PrintedAt?.ToString("o"),
                ScanStatus = item.ScanStatus.ToString(),
                ErrorMessage = item.ErrorMessage,
                CandidatePrintFolders = candidatesJson,
                BillQuantity = item.BillQuantity,
                QuantityResolutionMode = item.QuantityResolutionMode?.ToString(),
                QuantityResolutionNote = item.QuantityResolutionNote,
                BillingMetadataJson = item.BillingMetadataJson,
                FolderResolutionMode = item.FolderResolutionMode.ToString(),
                CustomerBillId = item.CustomerBillId
            }, transaction: transaction);

            item.Id = itemId;
        }


        transaction.Commit();
    }

    public async Task<IReadOnlyList<string>> GetScannedDatesInMonthAsync(string yearMonth, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var dates = await connection.QueryAsync<string>(@"
            SELECT DISTINCT work_date FROM orders
            WHERE work_date LIKE @Pattern AND last_scan_at IS NOT NULL
        ", new { Pattern = yearMonth + "%" });

        return dates.ToList();
    }

    public async Task UpdateOrderItemResolutionAsync(long orderItemId, int billQuantity, QuantityResolutionMode mode, string? note, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        await connection.ExecuteAsync(@"
            UPDATE order_item_scans
            SET bill_quantity = @BillQuantity,
                quantity_resolution_mode = @Mode,
                quantity_resolution_note = @Note
            WHERE id = @Id
        ", new
        {
            Id = orderItemId,
            BillQuantity = billQuantity,
            Mode = mode.ToString(),
            Note = note
        });
    }

    public async Task UpdateOrderItemPrintFolderAsync(long orderItemId, string printFolderRelativePath, int printCount, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        await connection.ExecuteAsync(@"
            UPDATE order_item_scans
            SET selected_print_folder_relative_path = @Path,
                print_count = @Count,
                print_folder_status = @Status,
                mismatch_count = @Count - source_count,
                folder_resolution_mode = @FolderResolutionMode
            WHERE id = @Id
        ", new
        {
            Id = orderItemId,
            Path = printFolderRelativePath,
            Count = printCount,
            Status = PrintFolderResolutionStatus.Resolved.ToString(),
            FolderResolutionMode = BillingFolderResolutionMode.ManuallySelected.ToString()
        });
    }

    public async Task UpdateOrderStatusAsync(long orderId, OrderStatus status, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET status = @Status,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = orderId,
            Status = status.ToString(),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task SetFilesystemChangedAfterLockAsync(long orderId, bool changed, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET filesystem_changed_after_lock = @Changed,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = orderId,
            Changed = changed ? 1 : 0,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task SetCustomerBillIdForItemsAsync(IEnumerable<long> orderItemIds, long? customerBillId, CancellationToken cancellationToken = default)
    {
        var idList = orderItemIds.ToList();
        if (idList.Count == 0) return;

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE order_item_scans
            SET customer_bill_id = @CustomerBillId
            WHERE id IN @Ids
        ", new { CustomerBillId = customerBillId, Ids = idList });
    }

    public async Task UpdateOrderCustomerIdAsync(long orderId, long customerId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET customer_id = @CustomerId,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = orderId,
            CustomerId = customerId,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task UpdateFolderCustomerIdAsync(string workDate, string originalFolderName, long? customerId, string? orderName = null, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET customer_id = @CustomerId,
                order_name = CASE WHEN @OrderName IS NOT NULL THEN @OrderName ELSE order_name END,
                updated_at = @UpdatedAt
            WHERE work_date = @WorkDate
              AND original_folder_name = @OriginalFolderName
              AND status != 'Locked'
        ", new
        {
            WorkDate = workDate,
            OriginalFolderName = originalFolderName,
            CustomerId = customerId,
            OrderName = orderName,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task DeleteOrderAsync(long orderId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var orderStatus = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT status FROM orders WHERE id = @OrderId", new { OrderId = orderId }, transaction: transaction);

        if (string.Equals(orderStatus, OrderStatus.Locked.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(orderStatus, OrderStatus.Billed.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Không thể xóa đơn hàng đã chốt hoặc đã khóa hóa đơn.");
        }

        var lockedBillsCount = await connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) 
            FROM customer_bill_orders cbo 
            JOIN customer_bills cb ON cbo.bill_id = cb.id 
            WHERE cbo.order_id = @OrderId AND cb.status IN ('Locked', 'Exported')",
            new { OrderId = orderId }, transaction: transaction);

        if (lockedBillsCount > 0)
        {
            throw new InvalidOperationException("Không thể xóa đơn hàng nằm trong hóa đơn đã khóa hoặc đã xuất.");
        }

        await connection.ExecuteAsync(@"
            DELETE FROM customer_bill_lines WHERE product_job_id IN (SELECT id FROM order_item_scans WHERE order_id = @OrderId);
            DELETE FROM customer_bill_orders WHERE order_id = @OrderId;
            DELETE FROM customer_bill_adjustments WHERE bill_id IN (SELECT id FROM customer_bills WHERE id NOT IN (SELECT bill_id FROM customer_bill_orders) AND status = 'Draft');
            DELETE FROM customer_bills WHERE id NOT IN (SELECT bill_id FROM customer_bill_orders) AND status = 'Draft';
            DELETE FROM bill_lines WHERE bill_id IN (SELECT id FROM bills WHERE order_id = @OrderId);
            DELETE FROM bills WHERE order_id = @OrderId;
            DELETE FROM order_item_scans WHERE order_id = @OrderId;
            DELETE FROM scan_snapshots WHERE order_id = @OrderId;
            DELETE FROM orders WHERE id = @OrderId;
        ", new { OrderId = orderId }, transaction: transaction);

        transaction.Commit();
    }

    public async Task UpdateOrderPrintedStatusAsync(long orderId, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET is_printed = @IsPrinted,
                printed_at = @PrintedAt,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = orderId,
            IsPrinted = isPrinted ? 1 : 0,
            PrintedAt = printedAt?.ToString("o"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task UpdateOrderPrintedStatusByRelativePathAsync(string relativePath, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;

        using var connection = _connectionFactory.CreateConnection();
        var count = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM orders WHERE relative_path=@RelativePath", new { RelativePath = relativePath });
        if (count > 1) throw new InvalidOperationException("Đường dẫn có ở nhiều root; cập nhật theo Order ID.");
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET is_printed = @IsPrinted,
                printed_at = @PrintedAt,
                updated_at = @UpdatedAt
            WHERE relative_path = @RelativePath
        ", new
        {
            RelativePath = relativePath,
            IsPrinted = isPrinted ? 1 : 0,
            PrintedAt = printedAt?.ToString("o"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task UpdateOrderPrintProgressAsync(long orderId, PrintStatus progress, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders
            SET print_progress = @PrintProgress,
                is_printed = @IsPrinted,
                printed_at = @PrintedAt,
                updated_at = @UpdatedAt
            WHERE id = @OrderId;
        ", new
        {
            OrderId = orderId,
            PrintProgress = (int)progress,
            IsPrinted = isPrinted ? 1 : 0,
            PrintedAt = printedAt?.ToString("o"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
    }

    public async Task UpdateOrderItemPrintedStatusAsync(long orderItemId, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE order_item_scans
            SET is_printed = @IsPrinted,
                printed_at = @PrintedAt
            WHERE id = @OrderItemId;
        ", new
        {
            OrderItemId = orderItemId,
            IsPrinted = isPrinted ? 1 : 0,
            PrintedAt = printedAt?.ToString("o")
        });
    }

    public async Task UpdateOrderDeliveredStatusAsync(long orderId, bool isDelivered, DateTimeOffset? deliveredAt, string? deliveredBy = null, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        int affected = await connection.ExecuteAsync(@"
            UPDATE orders
            SET is_delivered = @IsDelivered,
                delivered_at = @DeliveredAt,
                delivered_by = @DeliveredBy,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = orderId,
            IsDelivered = isDelivered ? 1 : 0,
            DeliveredAt = deliveredAt?.ToString("o"),
            DeliveredBy = deliveredBy,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        }, transaction);
        if (affected != 1) throw new InvalidOperationException("Order not found.");
        await SqliteCloudSyncStateRepository.EnqueueAsync(connection, transaction, "order_delivered", orderId, isDelivered, deliveredBy);
        transaction.Commit();
    }

    public async Task UpdateOrderNoteAsync(long orderId, string? note, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        int affected = await connection.ExecuteAsync(@"
            UPDATE orders
            SET note = @Note,
                updated_at = @UpdatedAt
            WHERE id = @Id
        ", new
        {
            Id = orderId,
            Note = note,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o")
        }, transaction);
        if (affected != 1) throw new InvalidOperationException("Order not found.");
        await SqliteCloudSyncStateRepository.EnqueueAsync(connection, transaction, "order_note", orderId, note ?? "");
        transaction.Commit();
    }

    public async Task<IReadOnlyList<ScanSnapshot>> GetScanSnapshotsForOrderAsync(long orderId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var snapshotDtos = await connection.QueryAsync<ScanSnapshotDto>(
            "SELECT * FROM scan_snapshots WHERE order_id = @OrderId ORDER BY started_at DESC",
            new { OrderId = orderId });

        var snapshots = new List<ScanSnapshot>();
        foreach (var dto in snapshotDtos)
        {
            var items = await connection.QueryAsync<OrderItemDto>(SnapshotItemSelectSql, new { SnapshotId = dto.id });

            snapshots.Add(new ScanSnapshot
            {
                Id = dto.id,
                OrderId = dto.order_id,
                StartedAt = DateTimeOffset.Parse(dto.started_at),
                CompletedAt = !string.IsNullOrEmpty(dto.completed_at) ? DateTimeOffset.Parse(dto.completed_at) : null,
                Scope = Enum.TryParse<ScanScope>(dto.scan_scope, out var s) ? s : ScanScope.Date,
                Status = Enum.TryParse<ScanStatus>(dto.status, out var st) ? st : ScanStatus.Pending,
                ErrorMessage = dto.error_message,
                AppVersion = dto.app_version,
                Items = items.Select(MapOrderItem).ToList()
            });
        }

        return snapshots;
    }

    private class ScanSnapshotDto
    {
        public long id { get; set; }
        public long order_id { get; set; }
        public string started_at { get; set; } = string.Empty;
        public string? completed_at { get; set; }
        public string scan_scope { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public string? error_message { get; set; }
        public string app_version { get; set; } = string.Empty;
    }

    private static Order MapOrder(OrderDto dto)
    {
        var order = new Order
        {
            Id = dto.id,
            OrderCode = dto.order_code ?? string.Empty,
            WorkDate = dto.work_date,
            CustomerId = dto.customer_id,
            OrderKind = Enum.TryParse<OrderKind>(dto.order_kind, out var ok) ? ok : OrderKind.Implicit,
            OrderName = dto.order_name,
            OriginalFolderName = dto.original_folder_name,
            RelativePath = dto.relative_path,
            Status = Enum.TryParse<OrderStatus>(dto.status, out var st) ? st : OrderStatus.Unscanned,
            IsPrinted = dto.is_printed == 1,
            PrintedAt = !string.IsNullOrEmpty(dto.printed_at) ? DateTimeOffset.Parse(dto.printed_at) : null,
            PrintProgress = (PrintStatus)dto.print_progress,
            HasPendingCloudChanges = dto.pending_cloud_changes != 0,
            IsDelivered = dto.is_delivered == 1,
            DeliveredAt = !string.IsNullOrEmpty(dto.delivered_at) ? DateTimeOffset.Parse(dto.delivered_at) : null,
            DeliveredBy = dto.delivered_by,
            Note = dto.note,
            DeliveredRevision = dto.delivered_revision,
            PrintedRevision = dto.printed_revision,
            NoteRevision = dto.note_revision,
            FilesystemChangedAfterLock = dto.filesystem_changed_after_lock == 1,
            Fingerprint = dto.fingerprint,
            LastScanAt = !string.IsNullOrEmpty(dto.last_scan_at) ? DateTimeOffset.Parse(dto.last_scan_at) : null,
            CreatedAt = DateTimeOffset.Parse(dto.created_at),
            UpdatedAt = DateTimeOffset.Parse(dto.updated_at),
            RootFolderId = dto.root_folder_id,
            RootFolderName = dto.root_folder_name,
            ThumbnailCandidateRelativePath = dto.thumbnail_candidate_relative_path
        };

        if (dto.customer_id.HasValue && !string.IsNullOrEmpty(dto.customer_canonical_name))
        {
            order.Customer = new Customer
            {
                Id = dto.customer_id.Value,
                CanonicalName = dto.customer_canonical_name
            };
        }

        return order;
    }

    private static OrderItemScan MapOrderItem(OrderItemDto dto)
    {
        var item = new OrderItemScan
        {
            Id = dto.id,
            ScanSnapshotId = dto.scan_snapshot_id,
            OrderId = dto.order_id,
            PrintSpecificationId = dto.print_specification_id,
            SpecificationFolderName = dto.specification_folder_name,
            SpecificationRelativePath = dto.specification_relative_path,
            SourceCount = (int)dto.source_count,
            PrintCount = (int?)dto.print_count,
            PrintableFileCount = (int?)dto.printable_file_count ?? (int?)dto.print_count,
            SelectedPrintFolderRelativePath = dto.selected_print_folder_relative_path,
            PrintFolderStatus = Enum.TryParse<PrintFolderResolutionStatus>(dto.print_folder_status, out var pfs) ? pfs : PrintFolderResolutionStatus.NoPrintFolder,
            MismatchCount = (int?)dto.mismatch_count,
            IsPrinted = dto.is_printed == 1,
            PrintedAt = !string.IsNullOrEmpty(dto.printed_at) ? DateTimeOffset.Parse(dto.printed_at) : null,
            ScanStatus = Enum.TryParse<ScanStatus>(dto.scan_status, out var ss) ? ss : ScanStatus.Pending,
            ErrorMessage = dto.error_message,
            BillQuantity = (int?)dto.bill_quantity,
            QuantityResolutionMode = !string.IsNullOrEmpty(dto.quantity_resolution_mode) && Enum.TryParse<QuantityResolutionMode>(dto.quantity_resolution_mode, out var qrm) ? qrm : null,
            QuantityResolutionNote = dto.quantity_resolution_note,
            BillingMetadataJson = dto.billing_metadata_json,
            FolderResolutionMode = !string.IsNullOrEmpty(dto.folder_resolution_mode) && Enum.TryParse<BillingFolderResolutionMode>(dto.folder_resolution_mode, out var bfm) ? bfm : BillingFolderResolutionMode.AutoResolved,
            CustomerBillId = dto.customer_bill_id
        };

        if (dto.print_specification_id.HasValue)
        {
            item.PrintSpecification = new PrintSpecification
            {
                Id = dto.print_specification_id.Value,
                CanonicalName = dto.spec_canonical_name ?? dto.specification_folder_name,
                UnitPrice = dto.unit_price ?? 0,
                Category = Enum.TryParse<ProductCategory>(dto.category, out var cat) ? cat : ProductCategory.PhotoPrint,
                BillingMethod = Enum.TryParse<BillingMethod>(dto.billing_method, out var bm) ? bm : BillingMethod.FileCount,
                IncludedSheets = dto.included_sheets,
                BasePrice = dto.base_price,
                ExtraSheetPrice = dto.extra_sheet_price
            };
        }

        if (!string.IsNullOrWhiteSpace(dto.candidate_print_folders))
        {
            try
            {
                item.CandidatePrintFolderRelativePaths = JsonSerializer.Deserialize<List<string>>(dto.candidate_print_folders) ?? new();
            }
            catch
            {
                // ignored
            }
        }

        return item;
    }

    private class OrderDto
    {
        public int pending_cloud_changes { get; set; }
        public long id { get; set; }
        public string? order_code { get; set; }
        public string work_date { get; set; } = string.Empty;
        public long? customer_id { get; set; }
        public string order_kind { get; set; } = "Implicit";
        public string? order_name { get; set; }
        public string original_folder_name { get; set; } = string.Empty;
        public string relative_path { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public long is_printed { get; set; }
        public string? printed_at { get; set; }
        public int print_progress { get; set; }
        public long is_delivered { get; set; }
        public string? delivered_at { get; set; }
        public string? delivered_by { get; set; }
        public string? note { get; set; }
        public long delivered_revision { get; set; }
        public long printed_revision { get; set; }
        public long note_revision { get; set; }
        public long filesystem_changed_after_lock { get; set; }
        public string? fingerprint { get; set; }
        public string? last_scan_at { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
        public string? customer_canonical_name { get; set; }
        public long? root_folder_id { get; set; }
        public string? root_folder_name { get; set; }
        public string? thumbnail_candidate_relative_path { get; set; }
    }

    private class OrderItemDto
    {
        public long id { get; set; }
        public long scan_snapshot_id { get; set; }
        public long order_id { get; set; }
        public long? print_specification_id { get; set; }
        public string specification_folder_name { get; set; } = string.Empty;
        public string specification_relative_path { get; set; } = string.Empty;
        public long source_count { get; set; }
        public long? print_count { get; set; }
        public long? printable_file_count { get; set; }
        public string? selected_print_folder_relative_path { get; set; }
        public string print_folder_status { get; set; } = string.Empty;
        public long? mismatch_count { get; set; }
        public long is_printed { get; set; }
        public string? printed_at { get; set; }
        public string scan_status { get; set; } = string.Empty;
        public string? error_message { get; set; }
        public string? candidate_print_folders { get; set; }
        public long? bill_quantity { get; set; }
        public string? quantity_resolution_mode { get; set; }
        public string? quantity_resolution_note { get; set; }
        public string? billing_metadata_json { get; set; }
        public string? folder_resolution_mode { get; set; }
        public long? customer_bill_id { get; set; }
        public string? spec_canonical_name { get; set; }
        public long? unit_price { get; set; }
        public string? category { get; set; }
        public string? billing_method { get; set; }
        public int? included_sheets { get; set; }
        public long? base_price { get; set; }
        public long? extra_sheet_price { get; set; }
    }
}

