import sys
from playwright.sync_api import sync_playwright

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

with sync_playwright() as p:
    b = p.chromium.launch(headless=True)
    page = b.new_page(viewport={"width": 390, "height": 844})
    page.on("framenavigated", lambda f: print(f"NAVIGATED: {f.url}", flush=True))
    page.on("request", lambda r: print(f"REQ: {r.method} {r.url}", flush=True))
    page.on("crash", lambda: print("PAGE CRASHED!", flush=True))
    page.on("close", lambda: print("PAGE CLOSED!", flush=True))

    page.goto("http://127.0.0.1:5050", wait_until="load")
    page.wait_for_timeout(1000)

    page.locator("#pinInput").fill("123456")
    page.locator("#pinModal button").click()
    page.wait_for_timeout(2000)

    print("Opening Order 7...", flush=True)
    page.evaluate("openOrderGallery(7)")
    page.wait_for_timeout(2000)

    print("Closing modal...", flush=True)
    page.evaluate("closeGalleryModal()")
    page.wait_for_timeout(1000)

    print("Opening Order 2...", flush=True)
    page.evaluate("openOrderGallery(2)")
    page.wait_for_timeout(2000)

    print("Checking Order 2 modal content...", flush=True)
    title = page.evaluate("document.getElementById('galleryModalTitle')?.innerText")
    body = page.evaluate("document.getElementById('galleryModalBody')?.innerText")
    print(f"Order 2 Title: {title}", flush=True)
    print(f"Order 2 Body: {body}", flush=True)

    page.screenshot(path="docs/qa-evidence-gallery/spa_06_order_2_gallery.png")
    b.close()
