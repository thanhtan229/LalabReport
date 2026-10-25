import { acceptOperation } from './operations';
import {
  CloudOrderDto,
  CloudBillDto,
  SyncBatchPayload,
  OperationalEventDto,
  HeartbeatPayload,
  SystemStatusDto,
  PrintCommandDto
} from './types';

export async function getOrdersByDate(db: D1Database, date: string): Promise<CloudOrderDto[]> {
  const stmt = db.prepare(`
    SELECT * FROM cloud_orders
    WHERE work_date = ?
    ORDER BY id ASC
  `);
  const result = await stmt.bind(date).all();

  return (result.results ?? []).map((row: any) => {
    let items = [];
    try {
      items = JSON.parse(row.items_json || '[]');
    } catch { }

    return {
      id: row.id,
      orderCode: row.order_code,
      customerName: row.customer_name,
      folderName: row.folder_name,
      workDate: row.work_date,
      status: row.status,
      isPrinted: Boolean(row.is_printed),
      printedAt: row.printed_at,
      isDelivered: Boolean(row.is_delivered),
      deliveredAt: row.delivered_at,
      deliveredBy: row.delivered_by,
      note: row.note,
      hasIssues: Boolean(row.has_issues),
      isLocked: Boolean(row.is_locked),
      hasThumbnail: Boolean(row.has_thumbnail),
      thumbnailFileName: row.thumbnail_file_name,
      thumbnailStatus: row.thumbnail_status || (row.has_thumbnail ? 'READY' : 'NONE'),
      thumbnailKey: row.thumbnail_key || null,
      thumbnailVersion: row.thumbnail_version || null,
      customerBillId: row.customer_bill_id,
      totalQuantity: row.total_quantity,
      items: items
    };
  });
}

export async function getOrderById(db: D1Database, id: number): Promise<CloudOrderDto | null> {
  const row = await db.prepare('SELECT * FROM cloud_orders WHERE id = ?').bind(id).first<any>();
  if (!row) return null;

  let items = [];
  try {
    items = JSON.parse(row.items_json || '[]');
  } catch { }

  return {
    id: row.id,
    orderCode: row.order_code,
    customerName: row.customer_name,
    folderName: row.folder_name,
    workDate: row.work_date,
    status: row.status,
    isPrinted: Boolean(row.is_printed),
    printedAt: row.printed_at,
    isDelivered: Boolean(row.is_delivered),
    deliveredAt: row.delivered_at,
    deliveredBy: row.delivered_by,
    note: row.note,
    hasIssues: Boolean(row.has_issues),
    isLocked: Boolean(row.is_locked),
    hasThumbnail: Boolean(row.has_thumbnail),
    thumbnailFileName: row.thumbnail_file_name,
    thumbnailStatus: row.thumbnail_status || (row.has_thumbnail ? 'READY' : 'NONE'),
    thumbnailKey: row.thumbnail_key || null,
    thumbnailVersion: row.thumbnail_version || null,
    customerBillId: row.customer_bill_id,
    totalQuantity: row.total_quantity,
    items: items
  };
}

export async function getBillById(db: D1Database, id: number): Promise<CloudBillDto | null> {
  const row = await db.prepare('SELECT * FROM cloud_bills WHERE id = ?').bind(id).first<any>();
  if (!row) return null;

  let lines = [];
  let adjustments = [];
  try {
    lines = JSON.parse(row.lines_json || '[]');
  } catch { }
  try {
    adjustments = JSON.parse(row.adjustments_json || '[]');
  } catch { }

  return {
    id: row.id,
    billNumber: row.bill_number,
    billType: row.bill_type,
    customerId: row.customer_id,
    customerNameSnapshot: row.customer_name_snapshot,
    phoneSnapshot: row.phone_snapshot || null,
    shippingAddressSnapshot: row.shipping_address_snapshot || null,
    note: row.note || null,
    periodDate: row.period_date,
    totalAmount: row.total_amount,
    productSubtotal: row.product_subtotal,
    adjustmentsTotal: row.adjustments_total,
    isPaid: Boolean(row.is_paid),
    paidAt: row.paid_at,
    hasJpeg: Boolean(row.has_jpeg),
    lockedAt: row.locked_at,
    exportedAt: row.exported_at,
    lines: lines,
    adjustments: adjustments
  };
}

