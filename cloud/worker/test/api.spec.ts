import { createSqliteD1 } from './sqlite-d1';
import { describe, it, expect } from 'vitest';
import worker from '../src/index';
import { Env, SyncBatchPayload } from '../src/types';
import { createAuthToken } from '../src/auth';

function createMockBucket() {
  const map = new Map<string, Uint8Array>();
  return {
    async get(key: string) {
      const data = map.get(key);
      if (!data) return null;
      return {
        body: data,
        httpEtag: 'mock-etag-123',
        writeHttpMetadata(headers: Headers) {
          headers.set('Content-Type', 'image/jpeg');
        }
      };
    },
    async put(key: string, value: any) {
      map.set(key, new Uint8Array(value));
      return {};
    }
  } as unknown as R2Bucket;
}

function createMockD1() { return createSqliteD1().db; }

describe('Worker Auth and API', () => {
  const env: Env = {
    DB: createMockD1(),
    BUCKET: createMockBucket(),
    JWT_SECRET: 'test-secret-key-32-chars-length!!',
    SYNC_SECRET: 'sync-secret-12345',
    ADMIN_PIN: '123456',
    STAFF_PIN: '000000',
    WORKSHOP_NAME: 'XƯỞNG IN LALAB'
  };

  const ctx = {
    waitUntil: () => {},
    passThroughOnException: () => {}
  } as unknown as ExecutionContext;

  it('should authenticate Admin with valid PIN', async () => {
    const req = new Request('http://localhost:8787/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '123456' })
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(200);

    const body = await res.json() as any;
    expect(body.success).toBe(true);
    expect(body.role).toBe('Admin');
    expect(body.token).toBeDefined();
  });

  it('should authenticate Staff with valid PIN', async () => {
    const req = new Request('http://localhost:8787/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '000000' })
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(200);

    const body = await res.json() as any;
    expect(body.success).toBe(true);
    expect(body.role).toBe('Staff');
    expect(body.token).toBeDefined();
  });

  it('should reject invalid PIN with 401', async () => {
    const req = new Request('http://localhost:8787/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '999999' })
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(401);

    const body = await res.json() as any;
    expect(body.message).toContain('Mã PIN không chính xác');
  });

  it('should verify token with /api/auth/me', async () => {
    const token = await createAuthToken('Admin', env.JWT_SECRET);
    const req = new Request('http://localhost:8787/api/auth/me', {
      headers: { Authorization: `Bearer ${token}` }
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(200);

    const body = await res.json() as any;
    expect(body.role).toBe('Admin');
  });

  it('should reject unauthenticated access to /api/orders', async () => {
    const req = new Request('http://localhost:8787/api/orders?date=2026-10-02');
    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(401);
  });

  it('should block Staff from accessing financial report /api/reports/monthly', async () => {
    const token = await createAuthToken('Staff', env.JWT_SECRET);
    const req = new Request('http://localhost:8787/api/reports/monthly?month=10&year=2026', {
      headers: { Authorization: `Bearer ${token}` }
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(403);
    const body = await res.json() as any;
    expect(body.message).toContain('Chủ tiệm');
  });

  it('should reject sync batch with invalid sync secret', async () => {
    const req = new Request('http://localhost:8787/api/sync/batch', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': 'wrong-secret'
      },
      body: JSON.stringify({ orders: [] })
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(403);
  });

  it('should accept sync batch with valid secret and query back data', async () => {
    const payload: SyncBatchPayload = {
      orders: [
        {
          id: 101,
          orderCode: 'ORD-101',
          customerName: 'Studio ABC',
          folderName: 'Studio ABC',
          workDate: '2026-10-02',
          status: 'Ready',
          isPrinted: true,
          printedAt: '2026-10-02T10:00:00Z',
          isDelivered: false,
          deliveredAt: null,
          deliveredBy: null,
          note: null,
          hasIssues: false,
          isLocked: true,
          hasThumbnail: false,
          thumbnailFileName: null,
          customerBillId: 201,
          totalQuantity: 45,
          itemsJson: JSON.stringify([{ spec: '13x18 lụa', qty: 45 }])
        }
      ],
      bills: [
        {
          id: 201,
          billNumber: 'BILL-202610-001',
          billType: 'daily',
          customerId: 1,
          customerNameSnapshot: 'Studio ABC',
          periodDate: '2026-10-02',
          totalAmount: 450000,
          isPaid: false,
          paidAt: null,
          linesJson: JSON.stringify([
            { description: 'In ảnh 13x18 ép lụa', quantity: 45, unitPrice: 10000, lineTotal: 450000 }
          ]),
          adjustmentsJson: '[]',
          hasJpeg: false,
          lockedAt: '2026-10-02T10:00:00Z',
          exportedAt: '2026-10-02T10:00:00Z'
        }
      ],
      reports: [
        {
          key: 'month_2026_10',
          dataJson: JSON.stringify({
            year: 2026,
            month: 10,
            totalOrders: 1,
            totalQuantity: 45,
            totalRevenue: 450000,
            days: []
          })
        }
      ]
    };

    // 1. Sync
    const syncReq = new Request('http://localhost:8787/api/sync/batch', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': 'sync-secret-12345'
      },
      body: JSON.stringify(payload)
    });

    const syncRes = await worker.fetch(syncReq, env, ctx);
    expect(syncRes.status).toBe(200);
    const syncBody = await syncRes.json() as any;
    expect(syncBody.success).toBe(true);
    expect(syncBody.ordersCount).toBe(1);
    expect(syncBody.billsCount).toBe(1);

    // 2. Query Orders with Staff Token
    const staffToken = await createAuthToken('Staff', env.JWT_SECRET);
    const ordersReq = new Request('http://localhost:8787/api/orders?date=2026-10-02', {
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const ordersRes = await worker.fetch(ordersReq, env, ctx);
    expect(ordersRes.status).toBe(200);
    const ordersBody = await ordersRes.json() as any[];
    expect(ordersBody).toHaveLength(1);
    expect(ordersBody[0].customerName).toBe('Studio ABC');

    // 3. Query Bill
    const billReq = new Request('http://localhost:8787/api/bills/201', {
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const billRes = await worker.fetch(billReq, env, ctx);
    expect(billRes.status).toBe(200);
    const billBody = await billRes.json() as any;
    expect(billBody.billNumber).toBe('BILL-202610-001');

    // 4. Query Monthly Report with Admin Token
    const adminToken = await createAuthToken('Admin', env.JWT_SECRET);
    const reportReq = new Request('http://localhost:8787/api/reports/monthly?month=10&year=2026', {
      headers: { Authorization: `Bearer ${adminToken}` }
    });
    const reportRes = await worker.fetch(reportReq, env, ctx);
    expect(reportRes.status).toBe(200);
    const reportBody = await reportRes.json() as any;
    expect(reportBody.month).toBe(10);
    expect(reportBody.totalRevenue).toBe(450000);

    // 5. Test Operational Sync: Toggle Delivered
    const deliverReq = new Request('http://localhost:8787/api/orders/101/toggle-delivered', {
      method: 'POST',
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const deliverRes = await worker.fetch(deliverReq, env, ctx);
    expect(deliverRes.status).toBe(200);
    const deliverBody = await deliverRes.json() as any;
    expect(deliverBody.success).toBe(true);
    expect(deliverBody.isDelivered).toBe(true);

    // 6. Test Operational Sync: Update Note
    const noteReq = new Request('http://localhost:8787/api/orders/101/note', {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${staffToken}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ note: 'Khách yêu cầu giao trước 17h' })
    });
    const noteRes = await worker.fetch(noteReq, env, ctx);
    expect(noteRes.status).toBe(200);
    const noteBody = await noteRes.json() as any;
    expect(noteBody.success).toBe(true);
    expect(noteBody.note).toBe('Khách yêu cầu giao trước 17h');

    // 7. Test Operational Sync: Toggle Bill Payment
    const paymentReq = new Request('http://localhost:8787/api/bills/201/toggle-payment', {
      method: 'POST',
      headers: { Authorization: `Bearer ${adminToken}` }
    });
    const paymentRes = await worker.fetch(paymentReq, env, ctx);
    expect(paymentRes.status).toBe(200);
    const paymentBody = await paymentRes.json() as any;
    expect(paymentBody.success).toBe(true);
    expect(paymentBody.isPaid).toBe(true);

    // 8. Test Desktop Sync Pull
    const pullReq = new Request('http://localhost:8787/api/sync/pull', {
      headers: { 'X-Sync-Secret': env.SYNC_SECRET! }
    });
    const pullRes = await worker.fetch(pullReq, env, ctx);
    expect(pullRes.status).toBe(200);
    const pullBody = await pullRes.json() as any;
    expect(pullBody.success).toBe(true);
    expect(pullBody.events.length).toBeGreaterThanOrEqual(3);
    const types = pullBody.events.map((e: any) => e.entityType);
    expect(types).toContain('order_delivered');
    expect(types).toContain('order_note');
    expect(types).toContain('bill_payment');

    // 9. Test Thumbnail Endpoint
    await env.BUCKET!.put('thumbnails/101.jpg', new TextEncoder().encode('fake-jpeg-data'));
    const thumbReq = new Request('http://localhost:8787/api/orders/101/thumbnail', {
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const thumbRes = await worker.fetch(thumbReq, env, ctx);
    expect(thumbRes.status).toBe(200);
    expect(thumbRes.headers.get('Content-Type')).toBe('image/jpeg');

    // 10. Test Heartbeat & System Status
    const hbReq = new Request('http://localhost:8787/api/sync/heartbeat', {
      method: 'POST',
      headers: {
        'X-Sync-Secret': env.SYNC_SECRET!,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        deviceId: 'main-pc',
        lanUrl: 'http://192.168.1.15:5000',
        appVersion: '2.5.0'
      })
    });
    const hbRes = await worker.fetch(hbReq, env, ctx);
    expect(hbRes.status).toBe(200);

    const sysStatusReq = new Request('http://localhost:8787/api/system/status', {
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const sysStatusRes = await worker.fetch(sysStatusReq, env, ctx);
    expect(sysStatusRes.status).toBe(200);
    const sysStatus = await sysStatusRes.json() as any;
    expect(sysStatus.isDesktopOnline).toBe(true);
    expect(sysStatus.lanUrl).toBe('http://192.168.1.15:5000');

    // 11. Test Remote Print Command Queue
    const remotePrintReq = new Request('http://localhost:8787/api/orders/101/remote-print', {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${adminToken}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ billId: 201 })
    });
    const remotePrintRes = await worker.fetch(remotePrintReq, env, ctx);
    expect(remotePrintRes.status).toBe(200);
    const remotePrintBody = await remotePrintRes.json() as any;
    expect(remotePrintBody.success).toBe(true);
    expect(remotePrintBody.commandId).toBeDefined();

    // Desktop pulls print commands
    const pullCmdsReq = new Request('http://localhost:8787/api/sync/print-commands', {
      headers: { 'X-Sync-Secret': env.SYNC_SECRET! }
    });
    const pullCmdsRes = await worker.fetch(pullCmdsReq, env, ctx);
    expect(pullCmdsRes.status).toBe(200);
    const pullCmdsBody = await pullCmdsRes.json() as any;
    expect(pullCmdsBody.commands.length).toBe(1);
    expect(pullCmdsBody.commands[0].orderId).toBe(101);
    expect(pullCmdsBody.commands[0].status).toBe('PENDING');

    const claimToken = 'durable-owner-token-1';
    const claimRes = await worker.fetch(new Request(`http://localhost:8787/api/sync/print-commands/${remotePrintBody.commandId}/claim`, {
      method: 'POST', headers: { 'X-Sync-Secret': env.SYNC_SECRET!, 'Content-Type': 'application/json' },
      body: JSON.stringify({ claimToken })
    }), env, ctx);
    expect(claimRes.status).toBe(200);
    // Desktop updates print command status
    const updateCmdReq = new Request(`http://localhost:8787/api/sync/print-commands/${remotePrintBody.commandId}/status`, {
      method: 'POST',
      headers: {
        'X-Sync-Secret': env.SYNC_SECRET!,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ status: 'COMPLETED', claimToken })
    });
    const updateCmdRes = await worker.fetch(updateCmdReq, env, ctx);
    expect(updateCmdRes.status).toBe(200);

    // 12. Test Label SVG Endpoints
    // Staff accessing order label -> gets SVG package slip (no financial info)
    const staffOrderLabelReq = new Request('http://localhost:8787/api/orders/101/label', {
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const staffOrderLabelRes = await worker.fetch(staffOrderLabelReq, env, ctx);
    expect(staffOrderLabelRes.status).toBe(200);
    expect(staffOrderLabelRes.headers.get('Content-Type')).toContain('image/svg+xml');
    const staffSvg = await staffOrderLabelRes.text();
    expect(staffSvg).toContain('TEM ĐÓNG GÓI');
    expect(staffSvg).not.toContain('TIỀN THU HỘ (COD)');

    // Admin accessing order label with linked bill -> gets SVG delivery label with bill details
    const adminOrderLabelReq = new Request('http://localhost:8787/api/orders/101/label', {
      headers: { Authorization: `Bearer ${adminToken}` }
    });
    const adminOrderLabelRes = await worker.fetch(adminOrderLabelReq, env, ctx);
    expect(adminOrderLabelRes.status).toBe(200);
    expect(adminOrderLabelRes.headers.get('Content-Type')).toContain('image/svg+xml');
    const adminSvg = await adminOrderLabelRes.text();
    expect(adminSvg).toContain('TEM GIAO HÀNG');

    // Admin accessing bill label -> gets SVG delivery label
    const adminBillLabelReq = new Request('http://localhost:8787/api/bills/201/label', {
      headers: { Authorization: `Bearer ${adminToken}` }
    });
    const adminBillLabelRes = await worker.fetch(adminBillLabelReq, env, ctx);
    expect(adminBillLabelRes.status).toBe(200);
    expect(adminBillLabelRes.headers.get('Content-Type')).toContain('image/svg+xml');

    // Staff accessing bill label directly -> 403 Forbidden
    const staffBillLabelReq = new Request('http://localhost:8787/api/bills/201/label', {
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const staffBillLabelRes = await worker.fetch(staffBillLabelReq, env, ctx);
    expect(staffBillLabelRes.status).toBe(403);

    // 13. Test Bill Remote Print
    // Staff attempting bill remote print -> 403 Forbidden
    const staffBillPrintReq = new Request('http://localhost:8787/api/bills/201/remote-print', {
      method: 'POST',
      headers: { Authorization: `Bearer ${staffToken}` }
    });
    const staffBillPrintRes = await worker.fetch(staffBillPrintReq, env, ctx);
    expect(staffBillPrintRes.status).toBe(403);

    // Admin remote printing bill -> 200 OK, queues print_bill_label
    const adminBillPrintReq = new Request('http://localhost:8787/api/bills/201/remote-print', {
      method: 'POST',
      headers: { Authorization: `Bearer ${adminToken}` }
    });
    const adminBillPrintRes = await worker.fetch(adminBillPrintReq, env, ctx);
    expect(adminBillPrintRes.status).toBe(200);
    const billPrintData = await adminBillPrintRes.json() as any;
    expect(billPrintData.success).toBe(true);
    expect(billPrintData.commandId).toBeDefined();
  });
});
