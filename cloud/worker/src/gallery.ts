import { Env } from './types';

export interface OrderGalleryItem {
  fileId: string;
  orderItemId: number;
  specificationName: string;
  fileName: string;
  fileSizeBytes: number;
  lastModifiedUtc: string;
  status: string;
  version: string;
  sourceRelativePath?: string;
}

export interface OrderGalleryGroup {
  orderItemId: number;
  specificationName: string;
  folderName: string;
  selectedPrintFolderRelativePath?: string | null;
  isFolderResolved: boolean;
  resolutionMessage?: string | null;
  imageCount?: number;
  items: OrderGalleryItem[];
}

export interface OrderGalleryManifest {
  orderId: number;
  orderCode: string;
  manifestVersion: string;
  generatedAtUtc: string;
  totalImageCount: number;
  hasUnresolvedFolders: boolean;
  notice?: string | null;
  groups: OrderGalleryGroup[];
}

export interface OrderGalleryItemDto {
  fileId: string;
  orderItemId: number;
  specificationName: string;
  fileName: string;
  fileSizeBytes: number;
  status: 'Ready' | 'Pending' | 'Failed' | 'Missing';
  version: string;
  thumbnailUrl?: string | null;
}

export interface OrderGalleryPage {
  orderId: number;
  manifestVersion: string;
  items: OrderGalleryItemDto[];
  nextCursor?: string | null;
  hasMore: boolean;
  totalItems: number;
}

export interface GalleryJob {
  id: string;
  orderId: number;
  manifestVersion: string;
  batchIndex: number;
  fileIds: string[];
  status: 'PENDING' | 'CLAIMED' | 'READY' | 'FAILED' | 'EXPIRED';
  claimToken?: string | null;
  claimedAt?: string | null;
  expiresAt: string;
  createdAt: string;
  updatedAt: string;
  errorMessage?: string | null;
}

export interface GalleryMedia {
  fileId: string;
  version: string;
  r2Key: string;
  byteSize: number;
  createdAt: string;
  lastUsedAt: string;
}

const MANIFEST_TTL_HOURS = 24;
const JOB_TTL_SECONDS = 120; // 2 minutes lease/timeout

export async function getGalleryManifest(db: D1Database, orderId: number): Promise<OrderGalleryManifest | null> {
  const row = await db.prepare(`
    SELECT manifest_json, expires_at FROM cloud_gallery_manifests
    WHERE order_id = ?
  `).bind(orderId).first<{ manifest_json: string; expires_at: string }>();

  if (!row) return null;
  // If expired, return null so a fresh manifest can be requested
  if (new Date(row.expires_at).getTime() < Date.now()) {
    return null;
  }

  try {
    return JSON.parse(row.manifest_json) as OrderGalleryManifest;
  } catch {
    return null;
  }
}

export async function saveGalleryManifest(db: D1Database, manifest: OrderGalleryManifest): Promise<void> {
  const now = new Date();
  const expiresAt = new Date(now.getTime() + MANIFEST_TTL_HOURS * 3600 * 1000).toISOString();
  const manifestJson = JSON.stringify(manifest);

  await db.prepare(`
    INSERT INTO cloud_gallery_manifests (order_id, manifest_version, manifest_json, total_items, created_at, expires_at)
    VALUES (?, ?, ?, ?, ?, ?)
    ON CONFLICT(order_id) DO UPDATE SET
      manifest_version = excluded.manifest_version,
      manifest_json = excluded.manifest_json,
      total_items = excluded.total_items,
      created_at = excluded.created_at,
      expires_at = excluded.expires_at
  `).bind(
    manifest.orderId,
    manifest.manifestVersion,
    manifestJson,
    manifest.totalImageCount || 0,
    now.toISOString(),
    expiresAt
  ).run();
}