export async function getMonthlyReport(db: D1Database, year: number, month: number): Promise<any> {
  const reportKey = `month_${year}_${String(month).padStart(2, '0')}`;
  const row = await db.prepare('SELECT data_json FROM cloud_reports WHERE report_key = ?').bind(reportKey).first<any>();
  if (row && row.data_json) {
    try {
      return JSON.parse(row.data_json);
    } catch { }
  }

  return {
    year: year,
    month: month,
    totalOrders: 0,
    totalQuantity: 0,
    totalRevenue: 0,
    days: []
  };
}

export async function getUnpaidCustomers(db: D1Database): Promise<any> {
  // Payment mutations are authoritative immediately; a cached Desktop report
  // may predate a mobile collection and must not keep the bill in the debt list.
  const rows = (await db.prepare('SELECT * FROM cloud_bills WHERE is_paid = 0 ORDER BY id').all<any>()).results ?? [];
  const groups = new Map<string, any>();
  for (const row of rows) {
    const key = JSON.stringify([row.customer_id ?? 0, row.customer_name_snapshot]);
    let group = groups.get(key);
    if (!group) { group = { customerId: row.customer_id ?? 0, customerName: row.customer_name_snapshot,
      unpaidBillCount: 0, totalDebt: 0, bills: [] }; groups.set(key, group); }
    group.unpaidBillCount++; group.totalDebt += row.total_amount;
    group.bills.push({ id: row.id, billNumber: row.bill_number, date: row.period_date, grandTotal: row.total_amount });
  }
  return [...groups.values()].sort((a, b) => b.totalDebt - a.totalDebt);
}

