-- 0007_order_gallery.sql: Schema for Order Gallery Manifests, On-Demand Jobs, and Gallery Media

CREATE TABLE IF NOT EXISTS cloud_gallery_manifests (
    order_id INTEGER PRIMARY KEY,
    manifest_version TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    total_items INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS cloud_gallery_jobs (
    id TEXT PRIMARY KEY,
    order_id INTEGER NOT NULL,
    manifest_version TEXT NOT NULL,
    batch_index INTEGER NOT NULL DEFAULT 0,
    file_ids_json TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'PENDING', -- PENDING, CLAIMED, READY, FAILED, EXPIRED
    claim_token TEXT,
    claimed_at TEXT,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    error_message TEXT
);

CREATE INDEX IF NOT EXISTS idx_gallery_jobs_status_expires ON cloud_gallery_jobs(status, expires_at);
CREATE INDEX IF NOT EXISTS idx_gallery_jobs_order ON cloud_gallery_jobs(order_id);

CREATE TABLE IF NOT EXISTS cloud_gallery_media (
    file_id TEXT NOT NULL,
    version TEXT NOT NULL,
    r2_key TEXT NOT NULL,
    byte_size INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    last_used_at TEXT NOT NULL,
    PRIMARY KEY (file_id, version)
);

CREATE INDEX IF NOT EXISTS idx_gallery_media_last_used ON cloud_gallery_media(last_used_at);