export async function createOrGetGalleryRequestJob(
  db: D1Database,
  orderId: number
): Promise<{ job: GalleryJob; isNew: boolean }> {
  const now = new Date();
  const nowIso = now.toISOString();

  // Check if an unexpired pending/claimed manifest job already exists for this order
  const existing = await db.prepare(`
    SELECT * FROM cloud_gallery_jobs
    WHERE order_id = ? AND manifest_version = '' AND status IN ('PENDING', 'CLAIMED') AND expires_at > ?
    ORDER BY created_at DESC LIMIT 1
  `).bind(orderId, nowIso).first<any>();

  if (existing) {
    return { job: mapJobRow(existing), isNew: false };
  }

  const jobId = crypto.randomUUID();
  const expiresAt = new Date(now.getTime() + JOB_TTL_SECONDS * 1000).toISOString();

  await db.prepare(`
    INSERT INTO cloud_gallery_jobs (id, order_id, manifest_version, batch_index, file_ids_json, status, expires_at, created_at, updated_at)
    VALUES (?, ?, '', 0, '[]', 'PENDING', ?, ?, ?)
  `).bind(jobId, orderId, expiresAt, nowIso, nowIso).run();

  return {
    job: {
      id: jobId,
      orderId,
      manifestVersion: '',
      batchIndex: 0,
      fileIds: [],
      status: 'PENDING',
      expiresAt,
      createdAt: nowIso,
      updatedAt: nowIso
    },
    isNew: true
  };
}

export async function createOrGetGalleryBatchJob(
  db: D1Database,
  orderId: number,
  manifestVersion: string,
  fileIds: string[]
): Promise<{ job: GalleryJob | null; allReady: boolean }> {
  if (fileIds.length === 0) {
    return { job: null, allReady: true };
  }

  // Validate that requested fileIds actually belong to this order manifest
  const manifest = await getGalleryManifest(db, orderId);
  if (!manifest) {
    return { job: null, allReady: false };
  }

  const validFileIdSet = new Set<string>();
  const itemVersionMap = new Map<string, string>();
  for (const g of manifest.groups) {
    for (const item of g.items) {
      validFileIdSet.add(item.fileId);
      if (item.version) {
        itemVersionMap.set(item.fileId, item.version);
      }
    }
  }

  const targetFileIds = fileIds.filter(fid => validFileIdSet.has(fid));
  if (targetFileIds.length === 0) {
    return { job: null, allReady: true };
  }

  // Check which fileIds already exist in cloud_gallery_media with matching version
  const placeholders = targetFileIds.map(() => '?').join(',');
  const readyRows = await db.prepare(`
    SELECT file_id, version FROM cloud_gallery_media
    WHERE file_id IN (${placeholders})
  `).bind(...targetFileIds).all<{ file_id: string; version: string }>();

  const readySet = new Set(
    (readyRows.results || [])
      .filter(r => {
        const expectedVersion = itemVersionMap.get(r.file_id);
        return !expectedVersion || r.version === expectedVersion;
      })
      .map(r => r.file_id)
  );
  const missingFileIds = targetFileIds.filter(fid => !readySet.has(fid));

  if (missingFileIds.length === 0) {
    return { job: null, allReady: true };
  }

  const now = new Date();
  const nowIso = now.toISOString();

  // Check if an existing pending/claimed job covers these missing files
  const existingRows = await db.prepare(`
    SELECT * FROM cloud_gallery_jobs
    WHERE order_id = ? AND manifest_version = ? AND status IN ('PENDING', 'CLAIMED') AND expires_at > ?
  `).bind(orderId, manifestVersion, nowIso).all<any>();

  for (const row of existingRows.results || []) {
    const job = mapJobRow(row);
    const jobFiles = new Set(job.fileIds);
    if (missingFileIds.every(id => jobFiles.has(id))) {
      return { job, allReady: false };
    }
  }

  const jobId = crypto.randomUUID();
  const expiresAt = new Date(now.getTime() + JOB_TTL_SECONDS * 1000).toISOString();
  const fileIdsJson = JSON.stringify(missingFileIds);

  await db.prepare(`
    INSERT INTO cloud_gallery_jobs (id, order_id, manifest_version, batch_index, file_ids_json, status, expires_at, created_at, updated_at)
    VALUES (?, ?, ?, 0, ?, 'PENDING', ?, ?, ?)
  `).bind(jobId, orderId, manifestVersion, fileIdsJson, expiresAt, nowIso, nowIso).run();

  return {
    job: {
      id: jobId,
      orderId,
      manifestVersion,
      batchIndex: 0,
      fileIds: missingFileIds,
      status: 'PENDING',
      expiresAt,
      createdAt: nowIso,
      updatedAt: nowIso
    },
    allReady: false
  };
}

