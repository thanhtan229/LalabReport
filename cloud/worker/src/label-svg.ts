import { CloudOrderDto, CloudBillDto } from './types';

function escapeXml(unsafe: string | null | undefined): string {
  if (!unsafe) return '';
  return String(unsafe)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function formatVnd(amount: number): string {
  return amount.toLocaleString('vi-VN') + ' đ';
}

/**
 * Renders a crisp 800x480 SVG thermal label for an order (packaging slip, no financial info).
 */
export function renderOrderLabelSvg(order: CloudOrderDto, workshopName = 'XƯỞNG IN ẢNH CHUYÊN NGHIỆP'): string {
  const code = order.orderCode || `ORD-${order.id}`;
  const customerName = order.customerName || order.folderName || 'Khách hàng';
  const totalQty = order.totalQuantity || (order.items?.reduce((sum, it) => sum + (it.billQuantity || it.printCount || 0), 0) ?? 0);
  const dateStr = order.workDate ? `Ngày: ${escapeXml(order.workDate)}` : new Date().toLocaleDateString('vi-VN');

  // Group raw items by variantName + size to match desktop thermal print logic
  const itemMap = new Map<string, { desc: string; qty: number }>();
  for (const it of (order.items || [])) {
    const spec = it.variantName || it.specName || 'In ảnh';
    const key = `${spec}|${it.size || ''}`;
    const qty = it.billQuantity || it.printCount || 0;
    const existing = itemMap.get(key);
    if (existing) {
      existing.qty += qty;
    } else {
      const desc = `${spec}${it.size ? ` (${it.size})` : ''}`;
      itemMap.set(key, { desc, qty });
    }
  }

  const groupedItems = Array.from(itemMap.values());
  const displayItems = groupedItems.slice(0, 3);
  const moreCount = Math.max(0, groupedItems.length - displayItems.length);

  let itemsSvg = '';
  let y = 295;
  for (const it of displayItems) {
    itemsSvg += `
      <text x="490" y="${y}" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="600" fill="#000">• ${escapeXml(it.desc)}</text>
      <text x="760" y="${y}" text-anchor="end" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="bold" fill="#000">x${it.qty}</text>
    `;
    y += 22;
  }
  if (moreCount > 0) {
    itemsSvg += `
      <text x="490" y="${y}" font-family="Segoe UI, -apple-system, sans-serif" font-size="12" font-style="italic" fill="#555">... và ${moreCount} loại khác</text>
    `;
  }

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 480" width="800" height="480">
  <rect x="0" y="0" width="800" height="480" fill="#ffffff"/>
  <rect x="12" y="10" width="776" height="460" rx="8" fill="none" stroke="#000000" stroke-width="2.5"/>

  <!-- Header -->
  <text x="28" y="38" font-family="Segoe UI, -apple-system, sans-serif" font-size="17" font-weight="bold" fill="#000">${escapeXml(workshopName.toUpperCase())}</text>
  <text x="770" y="38" text-anchor="end" font-family="Segoe UI, -apple-system, sans-serif" font-size="18" font-weight="bold" fill="#000">TEM ĐÓNG GÓI</text>

  <line x1="12" y1="52" x2="788" y2="52" stroke="#000000" stroke-width="1.5"/>

  <!-- Left Column: Recipient Info -->
  <text x="28" y="80" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#64748b">MÃ ĐƠN: <tspan fill="#000" font-size="15">${escapeXml(code)}</tspan></text>
  <text x="28" y="112" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#64748b">NGƯỜI NHẬN:</text>
  <text x="28" y="142" font-family="Segoe UI, -apple-system, sans-serif" font-size="22" font-weight="bold" fill="#000">${escapeXml(customerName)}</text>

  <rect x="24" y="162" width="435" height="248" rx="4" fill="#f8fafc" stroke="#cbd5e1" stroke-width="1"/>
  <text x="38" y="188" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#475569">THÔNG TIN BÀN GIAO:</text>
  <text x="38" y="218" font-family="Segoe UI, -apple-system, sans-serif" font-size="16" font-weight="600" fill="#000">Nhận tại xưởng / Giao theo hẹn</text>
  <text x="38" y="246" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" fill="#64748b">${dateStr}</text>

  ${order.note ? `
    <line x1="38" y1="270" x2="445" y2="270" stroke="#cbd5e1" stroke-width="1"/>
    <text x="38" y="295" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#b45309">GHI CHÚ:</text>
    <text x="38" y="322" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="bold" fill="#000">${escapeXml(order.note)}</text>
  ` : `
    <line x1="38" y1="270" x2="445" y2="270" stroke="#cbd5e1" stroke-width="1"/>
    <text x="38" y="295" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#64748b">LƯU Ý:</text>
    <text x="38" y="322" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-style="italic" fill="#475569">Vui lòng kiểm đếm kỹ số lượng khi nhận hàng.</text>
  `}

  <!-- Divider between left and right -->
  <line x1="468" y1="52" x2="468" y2="425" stroke="#cbd5e1" stroke-width="1.2"/>

  <!-- Right Column Upper: Packaging Notice Box -->
  <rect x="475" y="70" width="300" height="135" rx="6" fill="#f8fafc" stroke="#000000" stroke-width="2.5"/>
  <text x="625" y="108" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="17" font-weight="bold" fill="#000">📦 TEM ĐÓNG GÓI</text>
  <text x="625" y="136" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#333">ĐÃ KIỂM TRA ĐỦ SẢN PHẨM</text>
  <text x="625" y="168" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="16" font-weight="bold" fill="#000">Tổng SL: ${totalQty} tấm</text>

  <!-- Right Column Lower: Items -->
  <rect x="475" y="215" width="300" height="195" rx="6" fill="#f8fafc" stroke="#94a3b8" stroke-width="1"/>
  <text x="490" y="240" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#000">SẢN PHẨM IN:</text>
  <line x1="475" y1="252" x2="775" y2="252" stroke="#e2e8f0" stroke-width="1"/>

  ${itemsSvg}

  <!-- Bottom Quantity -->
  <line x1="475" y1="365" x2="775" y2="365" stroke="#000000" stroke-width="1"/>
  <text x="490" y="392" font-family="Segoe UI, -apple-system, sans-serif" font-size="15" font-weight="bold" fill="#000">Tổng SL: ${totalQty} sản phẩm</text>

  <!-- Footer -->
  <text x="28" y="445" font-family="Segoe UI, -apple-system, sans-serif" font-size="12" fill="#64748b">Lalab Auto Report · Tem đóng gói nhiệt</text>
</svg>`;
}

/**
 * Renders a crisp 800x480 SVG thermal shipping label for a bill (package slip with COD / payment status).
 */
export function renderBillLabelSvg(bill: CloudBillDto, workshopName = 'XƯỞNG IN ẢNH CHUYÊN NGHIỆP'): string {
  const billNumber = bill.billNumber || `HD-${bill.id}`;
  const customerName = bill.customerNameSnapshot || 'Khách hàng';
  const totalAmount = bill.totalAmount || (bill as any).grandTotal || 0;
  const isPaid = Boolean(bill.isPaid);

  const lines = (bill.lines || []).slice(0, 3);
  const totalPrintCount = (bill.lines || []).reduce((sum, l) => sum + (l.quantity || 0), 0);
  const moreCount = Math.max(0, (bill.lines?.length || 0) - lines.length);

  let itemsSvg = '';
  let y = 295;
  for (const l of lines) {
    const desc = escapeXml(`${l.description || 'Sản phẩm'}${l.size ? ` (${l.size})` : ''}`);
    const qty = l.quantity || 0;
    itemsSvg += `
      <text x="490" y="${y}" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="600" fill="#000">• ${desc}</text>
      <text x="760" y="${y}" text-anchor="end" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="bold" fill="#000">x${qty}</text>
    `;
    y += 22;
  }
  if (moreCount > 0) {
    itemsSvg += `
      <text x="490" y="${y}" font-family="Segoe UI, -apple-system, sans-serif" font-size="12" font-style="italic" fill="#555">... và ${moreCount} loại khác</text>
    `;
  }

  // Payment COD block
  const paymentBoxSvg = isPaid
    ? `
      <rect x="475" y="70" width="300" height="135" rx="6" fill="#f1f5f9" stroke="#000000" stroke-width="2.5"/>
      <text x="625" y="105" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="17" font-weight="bold" fill="#000">✓ ĐÃ THANH TOÁN (PAID)</text>
      <text x="625" y="132" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#333">KHÔNG THU TIỀN NGƯỜI NHẬN</text>
      <text x="625" y="165" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="16" font-weight="bold" fill="#000">(COD = 0 đ)</text>
    `
    : `
      <rect x="475" y="70" width="300" height="135" rx="6" fill="#ffffff" stroke="#000000" stroke-width="2.5"/>
      <text x="490" y="98" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="bold" fill="#000">TIỀN THU HỘ (COD):</text>
      <text x="625" y="142" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="${totalAmount >= 10000000 ? '25' : '30'}" font-weight="bold" fill="#000">${formatVnd(totalAmount)}</text>
      <text x="625" y="180" text-anchor="middle" font-family="Segoe UI, -apple-system, sans-serif" font-size="11" font-style="italic" fill="#333">(Shipper thu đủ tiền khi giao hàng)</text>
    `;

  const phone = (bill as any).phone || (bill as any).phoneSnapshot;
  const address = (bill as any).shippingAddress || (bill as any).shippingAddressSnapshot;
  const note = (bill as any).note;

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 480" width="800" height="480">
  <rect x="0" y="0" width="800" height="480" fill="#ffffff"/>
  <rect x="12" y="10" width="776" height="460" rx="8" fill="none" stroke="#000000" stroke-width="2.5"/>

  <!-- Header -->
  <text x="28" y="38" font-family="Segoe UI, -apple-system, sans-serif" font-size="17" font-weight="bold" fill="#000">${escapeXml(workshopName.toUpperCase())}</text>
  <text x="770" y="38" text-anchor="end" font-family="Segoe UI, -apple-system, sans-serif" font-size="18" font-weight="bold" fill="#000">TEM GIAO HÀNG</text>

  <line x1="12" y1="52" x2="788" y2="52" stroke="#000000" stroke-width="1.5"/>

  <!-- Left Column: Recipient Info -->
  <text x="28" y="80" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#64748b">SỐ HÓA ĐƠN: <tspan fill="#000" font-size="15">${escapeXml(billNumber)}</tspan></text>
  <text x="28" y="112" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#64748b">NGƯỜI NHẬN:</text>
  <text x="28" y="142" font-family="Segoe UI, -apple-system, sans-serif" font-size="22" font-weight="bold" fill="#000">${escapeXml(customerName)}</text>

  <rect x="24" y="162" width="435" height="248" rx="4" fill="#f8fafc" stroke="#cbd5e1" stroke-width="1"/>
  <text x="38" y="188" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#475569">${phone ? `SĐT: ${escapeXml(phone)}` : 'ĐỊA CHỈ NHẬN HÀNG (IN TEM DÁN):'}</text>
  <text x="38" y="218" font-family="Segoe UI, -apple-system, sans-serif" font-size="16" font-weight="600" fill="#000">${address ? escapeXml(address) : 'Địa chỉ giao theo thông tin khách hàng'}</text>
  <text x="38" y="246" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" fill="#64748b">Kỳ tính: ${escapeXml(bill.periodDate || 'Mặc định')}</text>

  ${note ? `
    <line x1="38" y1="270" x2="445" y2="270" stroke="#cbd5e1" stroke-width="1"/>
    <text x="38" y="295" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#b45309">GHI CHÚ:</text>
    <text x="38" y="322" font-family="Segoe UI, -apple-system, sans-serif" font-size="14" font-weight="bold" fill="#000">${escapeXml(note)}</text>
  ` : ''}

  <!-- Divider between left and right -->
  <line x1="468" y1="52" x2="468" y2="425" stroke="#cbd5e1" stroke-width="1.2"/>

  <!-- Right Column Upper: Payment/COD -->
  ${paymentBoxSvg}

  <!-- Right Column Lower: Items -->
  <rect x="475" y="215" width="300" height="195" rx="6" fill="#f8fafc" stroke="#94a3b8" stroke-width="1"/>
  <text x="490" y="240" font-family="Segoe UI, -apple-system, sans-serif" font-size="13" font-weight="bold" fill="#000">SẢN PHẨM IN:</text>
  <line x1="475" y1="252" x2="775" y2="252" stroke="#e2e8f0" stroke-width="1"/>

  ${itemsSvg}

  <!-- Bottom Quantity -->
  <line x1="475" y1="365" x2="775" y2="365" stroke="#000000" stroke-width="1"/>
  <text x="490" y="392" font-family="Segoe UI, -apple-system, sans-serif" font-size="15" font-weight="bold" fill="#000">Tổng SL: ${totalPrintCount} sản phẩm</text>

  <!-- Footer -->
  <text x="28" y="445" font-family="Segoe UI, -apple-system, sans-serif" font-size="12" fill="#64748b">Lalab Auto Report · Tem bưu kiện nhiệt</text>
</svg>`;
}
