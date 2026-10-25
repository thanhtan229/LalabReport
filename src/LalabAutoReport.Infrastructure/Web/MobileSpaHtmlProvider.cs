namespace LalabAutoReport.Infrastructure.Web;

public static class MobileSpaHtmlProvider
{
    public static string GetIndexHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""vi"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
  <title>Lalab Mobile</title>
  <link rel=""manifest"" href=""/manifest.json"">
  <meta name=""theme-color"" content=""#0f172a"">
  <script src=""https://cdn.tailwindcss.com""></script>
  <style>
    body { background-color: #0b0f17; color: #f1f5f9; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }
    .tab-active { color: #38bdf8; border-bottom: 2px solid #38bdf8; }
    .badge-printed { background: #065f46; color: #34d399; }
    .badge-pending { background: #854d0e; color: #fde047; }
    .badge-delivered { background: #1e3a8a; color: #93c5fd; }
    .badge-undelivered { background: #374151; color: #9ca3af; }
    .badge-issue { background: #991b1b; color: #fca5a5; }
    /* Hide scrollbars but keep touch scroll */
    .no-scrollbar::-webkit-scrollbar { display: none; }
    .no-scrollbar { -ms-overflow-style: none; scrollbar-width: none; }
  </style>
</head>
<body class=""h-screen flex flex-col justify-between overflow-hidden select-none"">

  <!-- DATE NOTIFICATION TOAST -->
  <div id=""dateToast"" class=""fixed top-14 left-1/2 -translate-x-1/2 z-50 bg-slate-900/95 border border-sky-500/60 shadow-2xl shadow-sky-950/80 text-slate-100 text-xs font-semibold px-4 py-2 rounded-full pointer-events-none transition-all duration-300 opacity-0 -translate-y-2 flex items-center space-x-2 backdrop-blur"">
    <span class=""text-sky-400 text-sm"">📅</span>
    <span id=""dateToastText"">Đang chọn ngày...</span>
  </div>

  <!-- ACTION NOTIFICATION TOAST -->
  <div id=""actionToast"" class=""fixed top-14 left-1/2 -translate-x-1/2 z-50 bg-slate-900/95 border border-emerald-500/70 shadow-2xl shadow-emerald-950/80 text-emerald-300 text-xs font-semibold px-4 py-2.5 rounded-full pointer-events-none transition-all duration-300 opacity-0 -translate-y-2 flex items-center space-x-2 backdrop-blur"">
    <span id=""actionToastIcon"">✅</span>
    <span id=""actionToastText"">Thao tác thành công</span>
  </div>

  <!-- TOP HEADER -->
  <header class=""bg-slate-900/90 backdrop-blur border-b border-slate-800 px-4 py-3 flex items-center justify-between z-10"">
    <div class=""flex items-center space-x-2"">
      <div class=""w-8 h-8 rounded-lg bg-sky-500/20 text-sky-400 flex items-center justify-center font-bold text-lg"">L</div>
      <div>
        <h1 id=""workshopTitle"" class=""font-semibold text-sm leading-tight text-slate-100"">Lalab Auto Report</h1>
        <p id=""roleBadge"" class=""text-[11px] text-slate-400"">Đang tải...</p>
      </div>
    </div>
    <button onclick=""logout()"" class=""text-xs text-slate-400 hover:text-rose-400 px-2 py-1 rounded bg-slate-800 border border-slate-700"">
      Đổi mã PIN
    </button>
  </header>

  <!-- MAIN SCROLLABLE CONTENT -->
  <main id=""mainContent"" class=""flex-1 overflow-y-auto no-scrollbar pb-20"">
    
    <!-- SECTION: ORDERS VIEW -->
    <div id=""ordersSection"" class=""space-y-0"">
      
      <!-- STICKY TOP CONTROLS (Date, Search, Quick Filter Tabs) -->
      <div class=""sticky top-0 z-20 bg-slate-900/95 backdrop-blur-md px-4 pt-3 pb-3 space-y-2.5 border-b border-slate-800 shadow-md shadow-black/40"">
        
        <!-- Date Filter & Navigation -->
        <div class=""flex items-center space-x-1.5"">
          <button onclick=""navigateDay(-1)"" title=""Ngày trước (hoặc vuốt phải)"" class=""w-9 h-9 flex items-center justify-center bg-slate-800 border border-slate-700 text-slate-300 hover:text-white rounded-lg text-sm active:scale-95 active:bg-slate-700 shrink-0 shadow-sm"">
            <svg class=""w-4 h-4"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2.5"" d=""M15 19l-7-7 7-7""/></svg>
          </button>
          <input type=""date"" id=""orderDateInput"" onchange=""onDateInputChange()"" class=""bg-slate-800 border border-slate-700 text-slate-200 text-xs rounded-lg px-2 py-2 flex-1 text-center font-medium focus:outline-none focus:border-sky-500 min-w-0"">
          <button onclick=""navigateDay(1)"" title=""Ngày kế (hoặc vuốt trái)"" class=""w-9 h-9 flex items-center justify-center bg-slate-800 border border-slate-700 text-slate-300 hover:text-white rounded-lg text-sm active:scale-95 active:bg-slate-700 shrink-0 shadow-sm"">
            <svg class=""w-4 h-4"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2.5"" d=""M9 5l7 7-7 7""/></svg>
          </button>
          <button onclick=""setToday(true)"" class=""h-9 px-2.5 bg-slate-800 border border-slate-700 rounded-lg text-xs font-medium text-slate-300 hover:text-white active:scale-95 active:bg-slate-700 shrink-0 shadow-sm"">Hôm nay</button>
          <button onclick=""loadOrders()"" title=""Tải lại dữ liệu"" class=""w-9 h-9 flex items-center justify-center bg-slate-800 border border-slate-700 rounded-lg text-xs font-medium text-sky-400 active:scale-95 active:bg-slate-700 shrink-0 shadow-sm"">🔄</button>
        </div>

        <div class=""relative"">
          <input type=""text"" id=""orderSearchInput"" oninput=""filterOrders()"" placeholder=""Tìm tên khách, mã đơn, quy cách..."" class=""w-full bg-slate-950 border border-slate-800 text-xs text-slate-200 rounded-lg pl-8 pr-3 py-2.5 focus:outline-none focus:border-sky-500"">
          <svg class=""w-4 h-4 text-slate-500 absolute left-2.5 top-3"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z""/></svg>
        </div>

        <!-- Quick Stats Filter Tabs -->
        <div class=""grid grid-cols-5 gap-1.5 text-center"">
          <button onclick=""setFilterMode('all')"" id=""statCardAll"" class=""bg-slate-900 border-2 border-sky-500 rounded-lg p-1.5 transition active:scale-95"">
            <div class=""text-[10px] text-slate-400"">Tất cả</div>
            <div id=""statTotalOrders"" class=""text-sm font-bold text-slate-100"">0</div>
          </button>
          <button onclick=""setFilterMode('pending')"" id=""statCardPending"" class=""bg-slate-900 border-2 border-transparent hover:border-slate-700 rounded-lg p-1.5 transition active:scale-95"">
            <div class=""text-[10px] text-amber-400"">Chờ in</div>
            <div id=""statPendingOrders"" class=""text-sm font-bold text-amber-400"">0</div>
          </button>
          <button onclick=""setFilterMode('printed')"" id=""statCardPrinted"" class=""bg-slate-900 border-2 border-transparent hover:border-slate-700 rounded-lg p-1.5 transition active:scale-95"">
            <div class=""text-[10px] text-emerald-400"">Đã in</div>
            <div id=""statPrintedOrders"" class=""text-sm font-bold text-emerald-400"">0</div>
          </button>
          <button onclick=""setFilterMode('undelivered')"" id=""statCardUndelivered"" class=""bg-slate-900 border-2 border-transparent hover:border-slate-700 rounded-lg p-1.5 transition active:scale-95"">
            <div class=""text-[10px] text-slate-300"">Chưa giao</div>
            <div id=""statUndeliveredOrders"" class=""text-sm font-bold text-slate-300"">0</div>
          </button>
          <button onclick=""setFilterMode('delivered')"" id=""statCardDelivered"" class=""bg-slate-900 border-2 border-transparent hover:border-slate-700 rounded-lg p-1.5 transition active:scale-95"">
            <div class=""text-[10px] text-sky-400"">Đã giao</div>
            <div id=""statDeliveredOrders"" class=""text-sm font-bold text-sky-400"">0</div>
          </button>
        </div>
      </div>

      <!-- Order List Container -->
      <div id=""orderList"" class=""p-4 space-y-3"">
        <div class=""text-center text-xs text-slate-500 py-8"">Đang tải danh sách đơn hàng...</div>
      </div>
    </div>

    <!-- SECTION: REPORTS VIEW (Admin only) -->
    <div id=""reportsSection"" class=""hidden p-4 space-y-4"">
      <div class=""flex space-x-2"">
        <input type=""month"" id=""reportMonthInput"" onchange=""loadMonthlyReport()"" class=""bg-slate-800 border border-slate-700 text-slate-200 text-xs rounded-lg px-3 py-2 flex-1 focus:outline-none"">
      </div>

      <div class=""grid grid-cols-2 gap-3"">
        <div class=""bg-gradient-to-br from-sky-950 to-slate-900 border border-sky-800/40 rounded-xl p-3.5"">
          <div class=""text-xs text-sky-400 font-medium"">Doanh thu tháng</div>
          <div id=""repTotalRevenue"" class=""text-lg font-bold text-slate-100 mt-1"">0 đ</div>
          <div id=""repTotalOrdersCount"" class=""text-[11px] text-slate-400 mt-0.5"">0 đơn hàng</div>
        </div>
        <div class=""bg-gradient-to-br from-indigo-950 to-slate-900 border border-indigo-800/40 rounded-xl p-3.5"">
          <div class=""text-xs text-indigo-400 font-medium"">Số lượng in</div>
          <div id=""repTotalPrintCount"" class=""text-lg font-bold text-slate-100 mt-1"">0 tấm</div>
          <div id=""repTotalCustomers"" class=""text-[11px] text-slate-400 mt-0.5"">0 khách hàng</div>
        </div>
      </div>

      <!-- Top Specifications Breakdown -->
      <div class=""bg-slate-900 border border-slate-800 rounded-xl p-3.5"">
        <h3 class=""text-xs font-semibold text-slate-300 uppercase tracking-wider mb-2.5"">Quy cách in nhiều nhất</h3>
        <div id=""repSpecList"" class=""space-y-2"">
          <!-- Filled dynamically -->
        </div>
      </div>
    </div>

    <!-- SECTION: DEBTS & UNPAID (Admin only) -->
    <div id=""debtsSection"" class=""hidden p-4 space-y-3"">
      <div class=""bg-amber-950/40 border border-amber-800/30 rounded-xl p-3"">
        <div class=""text-xs text-amber-300"">Tổng công nợ chưa thu</div>
        <div id=""debtsTotalSum"" class=""text-xl font-bold text-amber-400 mt-1"">0 đ</div>
      </div>

      <div id=""debtsList"" class=""space-y-2.5"">
        <!-- Filled dynamically -->
      </div>
    </div>

  </main>

  <!-- BOTTOM NAVIGATION BAR -->
  <nav id=""bottomNav"" class=""bg-slate-900/95 backdrop-blur border-t border-slate-800 flex justify-around py-2 fixed bottom-0 left-0 right-0 z-20"">
    <button onclick=""switchTab('orders')"" id=""tabOrdersBtn"" class=""flex flex-col items-center py-1 px-4 text-xs font-medium tab-active"">
      <svg class=""w-5 h-5 mb-1"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2""/></svg>
      Đơn hàng
    </button>
    <button onclick=""switchTab('reports')"" id=""tabReportsBtn"" class=""flex flex-col items-center py-1 px-4 text-xs font-medium text-slate-400"">
      <svg class=""w-5 h-5 mb-1"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M16 8v8m-4-5v5m-4-2v2m-2 4h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z""/></svg>
      Báo cáo
    </button>
    <button onclick=""switchTab('debts')"" id=""tabDebtsBtn"" class=""flex flex-col items-center py-1 px-4 text-xs font-medium text-slate-400"">
      <svg class=""w-5 h-5 mb-1"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M12 8c-1.657 0-3 .895-3 2s1.343 2 3 2 3 .895 3 2-1.343 2-3 2m0-8c1.11 0 2.08.402 2.599 1M12 8V7m0 1v8m0 0v1m0-1c-1.11 0-2.08-.402-2.599-1M21 12a9 9 0 11-18 0 9 9 0 0118 0z""/></svg>
      Công nợ
    </button>
  </nav>

  <!-- PIN LOGIN MODAL -->
  <div id=""pinModal"" class=""fixed inset-0 bg-slate-950 flex flex-col items-center justify-center p-6 z-50 hidden"">
    <div class=""w-12 h-12 rounded-xl bg-sky-500/20 text-sky-400 flex items-center justify-center font-bold text-2xl mb-4"">L</div>
    <h2 class=""text-lg font-bold text-slate-100"">Lalab Auto Report</h2>
    <p class=""text-xs text-slate-400 mt-1 mb-6 text-center"">Nhập mã PIN của bạn (Admin hoặc Nhân viên)</p>

    <div class=""w-full max-w-xs space-y-4"">
      <input type=""password"" id=""pinInput"" maxlength=""6"" pattern=""[0-9]*"" inputmode=""numeric"" placeholder=""••••••"" class=""w-full bg-slate-900 border border-slate-700 text-center text-2xl tracking-widest text-slate-100 rounded-xl py-3 focus:outline-none focus:border-sky-500"">
      <div id=""pinError"" class=""text-xs text-rose-400 text-center hidden"">Mã PIN không đúng</div>
      <button onclick=""submitPin()"" class=""w-full bg-sky-600 hover:bg-sky-500 text-white font-medium py-3 rounded-xl text-sm transition"">Đăng nhập</button>
    </div>
  </div>

  <!-- DETAILS & PREVIEW MODAL -->
  <div id=""previewModal"" onclick=""if (event.target === this) closePreviewModal();"" class=""fixed inset-0 bg-black/80 backdrop-blur-sm z-50 hidden flex flex-col justify-end sm:justify-center p-0 sm:p-4"">
    <div class=""bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl max-h-[90vh] flex flex-col overflow-hidden"">
      <div class=""px-4 py-3 border-b border-slate-800 flex justify-between items-center"">
        <h3 id=""previewModalTitle"" class=""font-semibold text-sm text-slate-100"">Chi tiết</h3>
        <button onclick=""closePreviewModal()"" class=""p-1 text-slate-400 hover:text-slate-200"">
          <svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M6 18L18 6M6 6l12 12""/></svg>
        </button>
      </div>
      <div id=""previewModalBody"" class=""flex-1 overflow-y-auto p-4 space-y-4"">
        <!-- Dynamic content -->
      </div>
    </div>
  </div>

  <!-- ORDER GALLERY MODAL -->
  <div id=""galleryModal"" class=""fixed inset-0 bg-black/90 backdrop-blur-md z-50 hidden flex flex-col p-0 sm:p-4"">
    <div class=""bg-slate-900 border border-slate-800 rounded-none sm:rounded-2xl max-w-2xl w-full mx-auto h-full sm:max-h-[90vh] flex flex-col shadow-2xl overflow-hidden"">
      <div class=""px-4 py-3 border-b border-slate-800 flex items-center justify-between bg-slate-900/90 sticky top-0 z-10"">
        <div class=""min-w-0 pr-2"">
          <h3 id=""galleryModalTitle"" class=""font-bold text-sm text-slate-100 truncate"">Thư viện ảnh in</h3>
          <p id=""galleryModalSubtitle"" class=""text-xs text-slate-400 truncate""></p>
        </div>
        <button onclick=""closeGalleryModal()"" class=""p-1.5 rounded-lg bg-slate-800 text-slate-400 hover:text-white transition"">
          <svg class=""w-5 h-5"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M6 18L18 6M6 6l12 12""/></svg>
        </button>
      </div>
      <div id=""galleryModalBody"" class=""flex-1 overflow-y-auto p-3 space-y-3"">
      </div>
    </div>
  </div>

  <!-- NOTE EDIT MODAL -->
  <div id=""noteModal"" class=""fixed inset-0 bg-black/80 backdrop-blur-sm z-50 hidden flex items-center justify-center p-4"">
    <div class=""bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-sm p-4 space-y-3 shadow-xl"">
      <div class=""flex justify-between items-center"">
        <h3 class=""font-semibold text-sm text-slate-100"">Ghi chú đơn hàng</h3>
        <button onclick=""closeNoteModal()"" class=""text-slate-400 hover:text-slate-200 text-lg leading-none"">✕</button>
      </div>
      <p id=""noteModalSubtitle"" class=""text-xs text-slate-400 truncate""></p>
      <textarea id=""noteInput"" rows=""3"" placeholder=""Ví dụ: thiếu 1 tấm, giao gấp buổi chiều..."" class=""w-full bg-slate-950 border border-slate-700 rounded-xl p-3 text-xs text-slate-100 focus:outline-none focus:border-sky-500""></textarea>
      <div class=""flex justify-end space-x-2 pt-1"">
        <button onclick=""closeNoteModal()"" class=""px-3 py-1.5 text-xs text-slate-400 hover:text-slate-200"">Hủy</button>
        <button onclick=""saveNote()"" class=""px-4 py-1.5 text-xs bg-sky-600 hover:bg-sky-500 text-white font-medium rounded-lg active:scale-95"">Lưu ghi chú</button>
      </div>
    </div>
  </div>

  <script>
    const IS_CLOUD_BUILD = false;
    let authToken = localStorage.getItem('lalab_token') || '';
    let currentRole = '';
    let cachedOrders = [];

    // Init check on load
    window.addEventListener('DOMContentLoaded', async () => {
      // Check query parameter auth token
      const urlParams = new URLSearchParams(window.location.search);
      const queryToken = urlParams.get('auth');
      if (queryToken) {
        authToken = queryToken;
        localStorage.setItem('lalab_token', authToken);
        // Clear query string from URL for clean look
        window.history.replaceState({}, document.title, window.location.pathname);
      }

      setToday();
      setThisMonth();
      setupSwipeGestures();
      await checkAuthAndLoad();
    });

    async function checkAuthAndLoad() {
      if (!authToken) {
        showPinModal();
        return;
      }

      try {
        const res = await fetch('/api/auth/me', {
          headers: { 'Authorization': 'Bearer ' + authToken }
        });

        if (!res.ok) {
          showPinModal();
          return;
        }

        const data = await res.json();
        currentRole = data.role;
        document.getElementById('workshopTitle').innerText = data.workshopName || 'Lalab Auto Report';
        const roleText = currentRole === 'Admin' ? 'Chủ tiệm' : 'Nhân viên';
        const envBadge = IS_CLOUD_BUILD
          ? '<span class=""text-sky-400 font-medium"">☁️ Cloud</span>'
          : '<span class=""text-emerald-400 font-medium"">📶 LAN</span>';
        document.getElementById('roleBadge').innerHTML = `${roleText} &bull; ${envBadge}`;
        
        // Hide admin tabs if staff
        if (currentRole !== 'Admin') {
          document.getElementById('tabReportsBtn').classList.add('hidden');
          document.getElementById('tabDebtsBtn').classList.add('hidden');
        } else {
          document.getElementById('tabReportsBtn').classList.remove('hidden');
          document.getElementById('tabDebtsBtn').classList.remove('hidden');
        }

        hidePinModal();
        loadOrders();
      } catch (err) {
        showPinModal();
      }
    }

    function showPinModal() {
      document.getElementById('pinModal').classList.remove('hidden');
      document.getElementById('pinInput').value = '';
      document.getElementById('pinInput').focus();
    }

    function hidePinModal() {
      document.getElementById('pinModal').classList.add('hidden');
      document.getElementById('pinError').classList.add('hidden');
    }

    async function submitPin() {
      const pin = document.getElementById('pinInput').value.trim();
      if (!pin) return;

      try {
        const res = await fetch('/api/auth/login', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ pin: pin })
        });

        const data = await res.json();
        if (data.success && data.token) {
          authToken = data.token;
          localStorage.setItem('lalab_token', authToken);
          await checkAuthAndLoad();
        } else {
          document.getElementById('pinError').innerText = data.message || 'Mã PIN không đúng';
          document.getElementById('pinError').classList.remove('hidden');
        }
      } catch (e) {
        document.getElementById('pinError').innerText = 'Lỗi kết nối máy chủ';
        document.getElementById('pinError').classList.remove('hidden');
      }
    }

    function logout() {
      authToken = '';
      localStorage.removeItem('lalab_token');
      showPinModal();
    }

    function formatDateIso(dateObj) {
      const yyyy = dateObj.getFullYear();
      const mm = String(dateObj.getMonth() + 1).padStart(2, '0');
      const dd = String(dateObj.getDate()).padStart(2, '0');
      return `${yyyy}-${mm}-${dd}`;
    }

    function parseLocalDate(dateStr) {
      if (!dateStr) return new Date();
      const parts = dateStr.split('-').map(Number);
      if (parts.length !== 3 || isNaN(parts[0])) return new Date();
      return new Date(parts[0], parts[1] - 1, parts[2]);
    }

    function setToday(reload = false) {
      const todayStr = formatDateIso(new Date());
      const input = document.getElementById('orderDateInput');
      if (input) input.value = todayStr;
      if (reload) {
        showDateToast(todayStr);
        loadOrders();
      }
    }

    function onDateInputChange() {
      const input = document.getElementById('orderDateInput');
      if (input && input.value) {
        showDateToast(input.value);
        loadOrders();
      }
    }

    function navigateDay(delta) {
      const input = document.getElementById('orderDateInput');
      if (!input || !input.value) return;
      const current = parseLocalDate(input.value);
      current.setDate(current.getDate() + delta);
      const newDateStr = formatDateIso(current);
      input.value = newDateStr;
      showDateToast(newDateStr);

      const listEl = document.getElementById('orderList');
      if (listEl) {
        listEl.style.transition = 'opacity 0.15s ease-out, transform 0.15s ease-out';
        listEl.style.opacity = '0.35';
        listEl.style.transform = delta > 0 ? 'translateX(-16px)' : 'translateX(16px)';
        setTimeout(() => {
          listEl.style.opacity = '1';
          listEl.style.transform = 'translateX(0)';
        }, 150);
      }

      loadOrders();
    }

    function showDateToast(dateStr) {
      const toast = document.getElementById('dateToast');
      const toastText = document.getElementById('dateToastText');
      if (!toast || !toastText) return;
      const d = parseLocalDate(dateStr);
      const daysOfWeek = ['Chủ Nhật', 'Thứ 2', 'Thứ 3', 'Thứ 4', 'Thứ 5', 'Thứ 6', 'Thứ 7'];
      const dayName = daysOfWeek[d.getDay()];
      const dd = String(d.getDate()).padStart(2, '0');
      const mm = String(d.getMonth() + 1).padStart(2, '0');
      const yyyy = d.getFullYear();

      const today = new Date();
      today.setHours(0, 0, 0, 0);
      const target = new Date(d);
      target.setHours(0, 0, 0, 0);
      const diffDays = Math.round((target - today) / (1000 * 60 * 60 * 24));
      let relLabel = '';
      if (diffDays === 0) relLabel = ' (Hôm nay)';
      else if (diffDays === -1) relLabel = ' (Hôm qua)';
      else if (diffDays === 1) relLabel = ' (Ngày mai)';

      toastText.innerText = `${dayName}, ${dd}/${mm}/${yyyy}${relLabel}`;
      toast.classList.remove('opacity-0', '-translate-y-2');
      toast.classList.add('opacity-100', 'translate-y-0');

      clearTimeout(window._dateToastTimer);
      window._dateToastTimer = setTimeout(() => {
        toast.classList.remove('opacity-100', 'translate-y-0');
        toast.classList.add('opacity-0', '-translate-y-2');
      }, 1200);
    }

    function setupSwipeGestures() {
      const mainEl = document.getElementById('mainContent');
      if (!mainEl) return;

      let touchStartX = 0;
      let touchStartY = 0;
      let touchStartTime = 0;

      mainEl.addEventListener('touchstart', (e) => {
        const ordersSec = document.getElementById('ordersSection');
        if (!ordersSec || ordersSec.classList.contains('hidden')) {
          touchStartX = 0;
          return;
        }

        const pinModal = document.getElementById('pinModal');
        const prevModal = document.getElementById('previewModal');
        const noteModal = document.getElementById('noteModal');
        if ((pinModal && !pinModal.classList.contains('hidden')) ||
            (prevModal && !prevModal.classList.contains('hidden')) ||
            (noteModal && !noteModal.classList.contains('hidden'))) {
          touchStartX = 0;
          return;
        }

        if (e.target.closest('input, textarea, button, a, select')) {
          touchStartX = 0;
          return;
        }

        if (e.touches && e.touches.length === 1) {
          touchStartX = e.touches[0].clientX;
          touchStartY = e.touches[0].clientY;
          touchStartTime = Date.now();
        }
      }, { passive: true });

      mainEl.addEventListener('touchend', (e) => {
        if (!touchStartX) return;
        if (!e.changedTouches || e.changedTouches.length !== 1) return;

        const touchEndX = e.changedTouches[0].clientX;
        const touchEndY = e.changedTouches[0].clientY;
        const deltaX = touchEndX - touchStartX;
        const deltaY = touchEndY - touchStartY;
        const elapsed = Date.now() - touchStartTime;

        touchStartX = 0;

        if (elapsed < 500 && Math.abs(deltaX) >= 50 && Math.abs(deltaX) > 1.8 * Math.abs(deltaY)) {
          if (deltaX < 0) {
            navigateDay(1);
          } else {
            navigateDay(-1);
          }
        }
      }, { passive: true });
    }

    function setThisMonth() {
      const now = new Date();
      const monthStr = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0');
      document.getElementById('reportMonthInput').value = monthStr;
    }

    async function loadOrders() {
      const date = document.getElementById('orderDateInput').value;
      if (!date) return;

      const orderListEl = document.getElementById('orderList');
      orderListEl.innerHTML = '<div class=""text-center text-xs text-slate-500 py-8"">Đang tải dữ liệu...</div>';

      try {
        const res = await fetch('/api/orders?date=' + date, {
          headers: { 'Authorization': 'Bearer ' + authToken }
        });

        if (!res.ok) {
          if (res.status === 401) { logout(); return; }
          orderListEl.innerHTML = '<div class=""text-center text-xs text-rose-400 py-8"">Lỗi tải danh sách đơn</div>';
          return;
        }

        cachedOrders = await res.json();
        renderOrders(cachedOrders);
      } catch (err) {
        orderListEl.innerHTML = '<div class=""text-center text-xs text-rose-400 py-8"">Không thể kết nối máy chủ</div>';
      }
    }

    let currentFilterMode = 'all';

    function setFilterMode(mode) {
      currentFilterMode = mode;
      const modes = ['all', 'pending', 'printed', 'undelivered', 'delivered'];
      const borderColors = {
        all: 'border-sky-500',
        pending: 'border-amber-500',
        printed: 'border-emerald-500',
        undelivered: 'border-slate-500',
        delivered: 'border-sky-500'
      };
      modes.forEach(m => {
        const el = document.getElementById('statCard' + m.charAt(0).toUpperCase() + m.slice(1));
        if (el) {
          el.className = 'bg-slate-900 border-2 ' + (mode === m ? borderColors[m] : 'border-transparent') + ' rounded-lg p-1.5 transition active:scale-95';
        }
      });
      filterOrders();
    }

    function removeDiacritics(str) {
      return (str || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
    }

    function escapeHtml(str) {
      if (!str) return '';
      return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/""/g, '&quot;')
        .replace(/'/g, '&#039;');
    }

    function renderOrders(orders = cachedOrders, updateStats = true) {
      const listEl = document.getElementById('orderList');
      if (updateStats) {
        const total = cachedOrders.length;
        const printed = cachedOrders.filter(o => o.isPrinted).length;
        const pending = total - printed;
        const delivered = cachedOrders.filter(o => o.isDelivered).length;
        const undelivered = total - delivered;

        document.getElementById('statTotalOrders').innerText = total;
        document.getElementById('statPrintedOrders').innerText = printed;
        document.getElementById('statPendingOrders').innerText = pending;
        document.getElementById('statDeliveredOrders').innerText = delivered;
        document.getElementById('statUndeliveredOrders').innerText = undelivered;
      }

      if (orders.length === 0) {
        listEl.innerHTML = '<div class=""text-center text-xs text-slate-500 py-8"">Không có đơn hàng nào phù hợp.</div>';
        return;
      }

      listEl.innerHTML = orders.map(o => {
        const printBadge = o.isPrinted 
          ? `<span class=""badge-printed text-[11px] px-2 py-0.5 rounded font-medium inline-block select-none"" title=""Đã in lúc ${o.printedAt || ''}"">Đã in</span>` 
          : `<span class=""badge-pending text-[11px] px-2 py-0.5 rounded font-medium inline-block select-none"" title=""Chờ in"">Chờ in</span>`;

        const noteHtml = o.note ? `
          <div class=""bg-amber-950/40 border border-amber-800/50 rounded-lg px-2.5 py-1.5 flex items-start justify-between text-xs text-amber-200"">
            <div class=""flex items-start space-x-1.5 min-w-0"">
              <span class=""shrink-0"">📝</span>
              <span class=""break-words font-medium text-amber-100"">${escapeHtml(o.note)}</span>
            </div>
            <button onclick=""openNoteModal(${o.id})"" class=""shrink-0 ml-2 text-[11px] text-amber-400 hover:text-amber-300 underline"">Sửa</button>
          </div>
        ` : '';

        const deliveryInfoHtml = o.isDelivered ? `
          <div class=""text-[11px] text-sky-400/90 flex items-center space-x-1"">
            <span>🚚 Đã giao:</span>
            <span class=""font-mono font-medium text-slate-200"">${o.deliveredAt || ''}</span>
          </div>
        ` : '';

        const itemsHtml = o.items.map(it => `
          <div class=""flex justify-between items-center text-xs py-1 border-b border-slate-800/60 last:border-0"">
            <span class=""text-slate-300"">${it.variantName} <span class=""text-[11px] text-slate-400"">(${it.size || 'Mặc định'})</span></span>
            <span class=""font-semibold text-slate-200"">${it.billQuantity} tấm</span>
          </div>
        `).join('');

        const actions = [];
        if (!o.note) {
          actions.push(`<button onclick=""openNoteModal(${o.id})"" class=""text-xs text-slate-400 hover:text-slate-200 bg-slate-800/60 border border-slate-700/60 px-2 py-1 rounded-lg active:bg-slate-700"">+ Ghi chú</button>`);
        }

        // Nút In tem nhiệt (Mở popup xem trước tem, nhấn In tem trong popup mới in thật sự)
        if (o.customerBillId && currentRole === 'Admin') {
          actions.push(`<button onclick=""viewLabel(${o.customerBillId}, ${o.id})"" id=""btnInTem_${o.id}"" class=""text-xs bg-amber-500/20 hover:bg-amber-500/30 text-amber-300 border border-amber-500/40 px-2.5 py-1 rounded-lg active:scale-95 transition font-semibold flex items-center space-x-1 shadow-sm"" title=""Xem trước và in tem nhiệt bưu kiện""><span>🏷️ In tem</span></button>`);
        } else {
          actions.push(`<button onclick=""viewOrderLabel(${o.id})"" id=""btnInTem_${o.id}"" class=""text-xs bg-amber-500/20 hover:bg-amber-500/30 text-amber-300 border border-amber-500/40 px-2.5 py-1 rounded-lg active:scale-95 transition font-semibold flex items-center space-x-1 shadow-sm"" title=""Xem trước và in tem nhiệt đóng gói""><span>🏷️ In tem</span></button>`);
        }

        if (o.isDelivered) {
          actions.push(`<button onclick=""toggleDelivered(${o.id})"" class=""text-xs bg-sky-950 border border-sky-800/80 text-sky-300 px-2.5 py-1 rounded-lg active:bg-sky-900"" title=""Đã giao - Nhấn để hoàn tác"">🚚 Đã giao</button>`);
        } else {
          actions.push(`<button onclick=""toggleDelivered(${o.id})"" class=""text-xs bg-slate-800 border border-slate-700 text-slate-200 px-2.5 py-1 rounded-lg active:bg-slate-700"" title=""Chưa giao - Nhấn để đánh dấu ĐÃ GIAO"">🚚 Chưa giao</button>`);
        }
        if (o.customerBillId) {
          actions.push(`<button onclick=""viewBill(${o.customerBillId})"" class=""text-xs bg-sky-950 border border-sky-800/60 text-sky-300 px-2.5 py-1 rounded-lg active:bg-sky-900"">Xem Bill</button>`);
        }

        return `
          <div class=""bg-slate-900 border border-slate-800 rounded-xl p-3.5 space-y-2.5"">
            <div class=""flex justify-between items-start"">
              <div class=""flex items-center min-w-0"">
                ${o.hasThumbnail ? `
                  <div onclick=""viewThumbnail(${o.id})"" class=""w-12 h-12 shrink-0 rounded-lg overflow-hidden bg-slate-950 border border-slate-700/80 cursor-pointer active:scale-95 transition mr-2.5 flex items-center justify-center shadow-sm relative"">
                    <img src=""/api/orders/${o.id}/thumbnail?token=${encodeURIComponent(authToken)}"" class=""w-full h-full object-cover"" loading=""lazy"" onerror=""this.style.display='none'; if (this.nextElementSibling) this.nextElementSibling.style.display='flex';"" alt=""Thumb""/>
                    <div style=""display:none;"" class=""w-full h-full items-center justify-center bg-slate-900 text-slate-500 text-base"">🖼️</div>
                  </div>
                ` : ''}
                <div class=""min-w-0"">
                  <div class=""font-semibold text-sm text-slate-100 flex items-center space-x-1.5 truncate"">
                    <span class=""truncate"">${o.customerName}</span>
                  </div>
                  <div class=""text-[11px] text-slate-400 mt-0.5 font-mono truncate"">${o.orderCode || o.folderName}</div>
                </div>
              </div>
              <div class=""shrink-0 ml-2"">
                ${printBadge}
              </div>
            </div>

            ${noteHtml}

            <!-- Items -->
            <div class=""bg-slate-950/60 rounded-lg p-2.5 border border-slate-800/80"">
              ${itemsHtml}
            </div>

            ${deliveryInfoHtml}

            <!-- Action buttons -->
            <div class=""flex justify-between items-center pt-1"">
              <span class=""text-xs text-slate-400"">Tổng: <strong class=""text-slate-200"">${o.totalQuantity}</strong> tấm</span>
              <div class=""flex items-center space-x-1.5 flex-wrap gap-y-1 justify-end"">
                ${actions.join('')}
              </div>
            </div>
          </div>
        `;
      }).join('');
    }

    function filterOrders() {
      const q = removeDiacritics(document.getElementById('orderSearchInput').value.trim());
      let filtered = cachedOrders;

      if (currentFilterMode === 'printed') {
        filtered = filtered.filter(o => o.isPrinted);
      } else if (currentFilterMode === 'pending') {
        filtered = filtered.filter(o => !o.isPrinted);
      } else if (currentFilterMode === 'delivered') {
        filtered = filtered.filter(o => o.isDelivered);
      } else if (currentFilterMode === 'undelivered') {
        filtered = filtered.filter(o => !o.isDelivered);
      }

      if (q) {
        filtered = filtered.filter(o => 
          removeDiacritics(o.customerName).includes(q) ||
          removeDiacritics(o.orderCode).includes(q) ||
          removeDiacritics(o.folderName).includes(q) ||
          (o.note && removeDiacritics(o.note).includes(q)) ||
          (o.items && o.items.some(i => removeDiacritics(i.variantName).includes(q) || removeDiacritics(i.specName).includes(q)))
        );
      }
      renderOrders(filtered, false);
    }

    async function toggleDelivered(orderId) {
      const o = cachedOrders.find(x => x.id === orderId);
      if (!o) return;

      try {
        const res = await fetch('/api/orders/' + orderId + '/toggle-delivered', {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (res.ok) {
          const data = await res.json();
          o.syncState = data.syncState;
          o.isDelivered = data.isDelivered;
          o.deliveredAt = data.deliveredAt;
          o.deliveredBy = data.deliveredBy;
          if (navigator.vibrate) navigator.vibrate([50, 30, 50]);
          showToast(data.syncState === 'pending' ? 'Đã lưu local · Chờ Cloud xác nhận' : (o.isDelivered ? 'Đã đánh dấu ĐÃ GIAO' : 'Đã hoàn tác CHƯA GIAO'), true);
          renderOrders(cachedOrders);
        } else {
          showToast('Không thể cập nhật trạng thái giao hàng', false);
        }
      } catch (err) {
        showToast('Lỗi kết nối khi cập nhật trạng thái', false);
      }
    }

    async function togglePrinted(orderId) {
      const o = cachedOrders.find(x => x.id === orderId);
      if (!o) return;

      try {
        const res = await fetch('/api/orders/' + orderId + '/toggle-printed', {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (res.ok) {
          const data = await res.json();
          o.isPrinted = data.isPrinted;
          o.printedAt = data.printedAt;
          renderOrders(cachedOrders);
        }
      } catch (err) { }
    }

    let editingNoteOrderId = null;

    function openNoteModal(orderId) {
      const o = cachedOrders.find(x => x.id === orderId);
      if (!o) return;
      editingNoteOrderId = orderId;
      document.getElementById('noteModalSubtitle').innerText = o.customerName + ' (' + (o.orderCode || o.folderName) + ')';
      document.getElementById('noteInput').value = o.note || '';
      document.getElementById('noteModal').classList.remove('hidden');
      setTimeout(() => document.getElementById('noteInput').focus(), 100);
    }

    function closeNoteModal() {
      document.getElementById('noteModal').classList.add('hidden');
      editingNoteOrderId = null;
    }

    async function saveNote() {
      if (!editingNoteOrderId) return;
      const orderId = editingNoteOrderId;
      const o = cachedOrders.find(x => x.id === orderId);
      const text = document.getElementById('noteInput').value.trim();

      try {
        const res = await fetch('/api/orders/' + orderId + '/note', {
          method: 'POST',
          headers: {
            'Authorization': 'Bearer ' + authToken,
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ note: text })
        });
        if (res.ok) {
          const data = await res.json();
          if (o) o.note = data.note;
          closeNoteModal();
          if (navigator.vibrate) navigator.vibrate(50);
          showToast('Đã lưu ghi chú đơn hàng!', true);
          renderOrders(cachedOrders);
        } else {
          showToast('Không thể lưu ghi chú', false);
        }
      } catch (err) {
        showToast('Lỗi kết nối khi lưu ghi chú', false);
      }
    }

    function viewThumbnail(orderId) {
      const o = cachedOrders.find(x => x.id === orderId);
      if (!o) return;
      const modal = document.getElementById('previewModal');
      const body = document.getElementById('previewModalBody');
      document.getElementById('previewModalTitle').innerText = 'Ảnh đại diện đơn hàng';
      body.innerHTML = `
        <div class=""space-y-3"">
          <div>
            <div class=""text-base font-bold text-slate-100"">${o.customerName}</div>
            <div class=""text-xs text-slate-400 font-mono mt-0.5"">${o.orderCode || o.folderName}</div>
            ${o.thumbnailFileName ? `<div class=""text-[11px] text-sky-400 font-mono mt-0.5"">📁 ${o.thumbnailFileName}</div>` : ''}
          </div>
          <div class=""bg-black/90 rounded-xl overflow-hidden border border-slate-800 flex items-center justify-center p-2 min-h-[220px]"">
            <img src=""/api/orders/${orderId}/thumbnail?token=${encodeURIComponent(authToken)}"" class=""max-h-[50vh] w-auto max-w-full rounded object-contain"" alt=""Thumbnail""/>
          </div>
          <div class=""flex justify-between items-center pt-1"">
            <span class=""text-xs text-slate-400"">Tổng: <strong class=""text-slate-200"">${o.totalQuantity}</strong> tấm</span>
            <div class=""flex space-x-2"">
              <button onclick=""closePreviewModal(); openOrderGallery(${orderId});"" class=""text-xs text-white bg-indigo-600 hover:bg-indigo-500 font-semibold px-3 py-1 rounded-lg flex items-center space-x-1 shadow transition"">
                <span>🖼️ Xem toàn bộ ảnh</span>
              </button>
              <a href=""/api/orders/${orderId}/thumbnail?token=${encodeURIComponent(authToken)}"" target=""_blank"" class=""text-xs text-indigo-400 hover:text-indigo-300 flex items-center space-x-1 bg-indigo-950/60 border border-indigo-800/60 px-2.5 py-1 rounded"">
                <span>🔍 Mở ảnh</span>
              </a>
            </div>
          </div>
        </div>
      `;
      modal.classList.remove('hidden');
    }

    let currentGalleryOrderId = null;
    let currentGalleryVersion = null;
    let currentGalleryOffset = 0;
    let currentGalleryObserver = null;
    let currentGalleryTouched = new Set();
    let currentGalleryAbort = null;

    function abortGalleryRequests() {
      if (currentGalleryAbort) {
        currentGalleryAbort.abort();
        currentGalleryAbort = null;
      }
    }

    function closeGalleryModal() {
      const modal = document.getElementById('galleryModal');
      if (modal) modal.classList.add('hidden');
      abortGalleryRequests();
      if (currentGalleryObserver) {
        currentGalleryObserver.disconnect();
        currentGalleryObserver = null;
      }
      currentGalleryOrderId = null;
      currentGalleryVersion = null;
    }

    async function openOrderGallery(orderId) {
      abortGalleryRequests();
      currentGalleryAbort = new AbortController();
      currentGalleryOrderId = orderId;
      currentGalleryOffset = 0;
      currentGalleryTouched.clear();
      const modal = document.getElementById('galleryModal');
      const title = document.getElementById('galleryModalTitle');
      const subtitle = document.getElementById('galleryModalSubtitle');
      const body = document.getElementById('galleryModalBody');

      modal.classList.remove('hidden');
      title.innerText = 'Thư viện ảnh in';
      subtitle.innerText = 'Đang tải dữ liệu...';
      body.innerHTML = `
        <div class=""py-12 flex flex-col items-center justify-center space-y-3"">
          <div class=""w-8 h-8 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin""></div>
          <div class=""text-xs text-slate-400"">Đang đọc danh sách ảnh in từ kho dữ liệu...</div>
        </div>
      `;

      try {
        const res = await fetch(`/api/orders/${orderId}/gallery/requests`, {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken },
          signal: currentGalleryAbort.signal
        });
        if (currentGalleryOrderId !== orderId) return;
        if (!res.ok) {
          const err = await res.json().catch(() => ({}));
          body.innerHTML = `<div class=""p-4 text-center text-rose-400 text-sm"">${escapeHtml(err.message || 'Không thể tải thư viện ảnh.')}</div>`;
          return;
        }

        const manifest = await res.json();
        if (currentGalleryOrderId !== orderId) return;
        currentGalleryVersion = manifest.manifestVersion;
        title.innerText = `[${manifest.orderCode || ('Đơn #' + orderId)}]`;
        subtitle.innerText = `Tổng cộng: ${manifest.totalImageCount} ảnh`;

        if (manifest.totalImageCount === 0 && !manifest.hasUnresolvedFolders) {
          let noticeHtml = manifest.notice ? `<div class=""p-3 bg-amber-950/60 border border-amber-800/80 rounded-xl text-xs text-amber-200 mb-3"">⚠️ ${escapeHtml(manifest.notice)}</div>` : '';
          body.innerHTML = `
            ${noticeHtml}
            <div class=""py-12 text-center text-slate-400 text-sm"">
              Không có ảnh JPG/JPEG nào trong các thư mục in đã chọn.
            </div>
          `;
          return;
        }

        let noticeHtml = manifest.notice ? `<div class=""p-3 bg-amber-950/60 border border-amber-800/80 rounded-xl text-xs text-amber-200 mb-3"">⚠️ ${escapeHtml(manifest.notice)}</div>` : '';

        // Render groups container
        let groupsHtml = '';
        (manifest.groups || []).forEach(g => {
          let warnHtml = !g.isFolderResolved ? `<div class=""p-2 bg-rose-950/60 border border-rose-800/80 rounded text-[11px] text-rose-300 mb-2"">⚠️ ${escapeHtml(g.resolutionMessage || 'Chưa xác định')}</div>` : '';
          groupsHtml += `
            <div class=""bg-slate-950/80 rounded-xl border border-slate-800/80 p-3 mb-3"">
              <div class=""flex justify-between items-center mb-2.5 pb-1.5 border-b border-slate-800"">
                <div class=""min-w-0 pr-2"">
                  <span class=""font-bold text-xs text-slate-200"">${escapeHtml(g.specificationName || 'Quy cách')}</span>
                  <span class=""text-[11px] text-slate-400 block truncate"">${escapeHtml(g.folderName || '')}</span>
                </div>
                <span class=""text-[10px] font-semibold bg-slate-800 text-slate-300 px-2 py-0.5 rounded-full shrink-0"">${g.imageCount || 0} ảnh</span>
              </div>
              ${warnHtml}
              <div id=""gallery-group-${g.orderItemId}"" class=""grid grid-cols-3 gap-2""></div>
            </div>
          `;
        });

        body.innerHTML = `
          ${noticeHtml}
          <div>${groupsHtml}</div>
          <div id=""galleryLoadMoreContainer"" class=""py-3 text-center""></div>
        `;

        await loadGalleryPage(orderId, manifest.manifestVersion, 0);
      } catch (e) {
        if (e.name === 'AbortError' || currentGalleryOrderId !== orderId) return;
        body.innerHTML = `<div class=""p-4 text-center text-rose-400 text-sm"">Lỗi kết nối máy chủ: ${escapeHtml(e.message)}</div>`;
      }
    }

    async function loadGalleryPage(orderId, version, offset) {
      if (!currentGalleryAbort || version !== currentGalleryVersion || currentGalleryOrderId !== orderId) return;
      const signal = currentGalleryAbort.signal;
      const loadMoreContainer = document.getElementById('galleryLoadMoreContainer');
      if (loadMoreContainer) {
        loadMoreContainer.innerHTML = '<div class=""text-xs text-slate-400"">Đang tải ảnh...</div>';
      }

      try {
        const res = await fetch(`/api/orders/${orderId}/gallery?version=${encodeURIComponent(version)}&offset=${offset}&limit=24`, {
          headers: { 'Authorization': 'Bearer ' + authToken },
          signal: signal
        });
        if (version !== currentGalleryVersion || currentGalleryOrderId !== orderId) return;
        if (!res.ok) {
          if (loadMoreContainer) {
            loadMoreContainer.innerHTML = `<button onclick=""loadGalleryPage(${orderId}, '${version}', ${offset})"" class=""px-4 py-2 bg-rose-700 hover:bg-rose-600 text-white text-xs rounded-xl"">⚠️ Lỗi tải ảnh - Thử lại</button>`;
          }
          return;
        }

        const page = await res.json();
        currentGalleryOffset = offset + page.items.length;

        page.items.forEach(item => {
          const groupContainer = document.getElementById(`gallery-group-${item.orderItemId}`);
          if (groupContainer) {
            const card = document.createElement('div');
            card.className = 'aspect-square bg-slate-900 rounded-lg overflow-hidden border border-slate-800/80 flex flex-col items-center justify-between p-1 relative shadow-sm hover:border-slate-700 transition';
            card.setAttribute('data-file-id', item.fileId);

            const imgUrl = `/api/orders/${orderId}/gallery/images/${item.fileId}?v=${encodeURIComponent(version)}&token=${encodeURIComponent(authToken)}`;
            card.innerHTML = `
              <div class=""flex-1 w-full flex items-center justify-center overflow-hidden"">
                <img src=""${imgUrl}"" class=""w-full h-full object-contain"" loading=""lazy"" onerror=""this.onerror=null; this.src=''; this.classList.add('hidden'); this.nextElementSibling.classList.remove('hidden');"" alt=""${escapeHtml(item.fileName)}""/>
                <div class=""hidden text-rose-400 text-[10px] text-center p-1 font-mono"">⚠️ Lỗi</div>
              </div>
              <div class=""w-full text-center text-[10px] text-slate-300 font-mono truncate px-0.5 pt-0.5"" title=""${escapeHtml(item.fileName)}"">
                ${escapeHtml(item.fileName)}
              </div>
            `;
            groupContainer.appendChild(card);
          }
        });

        setupTouchObserver(orderId, version);

        if (loadMoreContainer) {
          if (page.hasMore) {
            loadMoreContainer.innerHTML = `
              <button onclick=""loadGalleryPage(${orderId}, '${version}', ${currentGalleryOffset})"" class=""px-5 py-2 bg-indigo-600 hover:bg-indigo-500 text-white font-medium text-xs rounded-xl shadow active:scale-95 transition"">
                ⬇ Tải tiếp 24 ảnh (${currentGalleryOffset}/${page.totalCount})
              </button>
            `;
          } else {
            loadMoreContainer.innerHTML = `<div class=""text-[11px] text-slate-500"">Đã hiển thị toàn bộ ${page.totalCount} ảnh</div>`;
          }
        }
      } catch (e) {
        if (e.name === 'AbortError') return;
        if (loadMoreContainer) {
          loadMoreContainer.innerHTML = `<button onclick=""loadGalleryPage(${orderId}, '${version}', ${offset})"" class=""px-4 py-2 bg-rose-700 hover:bg-rose-600 text-white text-xs rounded-xl"">⚠️ Lỗi tải ảnh: ${escapeHtml(e.message)} - Thử lại</button>`;
        }
      }
    }

    function setupTouchObserver(orderId, version) {
      if (!window.IntersectionObserver) return;
      if (currentGalleryObserver) {
        currentGalleryObserver.disconnect();
        currentGalleryObserver = null;
      }
      currentGalleryObserver = new IntersectionObserver((entries) => {
          const newlyViewed = [];
          entries.forEach(entry => {
            if (entry.isIntersecting) {
              const fileId = entry.target.getAttribute('data-file-id');
              if (fileId && !currentGalleryTouched.has(fileId)) {
                currentGalleryTouched.add(fileId);
                newlyViewed.push(fileId);
              }
            }
          });
          if (newlyViewed.length > 0) {
            fetch(`/api/orders/${orderId}/gallery/touch`, {
              method: 'POST',
              headers: { 'Authorization': 'Bearer ' + authToken, 'Content-Type': 'application/json' },
              body: JSON.stringify({ version: version, fileIds: newlyViewed })
            }).catch(() => {});
          }
        }, { threshold: 0.1 });

      document.querySelectorAll('#galleryModalBody [data-file-id]').forEach(el => {
        currentGalleryObserver.observe(el);
      });
    }

    async function viewBill(billId) {
      const modal = document.getElementById('previewModal');
      const body = document.getElementById('previewModalBody');
      document.getElementById('previewModalTitle').innerText = 'Hóa đơn #' + billId;
      body.innerHTML = '<div class=""text-center text-xs text-slate-500 py-6"">Đang tải hóa đơn...</div>';
      modal.classList.remove('hidden');

      try {
        const res = await fetch('/api/bills/' + billId, {
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (!res.ok) { body.innerHTML = '<div class=""text-rose-400 text-xs text-center py-4"">Không thể tải hóa đơn</div>'; return; }
        const b = await res.json();

        let financialHtml = '';
        if (b.grandTotal !== null && b.grandTotal !== undefined) {
          financialHtml = `
            <div class=""bg-sky-950/40 border border-sky-800/40 rounded-xl p-3 flex justify-between items-center"">
              <span class=""text-xs text-sky-300 font-medium"">Tổng thanh toán:</span>
              <span class=""text-lg font-bold text-sky-400"">${b.grandTotal.toLocaleString('vi-VN')} đ</span>
            </div>
          `;
        }

        let paymentHtml = '';
        if (currentRole === 'Admin') {
          paymentHtml = `
            <div class=""flex items-center justify-between bg-slate-950/80 border border-slate-800 rounded-xl p-3"">
              <div class=""flex items-center space-x-2"">
                <span class=""text-base"">${b.isPaid ? '🟢' : '⏳'}</span>
                <div>
                  <div class=""text-xs font-semibold ${b.isPaid ? 'text-emerald-400' : 'text-amber-400'}"">
                    ${b.isPaid ? 'ĐÃ THANH TOÁN' : 'CHƯA THANH TOÁN'} ${b.syncState === 'pending' ? '· Chờ Cloud xác nhận' : ''}
                  </div>
                  ${b.paidAt ? `<div class=""text-[10px] text-slate-400 font-mono"">Lúc: ${new Date(b.paidAt).toLocaleTimeString('vi-VN', {hour:'2-digit', minute:'2-digit', day:'2-digit', month:'2-digit'})}</div>` : ''}
                </div>
              </div>
              <button onclick=""toggleBillPaymentInModal(${b.id})"" class=""text-xs font-semibold px-3 py-1.5 rounded-lg active:scale-95 transition ${b.isPaid ? 'bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-700' : 'bg-emerald-600 hover:bg-emerald-500 text-white shadow-sm'}"">
                ${b.isPaid ? 'Hủy đã thu' : 'Xác nhận ĐÃ THU'}
              </button>
            </div>
          `;
        }

        const linesHtml = (Array.isArray(b.lines) ? b.lines : []).map(l => `
          <div class=""flex justify-between items-center text-xs py-1.5 border-b border-slate-800"">
            <div>
              <div class=""text-slate-200 font-medium"">${escapeHtml(l.description || '')}</div>
              <div class=""text-[11px] text-slate-400"">Kích thước: ${escapeHtml(l.size || '-')}</div>
            </div>
            <div class=""text-right"">
              <div class=""font-semibold text-slate-200"">x${l.quantity}</div>
              ${Number.isFinite(l.lineTotal) ? `<div class=""text-[11px] text-slate-400"">${l.lineTotal.toLocaleString('vi-VN')} đ</div>` : ''}
            </div>
          </div>
        `).join('');

        body.innerHTML = `
          <div class=""space-y-3"">
            <div>
              <div class=""text-base font-bold text-slate-100"">${escapeHtml(b.customerName || '')}</div>
              <div class=""text-xs text-slate-400"">Số HĐ: <strong class=""font-mono text-slate-300"">${escapeHtml(b.billNumber || '')}</strong></div>
              <div class=""text-xs text-slate-400"">Ngày: ${escapeHtml(b.periodEnd || '—')}</div>
            </div>

            ${financialHtml}
            ${paymentHtml}

            <div class=""bg-slate-950 rounded-xl p-3 border border-slate-800"">
              <div class=""text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2"">Bảng kê chi tiết</div>
              ${linesHtml}
            </div>

            ${currentRole === 'Admin' ? `<!-- Financial media -->
            <div class=""space-y-2 pt-2"">
              <button onclick=""viewLabel(${billId}, null, true)"" class=""w-full flex items-center justify-center space-x-1.5 bg-amber-500 hover:bg-amber-400 active:scale-95 text-slate-950 font-bold py-2.5 rounded-xl text-xs transition shadow-sm"">
                <span>🏷️ In tem giao hàng</span>
              </button>
${b.hasExportFile ? `              <a href=""/api/bills/${billId}/image?token=${authToken}"" target=""_blank"" class=""w-full flex items-center justify-center space-x-2 bg-sky-600 hover:bg-sky-500 text-white font-medium py-2.5 rounded-xl text-xs transition"">
                <span>Mở ảnh Hóa đơn (JPG)</span>
              </a>` : '<div>Chưa có ảnh hóa đơn.</div>'}
${b.hasPaymentQr ? `              <button onclick=""showBillQr(${billId})"" class=""w-full bg-slate-800 border border-slate-700 text-slate-300 font-medium py-2.5 rounded-xl text-xs"">
                Hiện mã VietQR thanh toán
              </button>` : ''}
            </div>
` : ''}
          </div>
        `;
      } catch (e) {
        body.innerHTML = '<div class=""text-rose-400 text-xs text-center py-4"">Lỗi khi tải hóa đơn</div>';
      }
    }

    async function toggleBillPaymentInModal(billId) {
      try {
        const res = await fetch('/api/bills/' + billId + '/toggle-payment', {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (res.ok) {
          const data = await res.json();
          if (navigator.vibrate) navigator.vibrate([60, 40, 60]);
          showToast(data.syncState === 'pending' ? 'Đã lưu local · Chờ Cloud xác nhận' : (data.isPaid ? 'Đã xác nhận thanh toán hóa đơn!' : 'Đã chuyển thành chưa thanh toán.'), true);
          viewBill(billId);
          if (!document.getElementById('debtsSection').classList.contains('hidden')) {
            loadDebts();
          }
        } else {
          const err = await res.json().catch(() => ({ message: 'Lỗi cập nhật' }));
          showToast(err.message || 'Không thể đổi trạng thái thanh toán', false);
        }
      } catch (e) {
        showToast('Lỗi kết nối khi cập nhật thanh toán: ' + e.message, false);
      }
    }

    function handleLabelImageError(img, fallbackOrderId) {
      if (fallbackOrderId && !img.dataset.fallback) {
        img.dataset.fallback = '1';
        img.src = '/api/orders/' + fallbackOrderId + '/label?token=' + encodeURIComponent(authToken);
        return;
      }
      const parent = img.parentElement;
      if (parent) {
        parent.innerHTML = '<div class=""text-xs text-rose-400 py-6 text-center font-medium"">Không thể tạo hình ảnh tem giao hàng.</div>';
      }
    }

    function viewLabel(billId, orderId, fromBill = false) {
      if (!orderId && typeof cachedOrders !== 'undefined' && Array.isArray(cachedOrders)) {
        const matchingOrder = cachedOrders.find(x => x.customerBillId === billId);
        if (matchingOrder) orderId = matchingOrder.id;
      }
      const modal = document.getElementById('previewModal');
      const body = document.getElementById('previewModalBody');
      document.getElementById('previewModalTitle').innerText = 'Xem trước tem giao hàng';
      modal.classList.remove('hidden');

      const printBtnHtml = `<button onclick=""printBillLabel(${billId}, this, ${orderId || 'null'})"" class=""py-2.5 bg-amber-500 hover:bg-amber-400 active:scale-95 transition text-slate-950 font-bold rounded-xl text-xs shadow-lg shadow-amber-950/50 flex items-center justify-center space-x-1""><span>🖨️ In tem</span></button>`;

      const backBtnHtml = fromBill
        ? `<button onclick=""viewBill(${billId})"" class=""py-2.5 bg-slate-800 hover:bg-slate-700 border border-slate-700 active:scale-95 transition text-slate-300 font-semibold rounded-xl text-xs flex items-center justify-center space-x-1""><span>← Quay lại Bill</span></button>`
        : `<button onclick=""closePreviewModal()"" class=""py-2.5 bg-slate-800 hover:bg-slate-700 border border-slate-700 active:scale-95 transition text-slate-300 font-semibold rounded-xl text-xs flex items-center justify-center""><span>Đóng</span></button>`;

      const labelUrl = `/api/bills/${billId}/label?token=${encodeURIComponent(authToken)}${orderId ? '&orderId=' + encodeURIComponent(orderId) : ''}`;

      body.innerHTML = `
        <div class=""flex flex-col items-center space-y-3"">
          <div class=""w-full flex justify-center bg-slate-950/60 rounded-xl p-2 border border-slate-800"">
            <img src=""${labelUrl}"" alt=""Tem giao hàng"" class=""w-full h-auto max-h-[55vh] object-contain rounded-lg border border-slate-700 max-w-full shadow-lg"" onerror=""handleLabelImageError(this, ${orderId || 'null'})"">
          </div>
          <div class=""text-[11px] text-slate-400 text-center flex items-center justify-center space-x-1"">
            <span>🏷️ Tem nhiệt bưu kiện</span>
            <span class=""text-sky-400 font-medium"">(Tự động đánh dấu ĐÃ GIAO)</span>
          </div>
          <div class=""w-full grid grid-cols-2 gap-2 pt-1"">
            ${backBtnHtml}
            ${printBtnHtml}
          </div>
        </div>
      `;
    }

    function viewOrderLabel(orderId) {
      const modal = document.getElementById('previewModal');
      const body = document.getElementById('previewModalBody');
      document.getElementById('previewModalTitle').innerText = 'Xem trước tem đóng gói';
      modal.classList.remove('hidden');

      const printBtnHtml = `<button onclick=""printOrderLabel(${orderId}, this)"" class=""py-2.5 bg-amber-500 hover:bg-amber-400 active:scale-95 transition text-slate-950 font-bold rounded-xl text-xs shadow-lg shadow-amber-950/50 flex items-center justify-center space-x-1""><span>🖨️ In tem</span></button>`;

      body.innerHTML = `
        <div class=""flex flex-col items-center space-y-3"">
          <div class=""w-full flex justify-center bg-slate-950/60 rounded-xl p-2 border border-slate-800"">
            <img src=""/api/orders/${orderId}/label?token=${encodeURIComponent(authToken)}"" alt=""Tem đơn hàng"" class=""w-full h-auto max-h-[55vh] object-contain rounded-lg border border-slate-700 max-w-full shadow-lg"" onerror=""handleLabelImageError(this, null)"">
          </div>
          <div class=""text-[11px] text-slate-400 text-center flex items-center justify-center space-x-1"">
            <span>🏷️ Tem nhiệt bưu kiện</span>
            <span class=""text-sky-400 font-medium"">(Tự động đánh dấu ĐÃ GIAO)</span>
          </div>
          <div class=""w-full grid grid-cols-2 gap-2 pt-1"">
            <button onclick=""closePreviewModal()"" class=""py-2.5 bg-slate-800 hover:bg-slate-700 border border-slate-700 active:scale-95 transition text-slate-300 font-semibold rounded-xl text-xs flex items-center justify-center"">
              <span>Đóng</span>
            </button>
            ${printBtnHtml}
          </div>
        </div>
      `;
    }

    function showToast(msg, isSuccess = true) {
      const toast = document.getElementById('actionToast');
      const text = document.getElementById('actionToastText');
      const icon = document.getElementById('actionToastIcon');
      if (!toast || !text) return;
      text.innerText = msg;
      icon.innerText = isSuccess ? '✅' : '❌';
      toast.className = 'fixed top-14 left-1/2 -translate-x-1/2 z-50 bg-slate-900/95 border ' + 
        (isSuccess ? 'border-emerald-500/70 shadow-emerald-950/80 text-emerald-300' : 'border-rose-500/70 shadow-rose-950/80 text-rose-300') + 
        ' shadow-2xl text-xs font-semibold px-4 py-2.5 rounded-full pointer-events-none transition-all duration-300 opacity-100 translate-y-0 flex items-center space-x-2 backdrop-blur';
      clearTimeout(window._actionToastTimer);
      window._actionToastTimer = setTimeout(() => {
        toast.classList.remove('opacity-100', 'translate-y-0');
        toast.classList.add('opacity-0', '-translate-y-2');
      }, 3500);
    }

    async function printOrderLabel(orderId, btn) {
      let originalHtml = '';
      if (btn) {
        originalHtml = btn.innerHTML;
        btn.innerHTML = '<span>⏳ Đang in...</span>';
        btn.disabled = true;
      }

      try {
        const res = await fetch('/api/orders/' + orderId + '/print-label', {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken }
        });

        if (res.ok) {
          const data = await res.json();
          if (navigator.vibrate) navigator.vibrate([60, 40, 60]);
          showToast(data.message || 'Đã in tem thành công!', true);

          if (data.isDelivered) {
            const o = cachedOrders.find(x => x.id === orderId);
            if (o) {
              o.isDelivered = true;
              o.deliveredAt = data.deliveredAt;
              o.deliveredBy = data.deliveredBy;
              if (typeof filterOrders === 'function') filterOrders();
              else renderOrders(cachedOrders);
            }
          }
          closePreviewModal();
        } else {
          const err = await res.json().catch(() => ({ message: 'Lỗi server khi in tem' }));
          showToast(err.message || err.detail || 'Không thể in tem', false);
        }
      } catch (e) {
        showToast('Lỗi kết nối khi gửi lệnh in: ' + e.message, false);
      } finally {
        if (btn) {
          btn.innerHTML = originalHtml;
          btn.disabled = false;
        }
      }
    }

    async function printBillLabel(billId, btn, orderId) {
      let originalHtml = '';
      if (btn) {
        originalHtml = btn.innerHTML;
        btn.innerHTML = '<span>⏳ Đang in...</span>';
        btn.disabled = true;
      }

      try {
        const printUrl = '/api/bills/' + billId + '/print' + (orderId ? '?orderId=' + encodeURIComponent(orderId) : '');
        const res = await fetch(printUrl, {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken }
        });

        if (res.ok) {
          const data = await res.json();
          if (navigator.vibrate) navigator.vibrate([60, 40, 60]);
          showToast(data.message || 'Đã in tem hóa đơn thành công!', true);

          if (data.isDelivered) {
            const billIdNum = Number(billId);
            cachedOrders.forEach(o => {
              if ((orderId && o.id === orderId) || (o.customerBillId && o.customerBillId === billIdNum)) {
                o.isDelivered = true;
                o.deliveredAt = data.deliveredAt;
                o.deliveredBy = data.deliveredBy;
              }
            });
            if (typeof filterOrders === 'function') filterOrders();
            else renderOrders(cachedOrders);
          } else if (!orderId) {
            loadOrders();
          }
          closePreviewModal();
        } else {
          const err = await res.json().catch(() => ({ message: 'Lỗi server khi in tem' }));
          showToast(err.message || err.detail || 'Không thể in tem hóa đơn', false);
        }
      } catch (e) {
        showToast('Lỗi kết nối khi gửi lệnh in: ' + e.message, false);
      } finally {
        if (btn) {
          btn.innerHTML = originalHtml;
          btn.disabled = false;
        }
      }
    }

    function showBillQr(billId) {
      const modal = document.getElementById('previewModal');
      const body = document.getElementById('previewModalBody');
      document.getElementById('previewModalTitle').innerText = 'Mã VietQR chuyển khoản';
      modal.classList.remove('hidden');

      body.innerHTML = `
        <div class=""flex flex-col items-center space-y-3"">
          <img src=""/api/bills/${billId}/qr?token=${authToken}"" alt=""Mã VietQR"" class=""w-64 h-64 rounded-xl border border-slate-700 shadow-xl bg-white p-2"">
          <div class=""text-xs text-slate-300 font-medium text-center"">Quét bằng ứng dụng ngân hàng để thanh toán</div>
        </div>
      `;
    }

    function closePreviewModal() {
      document.getElementById('previewModal').classList.add('hidden');
    }

    // Switch Tabs
    function switchTab(tab) {
      document.getElementById('ordersSection').classList.add('hidden');
      document.getElementById('reportsSection').classList.add('hidden');
      document.getElementById('debtsSection').classList.add('hidden');

      document.getElementById('tabOrdersBtn').classList.remove('tab-active', 'text-slate-400');
      document.getElementById('tabReportsBtn').classList.remove('tab-active', 'text-slate-400');
      document.getElementById('tabDebtsBtn').classList.remove('tab-active', 'text-slate-400');

      if (tab === 'orders') {
        document.getElementById('ordersSection').classList.remove('hidden');
        document.getElementById('tabOrdersBtn').classList.add('tab-active');
        document.getElementById('tabReportsBtn').classList.add('text-slate-400');
        document.getElementById('tabDebtsBtn').classList.add('text-slate-400');
      } else if (tab === 'reports') {
        document.getElementById('reportsSection').classList.remove('hidden');
        document.getElementById('tabReportsBtn').classList.add('tab-active');
        document.getElementById('tabOrdersBtn').classList.add('text-slate-400');
        document.getElementById('tabDebtsBtn').classList.add('text-slate-400');
        loadMonthlyReport();
      } else if (tab === 'debts') {
        document.getElementById('debtsSection').classList.remove('hidden');
        document.getElementById('tabDebtsBtn').classList.add('tab-active');
        document.getElementById('tabOrdersBtn').classList.add('text-slate-400');
        document.getElementById('tabReportsBtn').classList.add('text-slate-400');
        loadDebts();
      }
    }

    async function loadMonthlyReport() {
      const monthVal = document.getElementById('reportMonthInput').value;
      if (!monthVal) return;
      const [year, month] = monthVal.split('-');

      try {
        const res = await fetch(`/api/reports/monthly?year=${year}&month=${month}`, {
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (!res.ok) return;
        const rep = await res.json();

        document.getElementById('repTotalRevenue').innerText = rep.totalAmount.toLocaleString('vi-VN') + ' đ';
        document.getElementById('repTotalOrdersCount').innerText = rep.totalOrders + ' đơn hàng';
        document.getElementById('repTotalPrintCount').innerText = rep.totalBillQuantity.toLocaleString('vi-VN') + ' tấm';
        document.getElementById('repTotalCustomers').innerText = rep.totalCustomers + ' khách hàng';

        const specListEl = document.getElementById('repSpecList');
        if (rep.specificationSummaries && rep.specificationSummaries.length > 0) {
          specListEl.innerHTML = rep.specificationSummaries.map(s => `
            <div class=""flex justify-between items-center text-xs py-1.5 border-b border-slate-800 last:border-0"">
              <span class=""text-slate-300 font-medium"">${s.specificationName}</span>
              <div class=""text-right"">
                <span class=""font-semibold text-slate-100"">${s.totalBillQuantity} tấm</span>
                <div class=""text-[11px] text-slate-400"">${s.totalAmount.toLocaleString('vi-VN')} đ</div>
              </div>
            </div>
          `).join('');
        } else {
          specListEl.innerHTML = '<div class=""text-xs text-slate-500 py-3 text-center"">Chưa có dữ liệu quy cách in</div>';
        }
      } catch (e) { }
    }

    async function loadDebts() {
      const listEl = document.getElementById('debtsList');
      listEl.innerHTML = '<div class=""text-center text-xs text-slate-500 py-6"">Đang tải sổ nợ...</div>';

      try {
        const res = await fetch('/api/customers/unpaid', {
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (!res.ok) return;
        const debts = await res.json();

        let totalSum = 0;
        debts.forEach(d => totalSum += d.totalDebt);
        document.getElementById('debtsTotalSum').innerText = totalSum.toLocaleString('vi-VN') + ' đ';

        if (debts.length === 0) {
          listEl.innerHTML = '<div class=""text-center text-xs text-emerald-400 py-6"">Tuyệt vời! Hiện không có công nợ tồn đọng.</div>';
          return;
        }

        listEl.innerHTML = debts.map(c => `
          <div class=""bg-slate-900 border border-slate-800 rounded-xl p-3.5 space-y-2"">
            <div class=""flex justify-between items-start"">
              <div>
                <div class=""font-semibold text-sm text-slate-100"">${c.customerName}</div>
                <div class=""text-[11px] text-slate-400"">${c.unpaidBillCount} hóa đơn chưa trả</div>
              </div>
              <div class=""text-right font-bold text-amber-400 text-sm"">
                ${c.totalDebt.toLocaleString('vi-VN')} đ
              </div>
            </div>

            <div class=""pt-1 space-y-1.5"">
              ${c.bills.map(b => `
                <div class=""flex justify-between items-center text-xs bg-slate-950/60 p-2 rounded-lg border border-slate-800/80"">
                  <div>
                    <span class=""font-mono text-slate-300 font-medium"">${b.billNumber}</span>
                    <span class=""text-[11px] text-slate-500 ml-1.5"">(${b.date})</span>
                  </div>
                  <div class=""flex items-center space-x-1.5"">
                    <span class=""font-semibold text-slate-200"">${b.grandTotal.toLocaleString('vi-VN')} đ</span>
                    <button onclick=""showBillQr(${b.id})"" class=""text-[11px] bg-slate-800 text-sky-300 px-2 py-1 rounded hover:bg-slate-700"" title=""Xem mã VietQR"">QR</button>
                    <button onclick=""viewBill(${b.id})"" class=""text-[11px] bg-slate-800 text-slate-300 px-2 py-1 rounded hover:bg-slate-700"" title=""Xem hóa đơn"">Xem</button>
                    <button onclick=""quickCollectDebt(${b.id})"" class=""text-[11px] bg-emerald-600 hover:bg-emerald-500 text-white font-medium px-2 py-1 rounded active:scale-95 transition"" title=""Xác nhận đã thu tiền"">Đã thu</button>
                  </div>
                </div>
              `).join('')}
            </div>
          </div>
        `).join('');
      } catch (e) {
        listEl.innerHTML = '<div class=""text-center text-xs text-rose-400 py-6"">Không thể tải sổ nợ</div>';
      }
    }

    async function quickCollectDebt(billId) {
      if (!confirm('Xác nhận đã thu tiền cho hóa đơn này?')) return;
      try {
        const res = await fetch('/api/bills/' + billId + '/toggle-payment', {
          method: 'POST',
          headers: { 'Authorization': 'Bearer ' + authToken }
        });
        if (res.ok) {
          const data = await res.json();
          if (navigator.vibrate) navigator.vibrate([60, 40, 60]);
          showToast(data.syncState === 'pending' ? 'Đã lưu local · Chờ Cloud xác nhận' : 'Đã ghi nhận thanh toán hóa đơn!', true);
          loadDebts();
        } else {
          showToast('Không thể cập nhật hóa đơn', false);
        }
      } catch (e) {
        showToast('Lỗi kết nối: ' + e.message, false);
      }
    }
  </script>
</body>
</html>
";
    }
}