export async function upsertSyncBatch(
  db: D1Database,
  batch: SyncBatchPayload
): Promise<{ ordersCount: number; billsCount: number; reportsCount: number }> {
  const now = new Date().toISOString();
  let ordersCount = 0;
  let billsCount = 0;
  let reportsCount = 0;

  const statements: D1PreparedStatement[] = [];

  if (batch.orders && batch.orders.length > 0) {
    for (const o of batch.orders) {
      statements.push(
        db.prepare(`
          INSERT INTO cloud_orders (
            id, order_code, customer_name, folder_name, work_date, status,
            is_printed, printed_at, is_delivered, delivered_at, delivered_by,
            note, has_issues, is_locked, has_thumbnail, thumbnail_file_name,
            thumbnail_status, thumbnail_key, thumbnail_version,
            customer_bill_id, total_quantity, items_json, updated_at
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
          ON CONFLICT(id) DO UPDATE SET
            order_code = excluded.order_code,
            customer_name = excluded.customer_name,
            folder_name = excluded.folder_name,
            work_date = excluded.work_date,
            status = excluded.status,
            is_printed = excluded.is_printed,
            printed_at = excluded.printed_at,
            has_issues = excluded.has_issues,
            is_locked = excluded.is_locked,
            has_thumbnail = excluded.has_thumbnail,
            thumbnail_file_name = excluded.thumbnail_file_name,
            thumbnail_status = excluded.thumbnail_status,
            thumbnail_key = excluded.thumbnail_key,
            thumbnail_version = excluded.thumbnail_version,
            customer_bill_id = excluded.customer_bill_id,
            total_quantity = excluded.total_quantity,
            items_json = excluded.items_json,
            updated_at = excluded.updated_at
        `).bind(
          o.id, o.orderCode, o.customerName, o.folderName, o.workDate, o.status,
          o.isPrinted ? 1 : 0, o.printedAt || null, o.isDelivered ? 1 : 0, o.deliveredAt || null, o.deliveredBy || null,
          o.note || null, o.hasIssues ? 1 : 0, o.isLocked ? 1 : 0, o.hasThumbnail ? 1 : 0, o.thumbnailFileName || null,
          o.thumbnailStatus || (o.hasThumbnail ? 'READY' : 'NONE'), o.thumbnailKey || null, o.thumbnailVersion || null,
          o.customerBillId || null, o.totalQuantity, o.itemsJson, now
        )
      );
      ordersCount++;
    }
  }

  if (batch.bills && batch.bills.length > 0) {
    for (const b of batch.bills) {
      statements.push(
        db.prepare(`
          INSERT INTO cloud_bills (
            id, bill_number, bill_type, customer_id, customer_name_snapshot,
            phone_snapshot, shipping_address_snapshot, note,
            period_date, total_amount, is_paid, paid_at, lines_json, adjustments_json,
            has_jpeg, locked_at, exported_at, updated_at, product_subtotal, adjustments_total
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
          ON CONFLICT(id) DO UPDATE SET
            bill_number = excluded.bill_number,
            bill_type = excluded.bill_type,
            customer_id = excluded.customer_id,
            customer_name_snapshot = excluded.customer_name_snapshot,
            phone_snapshot = excluded.phone_snapshot,
            shipping_address_snapshot = excluded.shipping_address_snapshot,
            note = excluded.note,
            period_date = excluded.period_date,
            total_amount = excluded.total_amount,
            product_subtotal = excluded.product_subtotal,
            adjustments_total = excluded.adjustments_total,
            lines_json = excluded.lines_json,
            adjustments_json = excluded.adjustments_json,
            has_jpeg = excluded.has_jpeg,
            locked_at = excluded.locked_at,
            exported_at = excluded.exported_at,
            updated_at = excluded.updated_at
        `).bind(
          b.id, b.billNumber, b.billType, b.customerId || null, b.customerNameSnapshot,
          b.phoneSnapshot || null, b.shippingAddressSnapshot || null, b.note || null,
          b.periodDate, b.totalAmount, b.isPaid ? 1 : 0, b.paidAt || null, b.linesJson, b.adjustmentsJson,
          b.hasJpeg ? 1 : 0, b.lockedAt || null, b.exportedAt || null, now, b.productSubtotal ?? null, b.adjustmentsTotal ?? null
        )
      );
      billsCount++;
    }
  }

  if (batch.reports && batch.reports.length > 0) {
    for (const r of batch.reports) {
      statements.push(
        db.prepare(`
          INSERT INTO cloud_reports (report_key, data_json, updated_at)
          VALUES (?, ?, ?)
          ON CONFLICT(report_key) DO UPDATE SET
            data_json = excluded.data_json,
            updated_at = excluded.updated_at
        `).bind(r.key, r.dataJson, now)
      );
      reportsCount++;
    }
  }

  statements.push(
    db.prepare(`
      INSERT INTO cloud_sync_meta (key, value)
      VALUES ('last_synced_at', ?)
      ON CONFLICT(key) DO UPDATE SET value = excluded.value
    `).bind(now)
  );

  if (statements.length > 0) {
    await db.batch(statements);
  }

  return { ordersCount, billsCount, reportsCount };
}

export async function toggleOrderDelivered(db: D1Database, orderId: number, deliveredBy?: string, operationId = crypto.randomUUID()) {
  const event = await acceptOperation(db, { operationId, entityType: 'order_delivered', entityId: orderId, toggle: true, deliveredBy });
  return { success: true, ...JSON.parse(event.payloadJson), revision: event.id, operationId: event.operationId };
}

export async function toggleBillPayment(db: D1Database, billId: number, operationId = crypto.randomUUID()) {
  const event = await acceptOperation(db, { operationId, entityType: 'bill_payment', entityId: billId, toggle: true });
  return { success: true, ...JSON.parse(event.payloadJson), revision: event.id, operationId: event.operationId };
}

export async function updateOrderNote(db: D1Database, orderId: number, note: string, operationId = crypto.randomUUID()) {
  const event = await acceptOperation(db, { operationId, entityType: 'order_note', entityId: orderId, value: note });
  return { success: true, ...JSON.parse(event.payloadJson), revision: event.id, operationId: event.operationId };
}

export async function getOperationalEvents(
  db: D1Database,
  since?: string | null
): Promise<OperationalEventDto[]> {
  let stmt: D1PreparedStatement;
  if (since && since.trim().length > 0) {
    stmt = db.prepare(`
      SELECT id, entity_type, entity_id, payload_json, created_at
      FROM cloud_operational_events
      WHERE created_at > ?
      ORDER BY id ASC
      LIMIT 500
    `).bind(since.trim());
  } else {
    stmt = db.prepare(`
      SELECT id, entity_type, entity_id, payload_json, created_at
      FROM cloud_operational_events
      ORDER BY id ASC
      LIMIT 500
    `);
  }

  const res = await stmt.all<any>();
  return (res.results ?? []).map((r: any) => ({
    id: r.id,
    entityType: r.entity_type,
    entityId: r.entity_id,
    payloadJson: r.payload_json,
    createdAt: r.created_at
  }));
}

