import sys
from playwright.sync_api import sync_playwright

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

with sync_playwright() as p:
    b = p.chromium.launch(headless=True)
    page = b.new_page(viewport={"width": 390, "height": 844})
    page.on("pageerror", lambda e: print(f"PAGE ERROR: {e}", flush=True))
    page.on("console", lambda m: print(f"CONSOLE {m.type}: {m.text}", flush=True))

    page.goto("http://127.0.0.1:5050", wait_until="load")
    page.wait_for_timeout(1000)

    page.locator("#pinInput").fill("123456")
    page.locator("#pinModal button").click()
    page.wait_for_timeout(2000)

    print("--- Calling openOrderGallery(2) without returning promise ---", flush=True)
    page.evaluate("() => { openOrderGallery(2); }")
    page.wait_for_timeout(2000)
    title = page.evaluate("() => document.getElementById('galleryModalTitle')?.innerText")
    body = page.evaluate("() => document.getElementById('galleryModalBody')?.innerText")
    print(f"Title: {title}", flush=True)
    print(f"Body: {body}", flush=True)
    page.screenshot(path="docs/qa-evidence-gallery/spa_06_order_2_gallery.png")

    b.close()