export async function getGalleryJobById(db: D1Database, jobId: string): Promise<GalleryJob | null> {
  const row = await db.prepare('SELECT * FROM cloud_gallery_jobs WHERE id = ?').bind(jobId).first<any>();
  if (!row) return null;
  return mapJobRow(row);
}

export async function getPendingGalleryJobs(db: D1Database): Promise<GalleryJob[]> {
  const nowIso = new Date().toISOString();
  // Fetch pending jobs or expired claimed jobs
  const rows = await db.prepare(`
    SELECT * FROM cloud_gallery_jobs
    WHERE (status = 'PENDING' OR (status = 'CLAIMED' AND expires_at <= ?))
      AND expires_at > datetime('now', '-5 minutes')
    ORDER BY created_at ASC LIMIT 10
  `).bind(nowIso).all<any>();

  return (rows.results || []).map(mapJobRow);
}

export async function claimGalleryJob(
  db: D1Database,
  jobId: string,
  claimToken: string,
  leaseDurationSeconds: number = 120
): Promise<boolean> {
  const now = new Date();
  const nowIso = now.toISOString();
  const expiresAt = new Date(now.getTime() + leaseDurationSeconds * 1000).toISOString();

  const res = await db.prepare(`
    UPDATE cloud_gallery_jobs
    SET status = 'CLAIMED',
        claim_token = ?,
        claimed_at = ?,
        expires_at = ?,
        updated_at = ?
    WHERE id = ? AND (status = 'PENDING' OR (status = 'CLAIMED' AND expires_at <= ?))
  `).bind(claimToken, nowIso, expiresAt, nowIso, jobId, nowIso).run();

  return Number(res.meta?.changes ?? 0) === 1;
}

export async function updateGalleryJobStatus(
  db: D1Database,
  jobId: string,
  claimToken: string,
  status: 'READY' | 'FAILED',
  errorMessage?: string | null
): Promise<boolean> {
  const nowIso = new Date().toISOString();
  const res = await db.prepare(`
    UPDATE cloud_gallery_jobs
    SET status = ?,
        updated_at = ?,
        error_message = ?
    WHERE id = ? AND claim_token = ?
  `).bind(status, nowIso, errorMessage || null, jobId, claimToken).run();

  return Number(res.meta?.changes ?? 0) === 1;
}

export async function recordGalleryMedia(
  db: D1Database,
  fileId: string,
  version: string,
  r2Key: string,
  byteSize: number
): Promise<void> {
  const nowIso = new Date().toISOString();
  await db.prepare(`
    INSERT INTO cloud_gallery_media (file_id, version, r2_key, byte_size, created_at, last_used_at)
    VALUES (?, ?, ?, ?, ?, ?)
    ON CONFLICT(file_id, version) DO UPDATE SET
      r2_key = excluded.r2_key,
      byte_size = excluded.byte_size,
      last_used_at = excluded.last_used_at
  `).bind(fileId, version, r2Key, byteSize, nowIso, nowIso).run();
}

export async function getGalleryMedia(
  db: D1Database,
  fileId: string,
  version?: string
): Promise<GalleryMedia | null> {
  let row: any;
  if (version) {
    row = await db.prepare(`
      SELECT * FROM cloud_gallery_media
      WHERE file_id = ? AND version = ?
    `).bind(fileId, version).first<any>();
  } else {
    row = await db.prepare(`
      SELECT * FROM cloud_gallery_media
      WHERE file_id = ?
      ORDER BY created_at DESC LIMIT 1
    `).bind(fileId).first<any>();
  }

  if (!row) return null;
  return {
    fileId: row.file_id,
    version: row.version,
    r2Key: row.r2_key,
    byteSize: row.byte_size,
    createdAt: row.created_at,
    lastUsedAt: row.last_used_at
  };
}