export async function recordHeartbeat(db: D1Database, payload: HeartbeatPayload): Promise<void> {
  const now = new Date().toISOString();
  await db.prepare(`
    INSERT INTO cloud_heartbeats (device_id, lan_url, app_version, last_heartbeat_at)
    VALUES (?, ?, ?, ?)
    ON CONFLICT(device_id) DO UPDATE SET
      lan_url = excluded.lan_url,
      app_version = excluded.app_version,
      last_heartbeat_at = excluded.last_heartbeat_at
  `).bind(payload.deviceId || 'desktop-main', payload.lanUrl, payload.appVersion || null, now).run();
}

export async function getSystemStatus(db: D1Database): Promise<SystemStatusDto> {
  const row = await db.prepare(`
    SELECT * FROM cloud_heartbeats
    ORDER BY last_heartbeat_at DESC
    LIMIT 1
  `).first<any>();

  if (!row) {
    return {
      isDesktopOnline: false,
      lanUrl: null,
      appVersion: null,
      lastHeartbeatAt: null,
      secondsSinceHeartbeat: null
    };
  }

  const lastAt = new Date(row.last_heartbeat_at).getTime();
  const now = Date.now();
  const diffSeconds = Math.max(0, Math.floor((now - lastAt) / 1000));
  const isOnline = diffSeconds <= 90;

  return {
    isDesktopOnline: isOnline,
    lanUrl: row.lan_url,
    appVersion: row.app_version,
    lastHeartbeatAt: row.last_heartbeat_at,
    secondsSinceHeartbeat: diffSeconds
  };
}

export async function createPrintCommand(
  db: D1Database,
  orderId: number,
  billId: number | null,
  commandType: 'print_order_label' | 'print_bill_label',
  requestedBy?: string | null
): Promise<PrintCommandDto> {
  const now = new Date().toISOString();
  const res = await db.prepare(`
    INSERT INTO cloud_print_commands (order_id, bill_id, command_type, status, requested_by, created_at)
    VALUES (?, ?, ?, 'PENDING', ?, ?)
  `).bind(orderId, billId || null, commandType, requestedBy || null, now).run();

  const id = (res.meta as any)?.last_row_id ?? 0;
  return {
    id,
    orderId,
    billId,
    commandType,
    status: 'PENDING',
    requestedBy,
    createdAt: now
  };
}

export async function getPendingPrintCommands(db: D1Database): Promise<PrintCommandDto[]> {
  const res = await db.prepare(`
    SELECT * FROM cloud_print_commands
    WHERE status = 'PENDING'
    ORDER BY id ASC
  `).all<any>();

  return (res.results || []).map((r: any) => ({
    id: r.id,
    orderId: r.order_id,
    billId: r.bill_id,
    commandType: r.command_type,
    status: r.status,
    requestedBy: r.requested_by,
    createdAt: r.created_at,
    processedAt: r.processed_at,
    errorMessage: r.error_message
  }));
}

// A claim never expires automatically: a physical spooler effect can be ambiguous.
export async function claimPrintCommand(db: D1Database, id: number, token: string): Promise<boolean> {
  const result = await db.prepare(`UPDATE cloud_print_commands SET status='PROCESSING', claim_token=?
    WHERE id=? AND status='PENDING'`).bind(token, id).run();
  return Number(result.meta?.changes ?? 0) === 1;
}

export async function updatePrintCommandStatus(
  db: D1Database, id: number, status: 'COMPLETED' | 'FAILED',
  errorMessage: string | null | undefined, token: string
): Promise<boolean> {
  const result = await db.prepare(`UPDATE cloud_print_commands SET status=?, processed_at=?, error_message=?
    WHERE id=? AND claim_token=? AND (status='PROCESSING' OR status=?)`)
    .bind(status, new Date().toISOString(), errorMessage || null, id, token, status).run();
  return Number(result.meta?.changes ?? 0) === 1;
}
