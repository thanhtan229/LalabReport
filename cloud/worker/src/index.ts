import { acceptOperation, getEventPage, getReconciliationSnapshot, OperationError, OperationalMutation } from './operations';
import { toMobileBill } from './mobile-bill';
import { renderOrderLabelSvg, renderBillLabelSvg } from './label-svg';
import { Env, UserRole, SyncBatchPayload, HeartbeatPayload } from './types';
import { createAuthToken, verifyAuthToken, extractAuthToken, validSecret } from './auth';
import {
  getPendingGalleryJobs,
  claimGalleryJob,
  updateGalleryJobStatus,
  saveGalleryManifest,
  getGalleryManifest,
  recordGalleryMedia,
  getGalleryMedia,
  touchGalleryMedia,
  getGalleryPage,
  createOrGetGalleryRequestJob,
  createOrGetGalleryBatchJob,
  getGalleryJobById,
  cleanupExpiredCloudGalleryMedia,
  clearAllCloudGalleryMedia,
  deleteGalleryMedia,
  OrderGalleryManifest
} from './gallery';
import {
  getOrdersByDate,
  getOrderById,
  getBillById,
  getMonthlyReport,
  getUnpaidCustomers,
  upsertSyncBatch,
  toggleOrderDelivered,
  toggleBillPayment,
  updateOrderNote,
  getOperationalEvents,
  recordHeartbeat,
  getSystemStatus,
  createPrintCommand,
  getPendingPrintCommands,
  updatePrintCommandStatus,
  claimPrintCommand
} from './db';

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Sync-Secret, X-Operation-Id'
};