export async function deleteGalleryMedia(
  db: D1Database,
  fileId: string,
  version?: string
): Promise<boolean> {
  let res: any;
  if (version) {
    res = await db.prepare(`
      DELETE FROM cloud_gallery_media
      WHERE file_id = ? AND version = ?
    `).bind(fileId, version).run();
  } else {
    res = await db.prepare(`
      DELETE FROM cloud_gallery_media
      WHERE file_id = ?
    `).bind(fileId).run();
  }
  return Number(res.meta?.changes ?? 0) > 0;
}

export async function touchGalleryMedia(db: D1Database, fileIds: string[]): Promise<void> {
  if (fileIds.length === 0) return;
  const nowIso = new Date().toISOString();
  const chunkSize = 50;
  for (let i = 0; i < fileIds.length; i += chunkSize) {
    const chunk = fileIds.slice(i, i + chunkSize);
    const placeholders = chunk.map(() => '?').join(',');
    await db.prepare(`
      UPDATE cloud_gallery_media
      SET last_used_at = ?
      WHERE file_id IN (${placeholders})
    `).bind(nowIso, ...chunk).run();
  }
}

export async function getGalleryPage(
  db: D1Database,
  orderId: number,
  cursor?: string | null,
  limit: number = 24
): Promise<OrderGalleryPage | null> {
  const manifest = await getGalleryManifest(db, orderId);
  if (!manifest) return null;

  // Flatten all items across groups
  const allItems: OrderGalleryItem[] = [];
  for (const g of manifest.groups) {
    for (const it of g.items) {
      allItems.push(it);
    }
  }

  // Find start index from cursor
  let startIndex = 0;
  if (cursor) {
    const idx = allItems.findIndex(x => x.fileId === cursor);
    if (idx >= 0) {
      startIndex = idx + 1;
    }
  }

  const pagedItems = allItems.slice(startIndex, startIndex + limit);
  const pagedFileIds = pagedItems.map(p => p.fileId);

  // Check ready media in D1
  const exactMediaMap = new Map<string, GalleryMedia>();
  const latestMediaMap = new Map<string, GalleryMedia>();
  if (pagedFileIds.length > 0) {
    const uniqueFileIds = Array.from(new Set(pagedFileIds));
    const chunkSize = 50;
    for (let i = 0; i < uniqueFileIds.length; i += chunkSize) {
      const chunk = uniqueFileIds.slice(i, i + chunkSize);
      const placeholders = chunk.map(() => '?').join(',');
      const rows = await db.prepare(`
        SELECT * FROM cloud_gallery_media
        WHERE file_id IN (${placeholders})
        ORDER BY created_at DESC
      `).bind(...chunk).all<any>();

      for (const r of rows.results || []) {
        const media: GalleryMedia = {
          fileId: r.file_id,
          version: r.version,
          r2Key: r.r2_key,
          byteSize: r.byte_size,
          createdAt: r.created_at,
          lastUsedAt: r.last_used_at
        };
        exactMediaMap.set(`${r.file_id}:${r.version}`, media);
        if (!latestMediaMap.has(r.file_id)) {
          latestMediaMap.set(r.file_id, media);
        }
      }
    }
  }

  const dtos: OrderGalleryItemDto[] = pagedItems.map(it => {
    const media = it.version ? exactMediaMap.get(`${it.fileId}:${it.version}`) : latestMediaMap.get(it.fileId);
    const isReady = !!media;
    const versionToUse = media?.version || it.version;
    return {
      fileId: it.fileId,
      orderItemId: it.orderItemId,
      specificationName: it.specificationName,
      fileName: it.fileName,
      fileSizeBytes: it.fileSizeBytes,
      status: isReady ? 'Ready' : 'Pending',
      version: it.version,
      thumbnailUrl: isReady
        ? `/api/orders/${orderId}/gallery/images/${it.fileId}${versionToUse ? `?v=${encodeURIComponent(versionToUse)}` : ''}`
        : null
    };
  });

  const hasMore = startIndex + limit < allItems.length;
  const nextCursor = hasMore && pagedItems.length > 0 ? pagedItems[pagedItems.length - 1].fileId : null;

  return {
    orderId,
    manifestVersion: manifest.manifestVersion,
    items: dtos,
    nextCursor,
    hasMore,
    totalItems: allItems.length
  };
}

