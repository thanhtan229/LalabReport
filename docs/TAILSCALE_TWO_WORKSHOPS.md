# Tailscale cho 2 xưởng A/B + xem/in từ điện thoại

Mục tiêu: một điện thoại xem và in được **cả xưởng A và xưởng B**, mỗi xưởng chạy app riêng trên PC riêng, không cần mở port ra Internet, không cần cài app desktop trên điện thoại.

## 0. Trạng thái hiện tại (máy A)

- Tailscale đã cài, service `Tailscale` = Running/Automatic.
- Node A: `tiniwin01`, IP Tailscale `100.81.149.85`.
- App Lalab Auto Report 2.0.0 đang chạy, PWA LAN tại `http://100.81.149.85:5050`.
- Firewall đã có rule cho Mobile Web Server, port TCP 5050.
- Đã verify: `http://100.81.149.85:5050/api/status` → 200.

## 1. Mô hình

```text
Điện thoại (Tailscale)
   ├─ http://100.81.149.85:5050  -> PC xưởng A -> DB/PWA/máy in A
   └─ http://100.<IP-B>:5050     -> PC xưởng B -> DB/PWA/máy in B
```

- Mỗi xưởng là một installation độc lập: DB riêng, PIN riêng, máy in riêng.
- Không trỏ A và B vào cùng một Cloud worker (worker hiện single-workshop).
- Tailscale chỉ tạo mạng riêng giữa các thiết bị; app vẫn là HTTP nội bộ, không cần TLS.

## 2. Cài đặt PC xưởng B

1. Cài Tailscale trên PC B: https://tailscale.com/download/windows
2. Đăng nhập **cùng tailnet** với tài khoản đang dùng cho máy A (hoặc share node A/B về tài khoản của điện thoại — xem mục 6).
3. Đặt tên máy dễ nhớ, ví dụ `lalab-b`.
4. Lấy IP Tailscale:
   ```powershell
   tailscale ip -4
   # ví dụ: 100.101.20.30
   ```
5. Cài/chạy Lalab Auto Report trên PC B, cấu hình root folder, PIN Admin/Staff, máy in nhiệt.
6. Bật app tự khởi động (Settings → Auto start), tắt sleep khi cắm điện.
7. Kiểm tra trên chính PC B:
   ```powershell
   Invoke-RestMethod http://127.0.0.1:5050/api/status
   Invoke-RestMethod http://100.101.20.30:5050/api/status
   ```

## 3. Cài Tailscale trên điện thoại

1. Cài Tailscale (App Store / Google Play).
2. Đăng nhập cùng tailnet với PC A/B.
3. Bật VPN của Tailscale.
4. Kiểm tra kết nối tới từng PC:
   - Mở `http://100.81.149.85:5050` (A)
   - Mở `http://100.101.20.30:5050` (B, thay IP thật)
5. “Add to Home screen” cho từng URL để có 2 biểu tượng A và B.
6. Đăng nhập PIN riêng của từng xưởng.

## 4. In từ điện thoại

- Mở URL của xưởng nào, bấm in bill/tem → lệnh in chạy trên **PC của xưởng đó**.
- Máy in phải cài trên PC đó (hoặc share tới PC đó) và được chọn làm máy in nhiệt mặc định trong Settings.
- Điện thoại **không in trực tiếp qua Bluetooth** trong bản này; không cần cài app in trên điện thoại.
- Kiểm tra thực tế: 1 bill + 1 tem từ mỗi URL, đúng khổ 40x100/75x100, đúng máy in.

## 5. Kiểm tra nhanh (checklist)

Trên điện thoại (Tailscale bật):
- [ ] `http://100.81.149.85:5050/api/status` trả JSON, đúng tên xưởng A.
- [ ] `http://100.<IP-B>:5050/api/status` trả JSON, đúng tên xưởng B.
- [ ] Đăng nhập PIN A, xem đơn A, in thử A.
- [ ] Đăng nhập PIN B, xem đơn B, in thử B.
- [ ] Tắt Wi-Fi, dùng 4G nhưng vẫn bật Tailscale → cả 2 URL vẫn mở được.
- [ ] PC A/B không sleep; app đang chạy; Tailscale service đang chạy.

Trên PC:
```powershell
Get-Service Tailscale
tailscale status
tailscale ping tiniwin01
Get-NetTCPConnection -LocalPort 5050 -State Listen
```

## 6. Bảo mật / vận hành

- Không bật **Tailscale Funnel** và không port-forward 5050 ra Internet.
- Chỉ chia sẻ node với người tin cậy; điện thoại phải nằm trong tailnet hoặc được share đúng node.
- Nếu dùng ACL, giới hạn điện thoại chỉ tới port 5050, ví dụ:
  ```json
  {
    "acls": [
      { "action": "accept",
        "src": ["group:lalab-phone"],
        "dst": ["tag:lalab-pc:5050"] }
    ],
    "groups": { "group:lalab-phone": ["user@example.com"] },
    "tagOwners": { "tag:lalab-pc": ["autogroup:admin"] }
  }
  ```
  Gắn tag `tag:lalab-pc` cho PC A/B trong admin console.
- Tailscale key expiry mặc định ~180 ngày: đặt **Disable key expiry** cho PC xưởng, hoặc nhắc lịch re-auth; nếu key hết hạn, điện thoại mất truy cập.
- PIN Admin/Staff của từng xưởng phải khác nhau và không dùng PIN demo.
- App hiện bind LAN port 5050 trên PC; Tailscale mã hoá đường truyền nhưng trong LAN nội bộ vẫn là HTTP — chỉ dùng trong mạng tin cậy.
- Nếu xưởng có nhiều máy trạm, mỗi xưởng vẫn chỉ nên có **một** app giữ DB (máy chính của xưởng đó).

## 7. Xử lý sự cố

| Hiện tượng | Kiểm tra |
|---|---|
| Điện thoại không mở được URL | Tailscale trên điện thoại đã bật? `tailscale status` trên PC có online? |
| Mở được `/api/status` nhưng PWA trắng | Hard-refresh; kiểm tra app còn chạy; xem Console trình duyệt |
| Một xưởng mở được, xưởng kia không | Sai IP/node B; B chưa cài app/chưa bật Tailscale; firewall B chưa có rule 5050 |
| In không ra | Máy in trên PC tương ứng chưa cài/chọn mặc định; PC sleep; xem log app |
| Tailscale báo key expired | Re-auth PC hoặc tắt key expiry cho node xưởng |
| IP Tailscale đổi | IP 100.x ổn định theo node; nếu xóa/thêm node, cập nhật bookmark |

## 8. Rollback

- Ngắt Tailscale trên điện thoại (hoặc logout) là mất quyền truy cập từ xa ngay.
- Gỡ node B khỏi tailnet nếu ngừng dùng; không ảnh hưởng dữ liệu app/DB.
- Không cần thay đổi code hay database khi bật/tắt Tailscale.
