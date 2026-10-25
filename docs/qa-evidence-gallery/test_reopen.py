import sys
import time
from playwright.sync_api import sync_playwright

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={"width": 390, "height": 844})

    page.on("framenavigated", lambda frame: print(f"Frame navigated: {frame.url}", flush=True))
    page.on("console", lambda msg: print(f"[CONSOLE {msg.type}] {msg.text}", flush=True))

    page.goto("http://127.0.0.1:5050", wait_until="domcontentloaded")
    page.wait_for_timeout(1000)
    page.locator("#pinInput").fill("123456")
    page.locator("button:has-text('Đăng nhập')").click()
    page.wait_for_timeout(1500)

    print("Opening Order 7...", flush=True)
    page.evaluate("() => openOrderGallery(7)")
    page.wait_for_timeout(2000)

    print("Closing gallery modal...", flush=True)
    page.evaluate("() => closeGalleryModal()")
    page.wait_for_timeout(1000)

    print("Opening Order 2...", flush=True)
    page.evaluate("() => openOrderGallery(2)")
    page.wait_for_timeout(2000)

    title = page.evaluate("() => document.getElementById('galleryModalTitle')?.innerText")
    subtitle = page.evaluate("() => document.getElementById('galleryModalSubtitle')?.innerText")
    body = page.evaluate("() => document.getElementById('galleryModalBody')?.innerText")
    print(f"Order 2 modal: title='{title}', subtitle='{subtitle}', body='{body}'", flush=True)

    page.screenshot(path="docs/qa-evidence-gallery/spa_06_order_2_gallery.png")
    browser.close()
