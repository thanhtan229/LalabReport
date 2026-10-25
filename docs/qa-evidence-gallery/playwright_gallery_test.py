import sys
import time
import json
from playwright.sync_api import sync_playwright

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

def run_tests():
    evidence = {}
    console_logs = []
    failed_requests = []
    aborted_requests = []

    print("Starting Playwright full suite...", flush=True)
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        # Mobile viewport 390x844
        context = browser.new_context(
            viewport={"width": 390, "height": 844},
            user_agent="Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1"
        )
        page = context.new_page()

        def on_console(msg):
            console_logs.append({"type": msg.type, "text": msg.text})
            print(f"[CONSOLE {msg.type}] {msg.text}", flush=True)
        page.on("console", on_console)

        def on_request_failed(req):
            err = req.failure
            if "net::ERR_ABORTED" in str(err) or "aborted" in str(err).lower():
                aborted_requests.append({"url": req.url, "error": str(err)})
                print(f"[ABORTED] {req.url}", flush=True)
            else:
                failed_requests.append({"url": req.url, "error": str(err)})
                print(f"[FAILED] {req.url}: {err}", flush=True)
        page.on("requestfailed", on_request_failed)

        # Step 1: Navigate to SPA
        print("1. Loading http://127.0.0.1:5050...", flush=True)
        page.goto("http://127.0.0.1:5050", wait_until="load")
        page.wait_for_timeout(1000)
        page.screenshot(path="docs/qa-evidence-gallery/spa_01_login.png")

        # Step 2: Login with PIN 123456
        print("2. Entering PIN 123456...", flush=True)
        pin_input = page.locator("#pinInput")
        pin_input.fill("123456")
        page.locator("#pinModal button").click()
        page.wait_for_timeout(1500)
        page.screenshot(path="docs/qa-evidence-gallery/spa_02_dashboard.png")

        logged_in = page.evaluate("() => typeof authToken !== 'undefined' && authToken !== null && authToken !== ''")
        evidence["logged_in"] = logged_in
        print(f"Logged in: {logged_in}", flush=True)

        # Step 3: Open Order 7 Gallery
        print("3. Opening Order 7 gallery...", flush=True)
        page.evaluate("() => { openOrderGallery(7); }")
        page.wait_for_timeout(2500)
        page.screenshot(path="docs/qa-evidence-gallery/spa_03_order_7_gallery_page1.png")

        # Verify 3-column grid
        grid_classes = page.evaluate("""() => {
            const grids = document.querySelectorAll('#galleryModalBody .grid');
            return Array.from(grids).map(g => g.className);
        }""")
        evidence["grid_classes"] = grid_classes
        has_3_cols = any("grid-cols-3" in c for c in grid_classes)
        evidence["has_3_column_grid"] = has_3_cols
        print(f"Has 3-column grid: {has_3_cols}", flush=True)

        # Verify contain (no crop)
        img_classes = page.evaluate("""() => {
            const imgs = document.querySelectorAll('#galleryModalBody img');
            return Array.from(imgs).map(img => img.className);
        }""")
        evidence["img_classes_sample"] = img_classes[:5]
        all_contain = all("object-contain" in c for c in img_classes) if img_classes else False
        evidence["all_images_object_contain"] = all_contain
        print(f"All images object-contain: {all_contain}", flush=True)

        # Verify lazy loading
        loading_attrs = page.evaluate("""() => {
            const imgs = document.querySelectorAll('#galleryModalBody img');
            return Array.from(imgs).map(img => img.getAttribute('loading'));
        }""")
        evidence["loading_attributes_sample"] = loading_attrs[:5]
        all_lazy = all(a == "lazy" for a in loading_attrs) if loading_attrs else False
        evidence["all_images_lazy_loading"] = all_lazy
        print(f"All images lazy loading: {all_lazy}", flush=True)

        # Verify file names
        file_names = page.evaluate("""() => {
            const titles = document.querySelectorAll('#galleryModalBody [data-file-id] div.truncate');
            return Array.from(titles).map(t => t.innerText.trim());
        }""")
        evidence["file_names_count"] = len(file_names)
        evidence["file_names_sample"] = file_names[:6]
        print(f"Page 1 file names count: {len(file_names)}, sample: {file_names[:6]}", flush=True)

        # Check unresolved warning
        unresolved_warn = page.evaluate("""() => {
            const warnings = document.querySelectorAll('#galleryModalBody .text-rose-300');
            return Array.from(warnings).map(w => w.innerText.trim());
        }""")
        evidence["unresolved_warning"] = unresolved_warn
        print(f"Unresolved folder warning: {unresolved_warn}", flush=True)

        # Check corrupt photo_02 error state
        corrupt_state = page.evaluate("""() => {
            const card = document.querySelector('[data-file-id="13a34e10eee3840e"]');
            if (!card) return { found: false };
            const img = card.querySelector('img');
            const errDiv = card.querySelector('.text-rose-400');
            return {
                found: true,
                imgHidden: img ? img.classList.contains('hidden') : false,
                imgSrc: img ? img.src : null,
                errVisible: errDiv ? !errDiv.classList.contains('hidden') : false,
                errText: errDiv ? errDiv.innerText : null
            };
        }""")
        evidence["corrupt_photo_02_state"] = corrupt_state
        print(f"Corrupt photo_02 state: {corrupt_state}", flush=True)

        # Scroll modal to trigger touch / lazy loading
        page.evaluate("""() => {
            const body = document.getElementById('galleryModalBody');
            body.scrollTop = body.scrollHeight;
        }""")
        page.wait_for_timeout(1000)

        # Check load-more button
        load_more_btn = page.locator("#galleryLoadMoreContainer button")
        has_load_more = load_more_btn.is_visible()
        evidence["has_load_more_button"] = has_load_more
        load_more_text = load_more_btn.inner_text() if has_load_more else ""
        evidence["load_more_button_text"] = load_more_text
        print(f"Load more button visible: {has_load_more} ('{load_more_text}')", flush=True)

        if has_load_more:
            print("Clicking load-more button...", flush=True)
            load_more_btn.click()
            page.wait_for_timeout(2000)
            page.screenshot(path="docs/qa-evidence-gallery/spa_04_order_7_after_load_more.png")

            total_cards = page.evaluate("""() => {
                return document.querySelectorAll('#galleryModalBody [data-file-id]').length;
            }""")
            evidence["total_cards_after_load_more"] = total_cards
            print(f"Total cards after load more: {total_cards} (expected 33)", flush=True)

            all_displayed_text = page.locator("#galleryLoadMoreContainer").inner_text()
            evidence["load_more_container_final_text"] = all_displayed_text
            print(f"Load more container final text: '{all_displayed_text}'", flush=True)

        # Step 4: Close modal aborts in-flight requests (no console errors)
        print("4. Testing close modal aborts in-flight requests...", flush=True)
        page.evaluate("() => { openOrderGallery(7); }")
        time.sleep(0.02)
        # Abort count before close
        aborts_before = len(aborted_requests)
        errors_before = len([l for l in console_logs if l["type"] == "error"])
        page.evaluate("() => { closeGalleryModal(); }")
        page.wait_for_timeout(500)
        page.screenshot(path="docs/qa-evidence-gallery/spa_05_modal_closed.png")
        errors_after = len([l for l in console_logs if l["type"] == "error"])
        evidence["console_errors_on_abort"] = errors_after - errors_before
        print(f"New console errors on abort: {errors_after - errors_before}", flush=True)

        # Step 5: Reopening another order (Order 2)
        print("5. Testing reopening another order (Order 2)...", flush=True)
        page.evaluate("() => { openOrderGallery(2); }")
        page.wait_for_timeout(2000)
        page.screenshot(path="docs/qa-evidence-gallery/spa_06_order_2_gallery.png")

        order_2_state = page.evaluate("""() => {
            const title = document.getElementById('galleryModalTitle')?.innerText;
            const subtitle = document.getElementById('galleryModalSubtitle')?.innerText;
            const bodyText = document.getElementById('galleryModalBody')?.innerText;
            const cards = document.querySelectorAll('#galleryModalBody [data-file-id]').length;
            return {
                title: title,
                subtitle: subtitle,
                cards: cards,
                bodyText: bodyText,
                currentGalleryOrderId: typeof currentGalleryOrderId !== 'undefined' ? currentGalleryOrderId : null
            };
        }""")
        evidence["order_2_gallery_state"] = order_2_state
        print(f"Order 2 gallery state: {order_2_state}", flush=True)

        # Step 6: Reopening Order 7 again to verify touch observer uses correct order
        print("6. Reopening Order 7 to verify touch observer and state...", flush=True)
        page.evaluate("() => { closeGalleryModal(); }")
        page.wait_for_timeout(500)
        page.evaluate("() => { openOrderGallery(7); }")
        page.wait_for_timeout(2000)
        reopen_order_id = page.evaluate("() => currentGalleryOrderId")
        evidence["reopened_currentGalleryOrderId"] = reopen_order_id
        print(f"Reopened currentGalleryOrderId: {reopen_order_id}", flush=True)

        evidence["console_logs"] = console_logs
        evidence["aborted_requests"] = aborted_requests
        evidence["failed_requests"] = failed_requests

        with open("docs/qa-evidence-gallery/11_browser_spa_test_results.json", "w", encoding="utf-8") as f:
            json.dump(evidence, f, ensure_ascii=False, indent=2)

        browser.close()
        print("Playwright test completed successfully!", flush=True)

if __name__ == "__main__":
    run_tests()