function jsonResponse(data: any, status: number = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      'Content-Type': 'application/json',
      ...CORS_HEADERS
    }
  });
}

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    // Handle CORS preflight
    if (request.method === 'OPTIONS') {
      return new Response(null, { headers: CORS_HEADERS });
    }

    // 1. Health check
    if (url.pathname === '/api/health') {
      let lastSync = 'Chưa đồng bộ';
      try {
        const meta = await env.DB.prepare("SELECT value FROM cloud_sync_meta WHERE key = 'last_synced_at'").first<{ value: string }>();
        if (meta?.value) lastSync = meta.value;
      } catch { }

      return jsonResponse({
        status: validSecret(env.JWT_SECRET) && validSecret(env.SYNC_SECRET) && !!env.ADMIN_PIN?.trim() ? 'ok' : 'configuration_required',
        configurationReady: validSecret(env.JWT_SECRET) && validSecret(env.SYNC_SECRET) && !!env.ADMIN_PIN?.trim(),
        appName: 'Lalab Cloud Worker 24/7',
        version: '1.0.0',
        workshopName: env.WORKSHOP_NAME ?? 'XƯỞNG IN ẢNH CHUYÊN NGHIỆP',
        lastSyncedAt: lastSync,
        time: new Date().toISOString()
      });
    }

    // 2. Authentication: Login with PIN
    if (url.pathname.startsWith('/api/auth/') && !validSecret(env.JWT_SECRET))
      return jsonResponse({ message: 'JWT_SECRET is not configured.' }, 503);

    if (url.pathname === '/api/auth/login' && request.method === 'POST') {
      if (!env.ADMIN_PIN?.trim() || (env.STAFF_PIN?.trim() && env.STAFF_PIN === env.ADMIN_PIN))
        return jsonResponse({ message: 'Configure distinct ADMIN_PIN and optional STAFF_PIN.' }, 503);
      try {
        const body = await request.json() as { pin?: string };
        const pin = (body?.pin || '').trim();

        const adminPin = env.ADMIN_PIN;
        const staffPin = env.STAFF_PIN;

        let role: UserRole | null = null;
        if (pin === adminPin) {
          role = 'Admin';
        } else if (pin === staffPin) {
          role = 'Staff';
        }

        if (!role) {
          return jsonResponse({ success: false, message: 'Mã PIN không chính xác.' }, 401);
        }

        const token = await createAuthToken(role, env.JWT_SECRET);
        return jsonResponse({
          success: true,
          role,
          token,
          workshopName: env.WORKSHOP_NAME ?? 'XƯỞNG IN ẢNH CHUYÊN NGHIỆP'
        });
      } catch (err: any) {
        return jsonResponse({ success: false, message: 'Lỗi xử lý yêu cầu: ' + err.message }, 400);
      }
    }

    // 3. Current User Info
    if (url.pathname === '/api/auth/me' && request.method === 'GET') {
      const token = extractAuthToken(request);
      if (!token) return jsonResponse({ message: 'Unauthorized' }, 401);

      const payload = await verifyAuthToken(token, env.JWT_SECRET);
      if (!payload) return jsonResponse({ message: 'Invalid or expired token' }, 401);

      return jsonResponse({
        authenticated: true,
        role: payload.role,
        workshopName: env.WORKSHOP_NAME ?? 'XƯỞNG IN ẢNH CHUYÊN NGHIỆP',
        workshopPhone: env.WORKSHOP_PHONE ?? '',
        workshopAddress: env.WORKSHOP_ADDRESS ?? ''
      });
    }

    // V2 negotiates an ID cursor before Desktop can push projections or operations.
    if (url.pathname.startsWith('/api/sync/v2/')) {
      if (!validSecret(env.SYNC_SECRET)) return jsonResponse({ message: 'SYNC_SECRET is not configured.' }, 503);
      if (request.headers.get('X-Sync-Secret') !== env.SYNC_SECRET) return jsonResponse({ message: 'Forbidden.' }, 403);
      try {
        if (url.pathname === '/api/sync/v2/reconciliation' && request.method === 'GET')
          return jsonResponse(await getReconciliationSnapshot(env.DB));
        if (url.pathname === '/api/sync/v2/pull' && request.method === 'GET') {
          const cursorText = url.searchParams.get('cursor') ?? '0';
          const limitText = url.searchParams.get('limit') ?? '500';
          if (!/^\d+$/.test(cursorText) || !/^\d+$/.test(limitText)) throw new OperationError('Invalid cursor or limit.');
          return jsonResponse(await getEventPage(env.DB, Number(cursorText), Number(limitText)));
        }
        if (url.pathname === '/api/sync/v2/operations' && request.method === 'POST') {
          const mutation = await request.json() as OperationalMutation;
          if (mutation.toggle) throw new OperationError('Desktop mutations must specify the final value.');
          return jsonResponse({ success: true, protocolVersion: 2, event: await acceptOperation(env.DB, mutation) });
        }
        return jsonResponse({ message: 'Unknown V2 endpoint.' }, 404);
      } catch (error: any) {
        return jsonResponse({ success: false, message: error.message }, error instanceof OperationError ? error.status : 500);
      }
    }

    // 4. Desktop Sync API: Batch Ingest
    if (url.pathname === '/api/sync/batch' && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret))
        return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);

      if (!syncSecret || syncSecret !== configuredSecret) {
        return jsonResponse({ success: false, message: 'Forbidden: Invalid sync secret.' }, 403);
      }

      try {
        const batch = await request.json() as SyncBatchPayload;
        const result = await upsertSyncBatch(env.DB, batch);

        return jsonResponse({
          success: true,
          message: 'Đồng bộ dữ liệu thành công.',
          ...result,
          syncedAt: new Date().toISOString()
        });
      } catch (err: any) {
        return jsonResponse({ success: false, message: 'Lỗi ghi cơ sở dữ liệu: ' + err.message }, 500);
      }
    }

    // 5. Desktop Sync API: Upload Media (Bill JPEG) to R2
    if (url.pathname.startsWith('/api/sync/media/') && request.method === 'PUT') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret))
        return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);

      if (!syncSecret || syncSecret !== configuredSecret) {
        return jsonResponse({ success: false, message: 'Forbidden: Invalid sync secret.' }, 403);
      }

      if (!env.BUCKET) {
        return jsonResponse({ success: false, message: 'R2 BUCKET binding is not configured.' }, 500);
      }

      const filename = url.pathname.replace('/api/sync/media/', '');
      if (!filename) return jsonResponse({ success: false, message: 'Filename required.' }, 400);

      const body = await request.arrayBuffer();
      await env.BUCKET.put(filename, body, {
        httpMetadata: {
          contentType: filename.endsWith('.png') ? 'image/png' : 'image/jpeg'
        }
      });

      return jsonResponse({ success: true, filename, uploadedAt: new Date().toISOString() });
    }

    // 5.1 Desktop Sync API: Pull Remote Operational Events
    if (url.pathname === '/api/sync/pull' && request.method === 'GET') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret))
        return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);

      if (!syncSecret || syncSecret !== configuredSecret) {
        return jsonResponse({ success: false, message: 'Forbidden: Invalid sync secret.' }, 403);
      }

      const since = url.searchParams.get('since');
      const events = await getOperationalEvents(env.DB, since);
      return jsonResponse({
        success: true,
        events,
        serverTime: new Date().toISOString()
      });
    }

    // 5.2 Desktop Sync API: Heartbeat
    if (url.pathname === '/api/sync/heartbeat' && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret))
        return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);

      if (!syncSecret || syncSecret !== configuredSecret) {
        return jsonResponse({ success: false, message: 'Forbidden: Invalid sync secret.' }, 403);
      }

      const body = await request.json() as HeartbeatPayload;
      if (!body?.lanUrl) {
        return jsonResponse({ success: false, message: 'lanUrl is required.' }, 400);
      }

      await recordHeartbeat(env.DB, body);
      return jsonResponse({ success: true, serverTime: new Date().toISOString() });
    }

    // 5.3 Desktop Sync API: Get Pending Print Commands
    if (url.pathname === '/api/sync/print-commands' && request.method === 'GET') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret))
        return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);

      if (!syncSecret || syncSecret !== configuredSecret) {
        return jsonResponse({ success: false, message: 'Forbidden: Invalid sync secret.' }, 403);
      }

      const commands = await getPendingPrintCommands(env.DB);
      return jsonResponse({ success: true, commands });
    }

    // 5.4 Desktop Sync API: Update Print Command Status
    const cmdStatusMatch = url.pathname.match(/^\/api\/sync\/print-commands\/(\d+)\/(status|claim)$/);
    if (cmdStatusMatch && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret))
        return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);

      if (!syncSecret || syncSecret !== configuredSecret) {
        return jsonResponse({ success: false, message: 'Forbidden: Invalid sync secret.' }, 403);
      }

      const cmdId = parseInt(cmdStatusMatch[1], 10);
      const body = await request.json() as { status?: string; errorMessage?: string; claimToken?: string };
      if (typeof body.claimToken !== 'string' || body.claimToken.length < 16 || body.claimToken.length > 128)
        return jsonResponse({ message: 'A durable claim token is required.' }, 400);
      if (cmdStatusMatch[2] === 'claim') {
        const claimed = await claimPrintCommand(env.DB, cmdId, body.claimToken);
        return jsonResponse({ success: claimed }, claimed ? 200 : 409);
      }
      if (body.status !== 'COMPLETED' && body.status !== 'FAILED')
        return jsonResponse({ message: 'Only terminal acknowledgements are accepted.' }, 400);
      const acknowledged = await updatePrintCommandStatus(env.DB, cmdId, body.status, body.errorMessage, body.claimToken);
      return jsonResponse({ success: acknowledged, commandId: cmdId }, acknowledged ? 200 : 409);
    }

    // 5.5 Desktop Sync API: Gallery Sync Endpoints
    if (url.pathname === '/api/sync/gallery/jobs' && request.method === 'GET') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret)) return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);
      if (!syncSecret || syncSecret !== configuredSecret) return jsonResponse({ success: false, message: 'Forbidden' }, 403);

      const jobs = await getPendingGalleryJobs(env.DB);
      return jsonResponse({ success: true, jobs });
    }

    const galleryClaimMatch = url.pathname.match(/^\/api\/sync\/gallery\/jobs\/([^/]+)\/claim$/);
    if (galleryClaimMatch && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret)) return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);
      if (!syncSecret || syncSecret !== configuredSecret) return jsonResponse({ success: false, message: 'Forbidden' }, 403);

      const jobId = galleryClaimMatch[1];
      const body = await request.json().catch(() => ({})) as { claimToken?: string };
      if (!body.claimToken) return jsonResponse({ message: 'claimToken is required.' }, 400);

      const claimed = await claimGalleryJob(env.DB, jobId, body.claimToken);
      return jsonResponse({ success: claimed }, claimed ? 200 : 409);
    }

    const galleryStatusMatch = url.pathname.match(/^\/api\/sync\/gallery\/jobs\/([^/]+)\/status$/);
    if (galleryStatusMatch && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret)) return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);
      if (!syncSecret || syncSecret !== configuredSecret) return jsonResponse({ success: false, message: 'Forbidden' }, 403);

      const jobId = galleryStatusMatch[1];
      const body = await request.json().catch(() => ({})) as { claimToken?: string; status?: string; errorMessage?: string };
      if (!body.claimToken || (body.status !== 'READY' && body.status !== 'FAILED')) {
        return jsonResponse({ message: 'claimToken and valid status (READY|FAILED) are required.' }, 400);
      }

      const acknowledged = await updateGalleryJobStatus(env.DB, jobId, body.claimToken, body.status as any, body.errorMessage);
      return jsonResponse({ success: acknowledged, jobId }, acknowledged ? 200 : 409);
    }

    if (url.pathname === '/api/sync/gallery/manifests' && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret)) return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);
      if (!syncSecret || syncSecret !== configuredSecret) return jsonResponse({ success: false, message: 'Forbidden' }, 403);

      const manifest = await request.json() as OrderGalleryManifest;
      if (!manifest || !manifest.orderId) return jsonResponse({ message: 'Invalid manifest' }, 400);

      await saveGalleryManifest(env.DB, manifest);
      return jsonResponse({ success: true, orderId: manifest.orderId });
    }

    const galleryUploadMatch = url.pathname.match(/^\/api\/sync\/gallery\/images\/([^/]+)$/);
    if (galleryUploadMatch && request.method === 'PUT') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret)) return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);
      if (!syncSecret || syncSecret !== configuredSecret) return jsonResponse({ success: false, message: 'Forbidden' }, 403);
      if (!env.BUCKET) return jsonResponse({ success: false, message: 'R2 BUCKET is not configured.' }, 500);

      const fileId = galleryUploadMatch[1];
      const version = url.searchParams.get('v') || '1';
      const r2Key = `gallery/${fileId}_${version}.jpg`;
      const body = await request.arrayBuffer();

      await env.BUCKET.put(r2Key, body, {
        httpMetadata: { contentType: 'image/jpeg' }
      });

      await recordGalleryMedia(env.DB, fileId, version, r2Key, body.byteLength);
      return jsonResponse({ success: true, fileId, version, r2Key });
    }

    if (url.pathname === '/api/sync/gallery/cleanup' && request.method === 'POST') {
      const syncSecret = request.headers.get('X-Sync-Secret') || extractAuthToken(request);
      const configuredSecret = env.SYNC_SECRET;
      if (!validSecret(configuredSecret)) return jsonResponse({ success: false, message: 'SYNC_SECRET is not configured.' }, 503);
      if (!syncSecret || syncSecret !== configuredSecret) return jsonResponse({ success: false, message: 'Forbidden' }, 403);

      const body = await request.json().catch(() => ({})) as { retentionDays?: number; clearAll?: boolean };
      if (body.clearAll) {
        const res = await clearAllCloudGalleryMedia(env.DB, env.BUCKET);
        return jsonResponse({ success: true, cleared: true, filesDeleted: res.deletedCount });
      } else {
        const res = await cleanupExpiredCloudGalleryMedia(env.DB, env.BUCKET, body.retentionDays);
        return jsonResponse({ success: true, filesDeleted: res.deletedCount, bytesFreed: res.bytesFreed });
      }
    }

    // --- PROTECTED MOBILE ROUTES ---
    const token = extractAuthToken(request);
    let currentUser = token ? await verifyAuthToken(token, env.JWT_SECRET) : null;

    // Helper auth guard
    const requireAuth = (): Response | null => {
      if (!validSecret(env.JWT_SECRET)) return jsonResponse({ message: "JWT_SECRET is not configured." }, 503);
      if (!currentUser) {
        return jsonResponse({ message: 'Vui lòng đăng nhập để tiếp tục.' }, 401);
      }
      return null;
    };

    // Helper admin guard
    const requireAdmin = (): Response | null => {
      const authErr = requireAuth();
      if (authErr) return authErr;
      if (currentUser?.role !== 'Admin') {
        return jsonResponse({ message: 'Quyền hạn bị từ chối: Chỉ Chủ tiệm mới có quyền xem dữ liệu này.' }, 403);
      }
      return null;
    };

    // 6. Orders: List by Date
    if (url.pathname === '/api/orders' && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const date = url.searchParams.get('date') || new Date().toISOString().split('T')[0];
      const orders = await getOrdersByDate(env.DB, date);
      return jsonResponse(orders);
    }

    // 7. Orders: Get Detail by ID
    const orderMatch = url.pathname.match(/^\/api\/orders\/(\d+)$/);
    if (orderMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderMatch[1], 10);
      const order = await getOrderById(env.DB, id);
      if (!order) return jsonResponse({ message: 'Không tìm thấy đơn hàng.' }, 404);

      return jsonResponse(order);
    }

    // 7.1 Orders: Toggle Delivered Status
    const orderDeliveredMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/toggle-delivered$/);
    if (orderDeliveredMatch && request.method === 'POST') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderDeliveredMatch[1], 10);
      try {
        const result = await toggleOrderDelivered(env.DB, id, currentUser?.role, request.headers.get('X-Operation-Id') ?? undefined);
        return jsonResponse(result);
      } catch (err: any) {
        return jsonResponse({ success: false, message: err.message }, err instanceof OperationError ? err.status : 400);
      }
    }

    // 7.2 Orders: Update Order Note
    const orderNoteMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/note$/);
    if (orderNoteMatch && request.method === 'POST') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderNoteMatch[1], 10);
      try {
        const body = await request.json() as { note?: string };
        const result = await updateOrderNote(env.DB, id, body?.note || '', request.headers.get('X-Operation-Id') ?? undefined);
        return jsonResponse(result);
      } catch (err: any) {
        return jsonResponse({ success: false, message: err.message }, err instanceof OperationError ? err.status : 400);
      }
    }

    // 7.3 Orders: Get Thumbnail Image from R2
    const orderThumbMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/thumbnail$/);
    if (orderThumbMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderThumbMatch[1], 10);
      const order = await getOrderById(env.DB, id);
      if (!order) return jsonResponse({ message: 'Không tìm thấy đơn hàng.' }, 404);

      if (!env.BUCKET) {
        return jsonResponse({ message: 'Bộ nhớ lưu trữ ảnh (R2) chưa được kích hoạt.' }, 500);
      }

      const objectKey = order.thumbnailKey || `thumbnails/${id}.jpg`;
      const object = await env.BUCKET.get(objectKey);
      if (!object) {
        return jsonResponse({ message: 'Chưa có ảnh thumbnail cho đơn hàng này.' }, 404);
      }

      const headers = new Headers();
      object.writeHttpMetadata(headers);
      headers.set('etag', object.httpEtag);
      headers.set('Cache-Control', 'public, max-age=604800, immutable');
      headers.set('Content-Type', 'image/jpeg');

      return new Response(object.body, { headers });
    }

    // 7.3 Orders: Get Label SVG
    const orderLabelMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/label$/);
    if (orderLabelMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderLabelMatch[1], 10);
      const order = await getOrderById(env.DB, id);
      if (!order) return jsonResponse({ message: 'Không tìm thấy đơn hàng.' }, 404);

      let svg: string;
      if (order.customerBillId && currentUser?.role === 'Admin') {
        const bill = await getBillById(env.DB, order.customerBillId);
        if (bill) {
          svg = renderBillLabelSvg(bill, env.WORKSHOP_NAME);
        } else {
          svg = renderOrderLabelSvg(order, env.WORKSHOP_NAME);
        }
      } else {
        svg = renderOrderLabelSvg(order, env.WORKSHOP_NAME);
      }

      return new Response(svg, {
        headers: {
          'Content-Type': 'image/svg+xml; charset=utf-8',
          'Cache-Control': 'no-cache',
          ...CORS_HEADERS
        }
      });
    }

    // 7.4 Orders: Send Remote Print Command
    const orderPrintMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/remote-print$/);
    if (orderPrintMatch && request.method === 'POST') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderPrintMatch[1], 10);
      const body = await request.json().catch(() => ({})) as { billId?: number };
      if (body.billId) { const err = requireAdmin(); if (err) return err; }
      const cmd = await createPrintCommand(
        env.DB,
        id,
        body?.billId || null,
        body?.billId ? 'print_bill_label' : 'print_order_label',
        currentUser?.role
      );

      return jsonResponse({
        success: true,
        message: 'Đã gửi lệnh in tem tới hàng đợi máy in xưởng.',
        commandId: cmd.id
      });
    }

    // 7.41 Orders Gallery: Request Gallery Manifest / Initial Batch
    const orderGalleryRequestMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/gallery\/requests$/);
    if (orderGalleryRequestMatch && request.method === 'POST') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderGalleryRequestMatch[1], 10);
      const order = await getOrderById(env.DB, id);
      if (!order) return jsonResponse({ message: 'Không tìm thấy đơn hàng.' }, 404);

      const manifest = await getGalleryManifest(env.DB, id);
      if (manifest) {
        return jsonResponse({ ready: true, manifest });
      }

      const { job } = await createOrGetGalleryRequestJob(env.DB, id);
      return jsonResponse({
        ready: false,
        pending: true,
        requestId: job.id,
        status: job.status,
        message: 'Đang gửi yêu cầu lập danh sách ảnh tới máy tính xưởng...'
      }, 202);
    }

    // 7.42 Orders Gallery: Get Page
    const orderGalleryPageMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/gallery$/);
    if (orderGalleryPageMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderGalleryPageMatch[1], 10);
      const cursor = url.searchParams.get('cursor');
      const rawLimit = parseInt(url.searchParams.get('limit') || '24', 10);
      const limit = Number.isNaN(rawLimit) ? 24 : Math.max(1, Math.min(50, rawLimit));

      const page = await getGalleryPage(env.DB, id, cursor, limit);
      if (!page) {
        return jsonResponse({ message: 'Chưa có danh sách ảnh cho đơn hàng này.' }, 404);
      }

      return jsonResponse(page);
    }

    // 7.43 Orders Gallery: Request Batch Images
    const orderGalleryBatchMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/gallery\/batches$/);
    if (orderGalleryBatchMatch && request.method === 'POST') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(orderGalleryBatchMatch[1], 10);
      const body = await request.json().catch(() => ({})) as { manifestVersion?: string; fileIds?: string[] };
      const fileIds = (body.fileIds || []).slice(0, 24);
      const manifestVersion = body.manifestVersion || '';

      const { job, allReady } = await createOrGetGalleryBatchJob(env.DB, id, manifestVersion, fileIds);
      if (allReady) {
        return jsonResponse({ ready: true, pendingCount: 0 });
      }
      if (!job) {
        return jsonResponse({ message: 'Chưa có danh sách ảnh cho đơn hàng này hoặc phiên bản không hợp lệ.' }, 404);
      }

      return jsonResponse({
        ready: false,
        pending: true,
        requestId: job.id,
        pendingCount: job.fileIds.length
      }, 202);
    }

    // 7.44 Orders Gallery: Get Request / Job Status
    const galleryJobStatusMatch = url.pathname.match(/^\/api\/gallery\/requests\/([^/]+)$/);
    if (galleryJobStatusMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const reqId = galleryJobStatusMatch[1];
      const job = await getGalleryJobById(env.DB, reqId);
      if (!job) return jsonResponse({ message: 'Không tìm thấy yêu cầu tải ảnh.' }, 404);

      // Strip internal claimToken before returning to mobile client
      const { claimToken, ...safeJob } = job;
      return jsonResponse(safeJob);
    }

    // 7.45 Orders Gallery: Serve Thumbnail Image from R2
    const orderGalleryImageMatch = url.pathname.match(/^\/api\/orders\/(\d+)\/gallery\/images\/([^/]+)$/);
    if (orderGalleryImageMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const orderId = parseInt(orderGalleryImageMatch[1], 10);
      const fileId = orderGalleryImageMatch[2];
      const version = url.searchParams.get('v') || undefined;

      // Validate scope against order manifest
      const manifest = await getGalleryManifest(env.DB, orderId);
      if (!manifest) return jsonResponse({ message: 'Không tìm thấy thư viện cho đơn hàng này.' }, 404);

      let found = false;
      for (const g of manifest.groups) {
        if (g.items.some(i => i.fileId === fileId)) {
          found = true;
          break;
        }
      }
      if (!found) return jsonResponse({ message: 'Ảnh không thuộc về đơn hàng này.' }, 404);

      if (!env.BUCKET) return jsonResponse({ message: 'R2 storage chưa được cấu hình.' }, 500);

      let media = await getGalleryMedia(env.DB, fileId, version);
      if (!media && version) {
        // Fall back to latest available version for self-heal UX
        media = await getGalleryMedia(env.DB, fileId);
      }
      if (!media) return jsonResponse({ message: 'Ảnh chưa sẵn sàng.' }, 404);

      const object = await env.BUCKET.get(media.r2Key);
      if (!object) {
        ctx.waitUntil(deleteGalleryMedia(env.DB, fileId, media.version));
        return jsonResponse({ message: 'File ảnh không tồn tại trên R2.' }, 404);
      }

      ctx.waitUntil(touchGalleryMedia(env.DB, [fileId]));

      const headers = new Headers();
      object.writeHttpMetadata(headers);
      headers.set('etag', object.httpEtag);
      headers.set('Cache-Control', 'private, max-age=86400');
      headers.set('Content-Type', 'image/jpeg');
      return new Response(object.body, { headers });
    }

    // 7.46 Orders Gallery: Touch Viewport Images
    if ((url.pathname.match(/^\/api\/orders\/\d+\/gallery\/touch$/) || url.pathname === '/api/gallery/touch') && request.method === 'POST') {
      const err = requireAuth();
      if (err) return err;

      const body = await request.json().catch(() => ({})) as { fileIds?: string[] };
      if (body.fileIds && body.fileIds.length > 0) {
        await touchGalleryMedia(env.DB, body.fileIds.slice(0, 100));
      }
      return jsonResponse({ success: true });
    }

    // 7.47 Admin Settings: Thumbnail Cleanup and Clear
    if (url.pathname === '/api/settings/thumbnails/cleanup' && request.method === 'POST') {
      const err = requireAdmin();
      if (err) return err;

      const body = await request.json().catch(() => ({})) as { retentionDays?: number };
      const res = await cleanupExpiredCloudGalleryMedia(env.DB, env.BUCKET, body.retentionDays);
      return jsonResponse({
        success: true,
        filesDeleted: res.deletedCount,
        bytesFreed: res.bytesFreed
      });
    }

    if (url.pathname === '/api/settings/thumbnails/clear' && request.method === 'POST') {
      const err = requireAdmin();
      if (err) return err;

      const res = await clearAllCloudGalleryMedia(env.DB, env.BUCKET);
      return jsonResponse({
        success: true,
        filesDeleted: res.deletedCount
      });
    }

    // 7.5 System: Get Machine Status
    if (url.pathname === '/api/system/status' && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const status = await getSystemStatus(env.DB);
      return jsonResponse(status);
    }

    // 8. Bills: Get Detail by ID
    const billMatch = url.pathname.match(/^\/api\/bills\/(\d+)$/);
    if (billMatch && request.method === 'GET') {
      const err = requireAuth();
      if (err) return err;

      const id = parseInt(billMatch[1], 10);
      const bill = await getBillById(env.DB, id);
      if (!bill) return jsonResponse({ message: 'Không tìm thấy hóa đơn.' }, 404);

      return jsonResponse(toMobileBill(bill, currentUser!.role));
    }

    // 8.1 Bills: Toggle Payment Status
    const billPaymentMatch = url.pathname.match(/^\/api\/bills\/(\d+)\/toggle-payment$/);
    if (billPaymentMatch && request.method === 'POST') {
      const err = requireAdmin();
      if (err) return err;

      const id = parseInt(billPaymentMatch[1], 10);
      try {
        const result = await toggleBillPayment(env.DB, id, request.headers.get('X-Operation-Id') ?? undefined);
        return jsonResponse(result);
      } catch (err: any) {
        return jsonResponse({ success: false, message: err.message }, err instanceof OperationError ? err.status : 400);
      }
    }

    // 8.2 Bills: Get Label SVG
    const billLabelMatch = url.pathname.match(/^\/api\/bills\/(\d+)\/label$/);
    if (billLabelMatch && request.method === 'GET') {
      const err = requireAdmin();
      if (err) return err;

      const id = parseInt(billLabelMatch[1], 10);
      const bill = await getBillById(env.DB, id);
      if (!bill) {
        const orderIdParam = parseInt(url.searchParams.get('orderId') || '0', 10);
        let fallbackOrder = orderIdParam > 0 ? await getOrderById(env.DB, orderIdParam) : null;
        if (!fallbackOrder) {
          const linked = await env.DB.prepare('SELECT id FROM cloud_orders WHERE customer_bill_id = ? LIMIT 1')
            .bind(id)
            .first<{ id: number }>();
          if (linked?.id) fallbackOrder = await getOrderById(env.DB, linked.id);
        }
        if (!fallbackOrder) return jsonResponse({ message: 'Không tìm thấy hóa đơn.' }, 404);

        const fallbackSvg = renderOrderLabelSvg(fallbackOrder, env.WORKSHOP_NAME);
        return new Response(fallbackSvg, {
          headers: {
            'Content-Type': 'image/svg+xml; charset=utf-8',
            'Cache-Control': 'no-cache',
            ...CORS_HEADERS
          }
        });
      }

      const svg = renderBillLabelSvg(bill, env.WORKSHOP_NAME);
      return new Response(svg, {
        headers: {
          'Content-Type': 'image/svg+xml; charset=utf-8',
          'Cache-Control': 'no-cache',
          ...CORS_HEADERS
        }
      });
    }

    // 8.3 Bills: Send Remote Print Command
    const billPrintMatch = url.pathname.match(/^\/api\/bills\/(\d+)\/remote-print$/);
    if (billPrintMatch && request.method === 'POST') {
      const err = requireAdmin();
      if (err) return err;

      const billId = parseInt(billPrintMatch[1], 10);
      const body = await request.json().catch(() => ({})) as { orderId?: number };
      const bill = await getBillById(env.DB, billId);

      let effectiveOrderId = body.orderId || 0;
      if (!effectiveOrderId) {
        const linkedOrder = await env.DB.prepare('SELECT id FROM cloud_orders WHERE customer_bill_id = ? LIMIT 1')
          .bind(billId)
          .first<{ id: number }>();
        if (linkedOrder?.id) effectiveOrderId = linkedOrder.id;
      }

      if (!bill && !effectiveOrderId) {
        return jsonResponse({ message: 'Không tìm thấy hóa đơn hoặc đơn hàng liên kết.' }, 404);
      }

      const cmd = await createPrintCommand(
        env.DB,
        effectiveOrderId,
        bill ? billId : null,
        bill ? 'print_bill_label' : 'print_order_label',
        currentUser?.role
      );

      return jsonResponse({
        success: true,
        message: bill
          ? 'Đã gửi lệnh in tem giao hàng tới máy in xưởng.'
          : 'Đã gửi lệnh in tem đóng gói đơn hàng tới máy in xưởng.',
        commandId: cmd.id
      });
    }

    // 9. Bills: Get Bill JPEG image from R2
    const billImageMatch = url.pathname.match(/^\/api\/bills\/(\d+)\/image$/);
    if (billImageMatch && request.method === 'GET') {
      const err = requireAdmin();
      if (err) return err;

      const id = parseInt(billImageMatch[1], 10);
      const bill = await getBillById(env.DB, id);
      if (!bill) return jsonResponse({ message: 'Không tìm thấy hóa đơn.' }, 404);

      if (!env.BUCKET) {
        return jsonResponse({ message: 'Bộ nhớ lưu trữ ảnh (R2) chưa được kích hoạt.' }, 404);
      }

      // Try bill_number or id
      const objectKey = `bills/${bill.billNumber}.jpg`;
      const object = await env.BUCKET.get(objectKey);
      if (!object) {
        return jsonResponse({ message: 'Chưa có ảnh hóa đơn được đồng bộ cho bill này.' }, 404);
      }

      const headers = new Headers();
      object.writeHttpMetadata(headers);
      headers.set('etag', object.httpEtag);
      headers.set('Cache-Control', 'private, no-store');
      headers.set('Content-Type', 'image/jpeg');

      return new Response(object.body, { headers });
    }

    // 10. Reports: Monthly Summary
    if (url.pathname === '/api/reports/monthly' && request.method === 'GET') {
      const err = requireAdmin();
      if (err) return err;

      const now = new Date();
      const year = parseInt(url.searchParams.get('year') || String(now.getFullYear()), 10);
      const month = parseInt(url.searchParams.get('month') || String(now.getMonth() + 1), 10);

      const report = await getMonthlyReport(env.DB, year, month);
      return jsonResponse(report);
    }

    // 11. Reports: Unpaid Customers (Công nợ)
    if (url.pathname === '/api/customers/unpaid' && request.method === 'GET') {
      const err = requireAdmin();
      if (err) return err;

      const debts = await getUnpaidCustomers(env.DB);
      return jsonResponse(debts);
    }

    // Fallback: If route starts with /api/, return 404 JSON
    if (url.pathname.startsWith('/api/')) {
      return jsonResponse({ message: 'Endpoint không tồn tại.' }, 404);
    }

    // 12. Fallback to Static Assets (Mobile SPA)
    if (env.ASSETS) {
      return env.ASSETS.fetch(request);
    }

    return new Response('Lalab Cloud Worker Active', { status: 200 });
  }
};
