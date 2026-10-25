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
        httpEtag: 'mock-etag-gallery-123',
        writeHttpMetadata(headers: Headers) {
          headers.set('Content-Type', 'image/jpeg');
        }
      };
    },
    async put(key: string, value: any) {
      map.set(key, new Uint8Array(value));
      return {};
    },
    async delete(key: string) {
      map.delete(key);
      return {};
    }
  } as unknown as R2Bucket;
}

describe('Cloud Order Gallery API and Lifecycle', () => {
  const env: Env = {
    DB: createSqliteD1().db,
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

  let adminToken: string;
  let staffToken: string;

  it('initializes tokens and order data', async () => {
    adminToken = await createAuthToken('Admin', env.JWT_SECRET);
    staffToken = await createAuthToken('Staff', env.JWT_SECRET);

    // Sync an order
    const batch: SyncBatchPayload = {
      orders: [
        {
          id: 101,
          orderCode: 'DH-101',
          customerName: 'Nguyễn Văn A',
          folderName: '101_NguyenVanA',
          workDate: '2026-10-25',
          status: 'CHO_IN',
          isPrinted: false,
          isDelivered: false,
          hasIssues: false,
          isLocked: false,
          hasThumbnail: false,
          totalQuantity: 10,
          itemsJson: JSON.stringify([
            { id: 1, specName: 'In Ảnh', variantName: '13x18', billQuantity: 10 }
          ])
        }
      ]
    };

    const syncReq = new Request('http://localhost:8787/api/sync/batch', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: JSON.stringify(batch)
    });

    const res = await worker.fetch(syncReq, env, ctx);
    expect(res.status).toBe(200);
  });

  it('POST /api/orders/:id/gallery/requests returns 202 and creates pending job when no manifest exists', async () => {
    const req = new Request('http://localhost:8787/api/orders/101/gallery/requests', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${staffToken}`
      }
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(202);

    const body = await res.json() as any;
    expect(body.ready).toBe(false);
    expect(body.pending).toBe(true);
    expect(body.requestId).toBeDefined();

    // Check that PC can see this job in /api/sync/gallery/jobs
    const pcPollReq = new Request('http://localhost:8787/api/sync/gallery/jobs', {
      method: 'GET',
      headers: { 'X-Sync-Secret': env.SYNC_SECRET! }
    });

    const pcRes = await worker.fetch(pcPollReq, env, ctx);
    expect(pcRes.status).toBe(200);
    const pcJobs = await pcRes.json() as any;
    expect(pcJobs.jobs.length).toBeGreaterThan(0);
    expect(pcJobs.jobs[0].orderId).toBe(101);
  });

  it('PC claims job, pushes manifest and updates job status', async () => {
    // 1. Get job
    const listReq = new Request('http://localhost:8787/api/sync/gallery/jobs', {
      method: 'GET',
      headers: { 'X-Sync-Secret': env.SYNC_SECRET! }
    });
    const listRes = await worker.fetch(listReq, env, ctx);
    const jobs = (await listRes.json() as any).jobs;
    const job = jobs[0];

    // 2. Claim job
    const claimToken = 'test-token-1234567890123456';
    const claimReq = new Request(`http://localhost:8787/api/sync/gallery/jobs/${job.id}/claim`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: JSON.stringify({ claimToken })
    });
    const claimRes = await worker.fetch(claimReq, env, ctx);
    expect(claimRes.status).toBe(200);
    expect((await claimRes.json() as any).success).toBe(true);

    // Verify mobile status check does NOT leak claimToken
    const leakCheckReq = new Request(`http://localhost:8787/api/gallery/requests/${job.id}`, {
      headers: { 'Authorization': `Bearer ${staffToken}` }
    });
    const leakCheckRes = await worker.fetch(leakCheckReq, env, ctx);
    expect(leakCheckRes.status).toBe(200);
    const statusBody = await leakCheckRes.json() as any;
    expect(statusBody.claimToken).toBeUndefined();
    expect(statusBody.status).toBe('CLAIMED');

    // 3. Push manifest
    const manifest = {
      orderId: 101,
      orderCode: 'DH-101',
      manifestVersion: 'v1_manifest',
      generatedAtUtc: new Date().toISOString(),
      totalImageCount: 2,
      hasUnresolvedFolders: false,
      groups: [
        {
          orderItemId: 1,
          specificationName: 'In Ảnh',
          folderName: '13x18',
          selectedPrintFolderRelativePath: '13x18',
          isFolderResolved: true,
          items: [
            {
              fileId: 'file_001',
              orderItemId: 1,
              specificationName: 'In Ảnh',
              fileName: 'IMG_001.jpg',
              fileSizeBytes: 2048,
              lastModifiedUtc: new Date().toISOString(),
              status: 'Ready',
              version: '20261025000000'
            },
            {
              fileId: 'file_002',
              orderItemId: 1,
              specificationName: 'In Ảnh',
              fileName: 'IMG_002.jpg',
              fileSizeBytes: 3072,
              lastModifiedUtc: new Date().toISOString(),
              status: 'Ready',
              version: '20261025000000'
            }
          ]
        }
      ]
    };

    const manifestReq = new Request('http://localhost:8787/api/sync/gallery/manifests', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: JSON.stringify(manifest)
    });
    const manifestRes = await worker.fetch(manifestReq, env, ctx);
    expect(manifestRes.status).toBe(200);

    // 4. Update job status to READY
    const statusReq = new Request(`http://localhost:8787/api/sync/gallery/jobs/${job.id}/status`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: JSON.stringify({ claimToken, status: 'READY' })
    });
    const statusRes = await worker.fetch(statusReq, env, ctx);
    expect(statusRes.status).toBe(200);
  });

  it('POST /api/orders/:id/gallery/requests returns 200 ready once manifest is saved', async () => {
    const req = new Request('http://localhost:8787/api/orders/101/gallery/requests', {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${staffToken}` }
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(200);
    const body = await res.json() as any;
    expect(body.ready).toBe(true);
    expect(body.manifest.manifestVersion).toBe('v1_manifest');
  });

  it('GET /api/orders/:id/gallery paginates items with Pending status before upload', async () => {
    const req = new Request('http://localhost:8787/api/orders/101/gallery?limit=1', {
      method: 'GET',
      headers: { 'Authorization': `Bearer ${staffToken}` }
    });

    const res = await worker.fetch(req, env, ctx);
    expect(res.status).toBe(200);
    const page = await res.json() as any;
    expect(page.items.length).toBe(1);
    expect(page.items[0].fileId).toBe('file_001');
    expect(page.items[0].status).toBe('Pending');
    expect(page.hasMore).toBe(true);
    expect(page.nextCursor).toBe('file_001');
  });

  it('PC uploads thumbnail to R2 and mobile can fetch image', async () => {
    // 1. Upload thumbnail for file_001
    const fakeJpeg = new Uint8Array([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]);
    const uploadReq = new Request('http://localhost:8787/api/sync/gallery/images/file_001?v=20261025000000', {
      method: 'PUT',
      headers: {
        'Content-Type': 'image/jpeg',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: fakeJpeg
    });

    const uploadRes = await worker.fetch(uploadReq, env, ctx);
    expect(uploadRes.status).toBe(200);
    const uploadBody = await uploadRes.json() as any;
    expect(uploadBody.success).toBe(true);

    // 2. Fetch page again: file_001 should now be Ready
    const pageReq = new Request('http://localhost:8787/api/orders/101/gallery', {
      headers: { 'Authorization': `Bearer ${staffToken}` }
    });
    const pageRes = await worker.fetch(pageReq, env, ctx);
    const page = await pageRes.json() as any;
    const item1 = page.items.find((x: any) => x.fileId === 'file_001');
    expect(item1.status).toBe('Ready');
    expect(item1.thumbnailUrl).toContain('/api/orders/101/gallery/images/file_001');

    // 3. Fetch image bytes from mobile endpoint
    const imgReq = new Request(`http://localhost:8787/api/orders/101/gallery/images/file_001?v=20261025000000`, {
      headers: { 'Authorization': `Bearer ${staffToken}` }
    });
    const imgRes = await worker.fetch(imgReq, env, ctx);
    expect(imgRes.status).toBe(200);
    expect(imgRes.headers.get('Content-Type')).toBe('image/jpeg');
    const bytes = new Uint8Array(await imgRes.arrayBuffer());
    expect(bytes[0]).toBe(0xFF);
    expect(bytes[1]).toBe(0xD8);
  });

  it('POST /api/orders/:id/gallery/batches returns 202 when images are missing and 200 when ready', async () => {
    // Request batch for file_002 which is missing
    const batchReq = new Request('http://localhost:8787/api/orders/101/gallery/batches', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${staffToken}`
      },
      body: JSON.stringify({
        manifestVersion: 'v1_manifest',
        fileIds: ['file_001', 'file_002']
      })
    });

    const batchRes = await worker.fetch(batchReq, env, ctx);
    expect(batchRes.status).toBe(202);
    const batchBody = await batchRes.json() as any;
    expect(batchBody.ready).toBe(false);
    expect(batchBody.pendingCount).toBe(1); // only file_002 is missing

    // Now upload file_002
    const fakeJpeg = new Uint8Array([0xFF, 0xD8, 0xFF, 0xDB]);
    await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/images/file_002?v=20261025000000', {
      method: 'PUT',
      headers: { 'Content-Type': 'image/jpeg', 'X-Sync-Secret': env.SYNC_SECRET! },
      body: fakeJpeg
    }), env, ctx);

    // Request batch again: should be 200 ready
    const batchReq2 = new Request('http://localhost:8787/api/orders/101/gallery/batches', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${staffToken}`
      },
      body: JSON.stringify({
        manifestVersion: 'v1_manifest',
        fileIds: ['file_001', 'file_002']
      })
    });
    const batchRes2 = await worker.fetch(batchReq2, env, ctx);
    expect(batchRes2.status).toBe(200);
    const batchBody2 = await batchRes2.json() as any;
    expect(batchBody2.ready).toBe(true);
    expect(batchBody2.pendingCount).toBe(0);
  });

  it('POST /api/gallery/touch touches viewport files', async () => {
    const touchReq = new Request('http://localhost:8787/api/gallery/touch', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${staffToken}`
      },
      body: JSON.stringify({ fileIds: ['file_001', 'file_002'] })
    });
    const touchRes = await worker.fetch(touchReq, env, ctx);
    expect(touchRes.status).toBe(200);
    expect((await touchRes.json() as any).success).toBe(true);
  });

  it('enforces Admin-only for thumbnail cleanup and clear', async () => {
    // Staff gets 403 Forbidden
    const staffCleanup = await worker.fetch(new Request('http://localhost:8787/api/settings/thumbnails/cleanup', {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${staffToken}` }
    }), env, ctx);
    expect(staffCleanup.status).toBe(403);

    const staffClear = await worker.fetch(new Request('http://localhost:8787/api/settings/thumbnails/clear', {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${staffToken}` }
    }), env, ctx);
    expect(staffClear.status).toBe(403);

    // Admin succeeds
    const adminCleanup = await worker.fetch(new Request('http://localhost:8787/api/settings/thumbnails/cleanup', {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${adminToken}` },
      body: JSON.stringify({ retentionDays: 60 })
    }), env, ctx);
    expect(adminCleanup.status).toBe(200);
    const cleanupBody = await adminCleanup.json() as any;
    expect(cleanupBody.success).toBe(true);

    const adminClear = await worker.fetch(new Request('http://localhost:8787/api/settings/thumbnails/clear', {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${adminToken}` }
    }), env, ctx);
    expect(adminClear.status).toBe(200);
    const clearBody = await adminClear.json() as any;
    expect(clearBody.success).toBe(true);
    expect(clearBody.filesDeleted).toBe(2);
  });

  it('re-triggers batch job when item version is updated in manifest', async () => {
    // 1. Upload media with version v1
    const uploadV1 = await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/images/file_001?v=v1', {
      method: 'PUT',
      headers: {
        'Content-Type': 'image/jpeg',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: new Uint8Array([1, 2, 3])
    }), env, ctx);
    expect(uploadV1.status).toBe(200);

    // 2. Save manifest with file_001 having version v2
    const manifestV2 = {
      orderId: 101,
      orderCode: 'DH-101',
      manifestVersion: 'man_v2',
      generatedAtUtc: new Date().toISOString(),
      totalImageCount: 1,
      hasUnresolvedFolders: false,
      groups: [
        {
          orderItemId: 1,
          specificationName: 'In Ảnh',
          folderName: 'print',
          selectedPrintFolderRelativePath: 'print',
          imageCount: 1,
          isFolderResolved: true,
          items: [
            {
              fileId: 'file_001',
              orderItemId: 1,
              specificationName: 'In Ảnh',
              fileName: 'photo1.jpg',
              fileSizeBytes: 2048,
              status: 'Ready',
              sourceRelativePath: 'print/photo1.jpg',
              version: 'v2'
            }
          ]
        }
      ]
    };

    const saveRes = await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/manifests', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: JSON.stringify(manifestV2)
    }), env, ctx);
    expect(saveRes.status).toBe(200);

    // 3. Request batch for file_001: must detect that v2 is not ready (even though v1 exists in D1)
    const batchRes = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery/batches', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${staffToken}`
      },
      body: JSON.stringify({ manifestVersion: 'man_v2', fileIds: ['file_001'] })
    }), env, ctx);

    expect(batchRes.status).toBe(202);
    const batchBody = await batchRes.json() as any;
    expect(batchBody.ready).toBe(false);
    expect(batchBody.pendingCount).toBe(1);
  });

  it('self-heals D1 record when R2 image is missing', async () => {
    // 1. Upload media file_003 with version v3
    const uploadRes = await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/images/file_003?v=v3', {
      method: 'PUT',
      headers: {
        'Content-Type': 'image/jpeg',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: new Uint8Array([7, 8, 9])
    }), env, ctx);
    expect(uploadRes.status).toBe(200);

    // 2. Post manifest with file_003
    const manifestV3 = {
      orderId: 101,
      orderCode: 'DH-101',
      manifestVersion: 'man_v3',
      generatedAtUtc: new Date().toISOString(),
      totalImageCount: 1,
      hasUnresolvedFolders: false,
      groups: [
        {
          orderItemId: 1,
          specificationName: 'In Ảnh',
          folderName: 'print',
          selectedPrintFolderRelativePath: 'print',
          imageCount: 1,
          isFolderResolved: true,
          items: [
            {
              fileId: 'file_003',
              orderItemId: 1,
              specificationName: 'In Ảnh',
              fileName: 'photo3.jpg',
              fileSizeBytes: 3000,
              status: 'Ready',
              sourceRelativePath: 'print/photo3.jpg',
              version: 'v3'
            }
          ]
        }
      ]
    };
    const saveManifestRes = await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/manifests', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Sync-Secret': env.SYNC_SECRET!
      },
      body: JSON.stringify(manifestV3)
    }), env, ctx);
    expect(saveManifestRes.status).toBe(200);

    // 3. Delete directly from R2 bucket to simulate missing object
    if (env.BUCKET) {
      await env.BUCKET.delete('gallery/file_003_v3.jpg');
    }

    // 4. Request image -> 404 and triggers self-healing delete in D1
    const imgRes = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery/images/file_003?v=v3', {
      headers: { 'Authorization': `Bearer ${staffToken}` }
    }), env, ctx);
    expect(imgRes.status).toBe(404);

    // 5. Subsequent batch request for file_003 finds it missing and creates a job
    const batchRes = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery/batches', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${staffToken}`
      },
      body: JSON.stringify({ manifestVersion: 'man_v3', fileIds: ['file_003'] })
    }), env, ctx);
    expect(batchRes.status).toBe(202);
    const batchBody = await batchRes.json() as any;
    expect(batchBody.ready).toBe(false);
    expect(batchBody.pendingCount).toBe(1);
  });

  it('resolves correct version when multiple versions exist for the same file_id in cloud_gallery_media', async () => {
    // FIX 1: Upload version v1 and version v2 for file_multi_ver
    const fakeImg = new Uint8Array([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
    await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/images/file_multi_ver?v=v1', {
      method: 'PUT',
      headers: { 'Content-Type': 'image/jpeg', 'X-Sync-Secret': env.SYNC_SECRET! },
      body: fakeImg
    }), env, ctx);

    await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/images/file_multi_ver?v=v2', {
      method: 'PUT',
      headers: { 'Content-Type': 'image/jpeg', 'X-Sync-Secret': env.SYNC_SECRET! },
      body: fakeImg
    }), env, ctx);

    // Save manifest requiring version v2
    const manifestMulti = {
      orderId: 101,
      orderCode: 'DH-101',
      manifestVersion: 'man_multi_v2',
      generatedAtUtc: new Date().toISOString(),
      totalImageCount: 1,
      hasUnresolvedFolders: false,
      groups: [
        {
          orderItemId: 1,
          specificationName: 'In Ảnh',
          folderName: 'print',
          selectedPrintFolderRelativePath: 'print',
          imageCount: 1,
          isFolderResolved: true,
          items: [
            {
              fileId: 'file_multi_ver',
              orderItemId: 1,
              specificationName: 'In Ảnh',
              fileName: 'photo_multi.jpg',
              fileSizeBytes: 2048,
              status: 'Ready',
              sourceRelativePath: 'print/photo_multi.jpg',
              version: 'v2'
            }
          ]
        }
      ]
    };

    await worker.fetch(new Request('http://localhost:8787/api/sync/gallery/manifests', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Sync-Secret': env.SYNC_SECRET! },
      body: JSON.stringify(manifestMulti)
    }), env, ctx);

    // Fetch gallery page: expect Ready with expected version v2's URL
    const pageRes = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery', {
      headers: { Authorization: `Bearer ${staffToken}` }
    }), env, ctx);
    expect(pageRes.status).toBe(200);
    const page = await pageRes.json() as any;
    const item = page.items.find((x: any) => x.fileId === 'file_multi_ver');
    expect(item).toBeDefined();
    expect(item.status).toBe('Ready');
    expect(item.thumbnailUrl).toBe('/api/orders/101/gallery/images/file_multi_ver?v=v2');
  });

  it('GET /api/orders/:id/gallery/images/:fileId falls back to latest version if requested version is missing', async () => {
    // FIX 2 (cloud worker part): file_multi_ver has v1 and v2. Request with v_missing
    const imgRes = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery/images/file_multi_ver?v=v_nonexistent', {
      headers: { Authorization: `Bearer ${staffToken}` }
    }), env, ctx);
    // Should fall back to latest available version and return 200 OK
    expect(imgRes.status).toBe(200);
    expect(imgRes.headers.get('Content-Type')).toBe('image/jpeg');

    // Request non-existent file -> 404
    const notFoundRes = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery/images/file_not_exist?v=v1', {
      headers: { Authorization: `Bearer ${staffToken}` }
    }), env, ctx);
    expect(notFoundRes.status).toBe(404);
  });

  it('clamps gallery page limit to 1..50 with NaN fallback 24', async () => {
    // FIX 3: test limit clamping
    // 1. limit=100 clamped to 50
    const res100 = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery?limit=100', {
      headers: { Authorization: `Bearer ${staffToken}` }
    }), env, ctx);
    expect(res100.status).toBe(200);
    const page100 = await res100.json() as any;
    expect(page100.items.length).toBeLessThanOrEqual(50);

    // 2. limit=0 or negative clamped to 1
    const resZero = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery?limit=0', {
      headers: { Authorization: `Bearer ${staffToken}` }
    }), env, ctx);
    expect(resZero.status).toBe(200);
    const pageZero = await resZero.json() as any;
    expect(pageZero.items.length).toBe(1);

    // 3. limit=abc fallback to 24
    const resNan = await worker.fetch(new Request('http://localhost:8787/api/orders/101/gallery?limit=invalid_nan', {
      headers: { Authorization: `Bearer ${staffToken}` }
    }), env, ctx);
    expect(resNan.status).toBe(200);
  });

  it('FIX 4: synced bill includes phoneSnapshot, shippingAddressSnapshot, and note in label SVG', async () => {
    const batch: SyncBatchPayload = {
      bills: [
        {
          id: 501,
          billNumber: 'HD-LABEL-501',
          billType: 'daily',
          customerId: 1,
          customerNameSnapshot: 'Chị Mai Hoa',
          phoneSnapshot: '0901234567',
          shippingAddressSnapshot: '456 Phố Huế, Hà Nội',
          note: 'Giao giờ hành chính',
          periodDate: '2026-10-25',
          totalAmount: 150000,
          isPaid: true,
          linesJson: JSON.stringify([{ description: 'Ảnh ép gỗ', quantity: 5, unitPrice: 30000, lineTotal: 150000 }]),
          adjustmentsJson: '[]',
          hasJpeg: false
        }
      ]
    };

    const syncReq = new Request('http://localhost:8787/api/sync/batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Sync-Secret': env.SYNC_SECRET! },
      body: JSON.stringify(batch)
    });
    const syncRes = await worker.fetch(syncReq, env, ctx);
    expect(syncRes.status).toBe(200);

    const labelReq = new Request('http://localhost:8787/api/bills/501/label', {
      headers: { Authorization: `Bearer ${adminToken}` }
    });
    const labelRes = await worker.fetch(labelReq, env, ctx);
    expect(labelRes.status).toBe(200);
    const svg = await labelRes.text();
    expect(svg).toContain('0901234567');
    expect(svg).toContain('456 Phố Huế, Hà Nội');
    expect(svg).toContain('Giao giờ hành chính');
  });

});