export async function cleanupExpiredCloudGalleryMedia(
  db: D1Database,
  bucket: R2Bucket | undefined,
  retentionDays: number = 60
): Promise<{ deletedCount: number; bytesFreed: number }> {
  const safeDays = (retentionDays === 30 || retentionDays === 90) ? retentionDays : 60;
  const cutoff = new Date(Date.now() - safeDays * 86400 * 1000).toISOString();

  // Find up to 100 expired media entries
  const rows = await db.prepare(`
    SELECT file_id, version, r2_key, byte_size FROM cloud_gallery_media
    WHERE last_used_at < ?
    LIMIT 100
  `).bind(cutoff).all<any>();

  let deletedCount = 0;
  let bytesFreed = 0;

  if (rows.results && rows.results.length > 0) {
    for (const row of rows.results) {
      if (bucket && row.r2_key) {
        try {
          await bucket.delete(row.r2_key);
        } catch { }
      }
      deletedCount++;
      bytesFreed += (row.byte_size || 0);
    }

    // Chunk deletes to <= 25 pairs (50 variables)
    const chunkSize = 25;
    for (let i = 0; i < rows.results.length; i += chunkSize) {
      const chunk = rows.results.slice(i, i + chunkSize);
      const placeholders = chunk.map(() => '(?, ?)').join(',');
      const flatArgs: string[] = [];
      for (const r of chunk) {
        flatArgs.push(r.file_id, r.version);
      }
      await db.prepare(`
        DELETE FROM cloud_gallery_media
        WHERE (file_id, version) IN (${placeholders})
      `).bind(...flatArgs).run();
    }
  }

  return { deletedCount, bytesFreed };
}

export async function clearAllCloudGalleryMedia(
  db: D1Database,
  bucket: R2Bucket | undefined
): Promise<{ deletedCount: number }> {
  let totalDeleted = 0;
  // Delete in batches of 100
  while (true) {
    const rows = await db.prepare(`
      SELECT file_id, version, r2_key FROM cloud_gallery_media
      LIMIT 100
    `).all<any>();

    if (!rows.results || rows.results.length === 0) break;

    for (const r of rows.results) {
      if (bucket && r.r2_key) {
        try {
          await bucket.delete(r.r2_key);
        } catch { }
      }
      totalDeleted++;
    }

    // Chunk into batches of at most 25 pairs (50 parameters) to respect D1 variable limit
    const chunkSize = 25;
    for (let i = 0; i < rows.results.length; i += chunkSize) {
      const chunk = rows.results.slice(i, i + chunkSize);
      const placeholders = chunk.map(() => '(?, ?)').join(',');
      const flatArgs: string[] = [];
      for (const r of chunk) {
        flatArgs.push(r.file_id, r.version);
      }
      await db.prepare(`
        DELETE FROM cloud_gallery_media
        WHERE (file_id, version) IN (${placeholders})
      `).bind(...flatArgs).run();
    }
  }

  // Also clear manifests and jobs
  await db.prepare('DELETE FROM cloud_gallery_manifests').run();
  await db.prepare('DELETE FROM cloud_gallery_jobs').run();

  return { deletedCount: totalDeleted };
}

function mapJobRow(row: any): GalleryJob {
  let fileIds: string[] = [];
  try {
    fileIds = JSON.parse(row.file_ids_json || '[]');
  } catch { }

  return {
    id: row.id,
    orderId: row.order_id,
    manifestVersion: row.manifest_version,
    batchIndex: row.batch_index ?? 0,
    fileIds,
    status: row.status,
    claimToken: row.claim_token,
    claimedAt: row.claimed_at,
    expiresAt: row.expires_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    errorMessage: row.error_message
  };
}
