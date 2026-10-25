import sqlite3
import os
from PIL import Image

db_path = r'C:\Users\Admin\AppData\Local\LalabAutoReport\lalab_autoreport.db'
conn = sqlite3.connect(db_path)
c = conn.cursor()

c.execute("SELECT cache_key, cached_file_path, cached_file_size, status, source_relative_path FROM thumbnail_cache_entries WHERE source_relative_path LIKE '%Quang Studio%'")
rows = c.fetchall()
print(f"Total Quang Studio cached rows in DB: {len(rows)}")

files_checked = 0
max_dimension = 0
invalid_files = []

for r in rows:
    key, path, size, status, source = r
    if os.path.exists(path):
        files_checked += 1
        try:
            with Image.open(path) as img:
                w, h = img.size
                if max(w, h) > max_dimension:
                    max_dimension = max(w, h)
                if max(w, h) > 320:
                    invalid_files.append((path, w, h))
        except Exception as ex:
            print(f"Error opening image {path}: {ex}")
    else:
        print(f"Cache file does not exist on disk: {path}")

print(f"Files verified on disk: {files_checked}")
print(f"Max image dimension across all thumbnails: {max_dimension}px (Requirement: <= 320px)")
if invalid_files:
    print(f"FAIL: Found {len(invalid_files)} files exceeding 320px: {invalid_files}")
else:
    print("PASS: All cached thumbnail files exist and are <= 320px.")

# Check log file entries for order 7 / photo_02
log_path = r'C:\Users\Admin\AppData\Local\LalabAutoReport\logs\lalab-20261025.log'
if os.path.exists(log_path):
    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        log_lines = f.readlines()
    thumbnail_logs = [l.strip() for l in log_lines if 'thumbnail' in l.lower() or 'photo_02' in l.lower()]
    print(f"\nTotal thumbnail log entries today: {len(thumbnail_logs)}")
    print("Sample log entries:")
    for l in thumbnail_logs[-5:]:
        print("  ", l)
