import { readFileSync } from 'node:fs';
import { URL } from 'node:url';
import { Script, runInNewContext } from 'node:vm';
import { describe, it, expect } from 'vitest';
import { toMobileBill } from '../src/mobile-bill';
import { CloudBillDto } from '../src/types';

const html = readFileSync(new URL('../public/index.html', import.meta.url), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)![1];
const viewBill = script.slice(script.indexOf('    async function viewBill('), script.indexOf('    async function toggleBillPaymentInModal'));
const escapeHtml = script.slice(script.indexOf('    function escapeHtml('), script.indexOf('    function escapeHtml(') + script.slice(script.indexOf('    function escapeHtml(')).indexOf('\n    }') + 6);

function createMockDom() {
  const body = { innerHTML: '' };
  const title = { innerText: '' };
  const modal = { classList: { remove() {} } };
  const document = {
    getElementById(id: string) {
      return id === 'previewModalBody' ? body : id === 'previewModal' ? modal : title;
    }
  };
  return { body, title, document };
}

describe('PWA viewBill rendering', () => {
  it('parses the complete deployed PWA script', () => {
    expect(() => new Script(script)).not.toThrow();
  });

  it.each(['Admin', 'Staff'] as const)('renders representative 75,000 VND bill for %s without undefined or financial leakage', async role => {
    const bill: CloudBillDto = {
      id: 1,
      billNumber: 'AUDIT-1',
      billType: 'Customer',
      customerNameSnapshot: 'Audit Customer',
      periodDate: '2026-10-02',
      totalAmount: 75000,
      isPaid: false,
      hasJpeg: false,
      lines: [{ id: 1, description: 'In ảnh 10x15', size: '10x15', quantity: 15, unitPrice: 5000, lineTotal: 75000 }],
      adjustments: []
    };
    const { body, document } = createMockDom();
    await runInNewContext(`let currentRole=${JSON.stringify(role)}, authToken='fixture'; ${escapeHtml}\n${viewBill}\nviewBill(1)`, {
      document,
      fetch: async () => ({ ok: true, json: async () => toMobileBill(bill, role) })
    });

    // Universal data
    expect(body.innerHTML).toContain('Audit Customer');
    expect(body.innerHTML).toContain('AUDIT-1');
    expect(body.innerHTML).toContain('2026-10-02');
    expect(body.innerHTML).toContain('In ảnh 10x15');
    expect(body.innerHTML).toContain('10x15');
    expect(body.innerHTML).toContain('x15');
    expect(body.innerHTML).not.toContain('undefined');
    expect(body.innerHTML).not.toContain('NaN');

    if (role === 'Admin') {
      // Admin sees financial totals, line totals, and payment actions
      expect(body.innerHTML).toContain('75.000');
      expect(body.innerHTML).toContain('CHƯA THANH TOÁN');
      expect(body.innerHTML).toContain('Xác nhận ĐÃ THU');
      expect(body.innerHTML).toContain('toggleBillPaymentInModal(1)');
    } else {
      // Staff has financial amounts completely redacted and no payment actions
      expect(body.innerHTML).not.toContain('75.000');
      expect(body.innerHTML).not.toContain('5.000');
      expect(body.innerHTML).not.toContain('Tổng thanh toán:');
      expect(body.innerHTML).not.toContain('Xác nhận ĐÃ THU');
      expect(body.innerHTML).not.toContain('toggleBillPaymentInModal');
      expect(body.innerHTML).not.toContain('/api/bills/1/image');
      expect(body.innerHTML).not.toContain('showBillQr');
    }
  });

  it('renders paid bill state with timestamp and cancel payment action for Admin', async () => {
    const bill: CloudBillDto = {
      id: 2,
      billNumber: 'PAID-2',
      billType: 'Customer',
      customerNameSnapshot: 'Paid Customer',
      periodDate: '2026-10-02',
      totalAmount: 120000,
      isPaid: true,
      paidAt: '2026-10-02T15:30:00',
      hasJpeg: false,
      lines: [{ id: 1, description: 'Album', size: '20x30', quantity: 1, unitPrice: 120000, lineTotal: 120000 }],
      adjustments: []
    };
    const { body, document } = createMockDom();
    await runInNewContext(`let currentRole='Admin', authToken='fixture'; ${escapeHtml}\n${viewBill}\nviewBill(2)`, {
      document,
      fetch: async () => ({ ok: true, json: async () => toMobileBill(bill, 'Admin') })
    });

    expect(body.innerHTML).toContain('ĐÃ THANH TOÁN');
    expect(body.innerHTML).toContain('Hủy đã thu');
    expect(body.innerHTML).toContain('120.000');
    expect(body.innerHTML).not.toContain('undefined');
    expect(body.innerHTML).not.toContain('NaN');
  });

  it('renders export JPEG link and VietQR button when available for Admin only', async () => {
    // Both JPEG and QR present in contract
    const billData = {
      id: 3,
      billNumber: 'MEDIA-3',
      billType: 'Customer',
      customerName: 'Media Customer',
      phone: null,
      shippingAddress: null,
      periodStart: null,
      periodEnd: '2026-10-02',
      status: 'Exported',
      isPaid: false,
      paidAt: null,
      note: null,
      hasExportFile: true,
      hasPaymentQr: true,
      productSubtotal: 75000,
      adjustmentsTotal: 0,
      grandTotal: 75000,
      lines: [{ id: 1, description: 'Photo', size: '10x15', quantity: 15, unitPrice: 5000, lineTotal: 75000 }],
      adjustments: []
    };

    // Admin view
    const adminDom = createMockDom();
    await runInNewContext(`let currentRole='Admin', authToken='my-jwt'; ${escapeHtml}\n${viewBill}\nviewBill(3)`, {
      document: adminDom.document,
      fetch: async () => ({ ok: true, json: async () => billData })
    });
    expect(adminDom.body.innerHTML).toContain('/api/bills/3/image?token=my-jwt');
    expect(adminDom.body.innerHTML).toContain('Mở ảnh Hóa đơn (JPG)');
    expect(adminDom.body.innerHTML).toContain('showBillQr(3)');
    expect(adminDom.body.innerHTML).toContain('Hiện mã VietQR thanh toán');

    // Staff view with same media-ready bill
    const staffBillData = {
      ...billData,
      hasExportFile: false,
      hasPaymentQr: false,
      grandTotal: null,
      productSubtotal: null,
      adjustmentsTotal: null,
      lines: [{ ...billData.lines[0], unitPrice: null, lineTotal: null }],
      adjustments: null
    };
    const staffDom = createMockDom();
    await runInNewContext(`let currentRole='Staff', authToken='staff-jwt'; ${escapeHtml}\n${viewBill}\nviewBill(3)`, {
      document: staffDom.document,
      fetch: async () => ({ ok: true, json: async () => staffBillData })
    });
    expect(staffDom.body.innerHTML).not.toContain('/api/bills/3/image');
    expect(staffDom.body.innerHTML).not.toContain('showBillQr');
    expect(staffDom.body.innerHTML).not.toContain('undefined');
  });

  it('renders safely with missing/null data without NaN or undefined', async () => {
    const minimalBill = {
      id: 4,
      billNumber: null,
      customerName: null,
      periodEnd: null,
      isPaid: false,
      paidAt: null,
      hasExportFile: false,
      hasPaymentQr: false,
      grandTotal: null,
      lines: []
    };
    const { body, document } = createMockDom();
    await runInNewContext(`let currentRole='Staff', authToken='fixture'; ${escapeHtml}\n${viewBill}\nviewBill(4)`, {
      document,
      fetch: async () => ({ ok: true, json: async () => minimalBill })
    });

    expect(body.innerHTML).toContain('Ngày: —');
    expect(body.innerHTML).not.toContain('undefined');
    expect(body.innerHTML).not.toContain('NaN');
    expect(body.innerHTML).not.toContain('null');
  });

  it('escapes unsafe customer and line text to prevent XSS', async () => {
    const bill: CloudBillDto = {
      id: 5,
      billNumber: '<b>XSS-1</b>',
      billType: 'Customer',
      customerNameSnapshot: '<script>alert("xss")</script> & "Quotes"',
      periodDate: '2026-10-02',
      totalAmount: 75000,
      isPaid: false,
      hasJpeg: false,
      lines: [{ id: 1, description: '<img src=x onerror=1>', size: '<large>', quantity: 1, unitPrice: 75000, lineTotal: 75000 }],
      adjustments: []
    };
    const { body, document } = createMockDom();
    await runInNewContext(`let currentRole='Admin', authToken='fixture'; ${escapeHtml}\n${viewBill}\nviewBill(5)`, {
      document,
      fetch: async () => ({ ok: true, json: async () => toMobileBill(bill, 'Admin') })
    });

    expect(body.innerHTML).toContain('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;');
    expect(body.innerHTML).toContain('&amp;');
    expect(body.innerHTML).toContain('&lt;img src=x onerror=1&gt;');
    expect(body.innerHTML).toContain('&lt;large&gt;');
    expect(body.innerHTML).not.toContain('<script>');
    expect(body.innerHTML).not.toContain('<img src=x onerror=1>');
  });

  it('renders identically against LAN DTO and Cloud toMobileBill DTO', async () => {
    const lanDto = {
      id: 10,
      billNumber: 'COMPARE-10',
      billType: 'Customer',
      customerName: 'Equivalent Customer',
      phone: null,
      shippingAddress: null,
      periodStart: null,
      periodEnd: '2026-10-02',
      status: 'Draft',
      syncState: 'confirmed',
      isPaid: false,
      paidAt: null,
      note: null,
      hasPaymentQr: false,
      hasExportFile: false,
      productSubtotal: 75000,
      adjustmentsTotal: 0,
      grandTotal: 75000,
      lines: [{ id: 1, description: 'Photo', size: '10x15', quantity: 15, unitPrice: 5000, lineTotal: 75000 }],
      adjustments: []
    };

    const cloudDto: CloudBillDto = {
      id: 10,
      billNumber: 'COMPARE-10',
      billType: 'Customer',
      customerNameSnapshot: 'Equivalent Customer',
      periodDate: '2026-10-02',
      totalAmount: 75000,
      productSubtotal: 75000,
      adjustmentsTotal: 0,
      isPaid: false,
      hasJpeg: false,
      lines: [{ id: 1, description: 'Photo', size: '10x15', quantity: 15, unitPrice: 5000, lineTotal: 75000 }],
      adjustments: []
    };
    const cloudMappedDto = toMobileBill(cloudDto, 'Admin');

    const lanDom = createMockDom();
    await runInNewContext(`let currentRole='Admin', authToken='tok'; ${escapeHtml}\n${viewBill}\nviewBill(10)`, {
      document: lanDom.document,
      fetch: async () => ({ ok: true, json: async () => lanDto })
    });

    const cloudDom = createMockDom();
    await runInNewContext(`let currentRole='Admin', authToken='tok'; ${escapeHtml}\n${viewBill}\nviewBill(10)`, {
      document: cloudDom.document,
      fetch: async () => ({ ok: true, json: async () => cloudMappedDto })
    });

    expect(lanDom.body.innerHTML).toBe(cloudDom.body.innerHTML);
  });

  it('opens print preview modal via viewLabel/viewOrderLabel when tapping In tem on order cards', () => {
    const renderOrdersFn = script.slice(script.indexOf('    function renderOrders('), script.indexOf('    function filterOrders('));
    const viewLabelFn = script.slice(script.indexOf('    function viewLabel('), script.indexOf('    function showToast('));

    const orderList = { innerHTML: '' };
    const orders = [
      {
        id: 101,
        orderCode: 'ORD-101',
        customerName: 'Khách A',
        folderName: 'Khách A',
        isPrinted: false,
        isDelivered: false,
        totalQuantity: 10,
        customerBillId: 501,
        items: [{ variantName: '10x15', size: '10x15', billQuantity: 10 }]
      },
      {
        id: 102,
        orderCode: 'ORD-102',
        customerName: 'Khách B',
        folderName: 'Khách B',
        isPrinted: true,
        isDelivered: false,
        totalQuantity: 5,
        customerBillId: null,
        items: [{ variantName: '13x18', size: '13x18', billQuantity: 5 }]
      }
    ];

    const removedClasses: string[] = [];
    const previewBody = { innerHTML: '' };
    const previewTitle = { innerText: '' };
    const previewModal = { classList: { remove(cls: string) { removedClasses.push(cls); } } };

    const doc = {
      getElementById(id: string) {
        if (id === 'orderList') return orderList;
        if (id === 'previewModalBody') return previewBody;
        if (id === 'previewModalTitle') return previewTitle;
        if (id === 'previewModal') return previewModal;
        return { innerText: '' };
      }
    };

    runInNewContext(
      `const IS_CLOUD_BUILD = true; let currentRole = 'Admin', authToken = 'tok123', cachedOrders = ${JSON.stringify(orders)}; ${escapeHtml}\n${renderOrdersFn}\n${viewLabelFn}\nrenderOrders(cachedOrders, false);`,
      { document: doc }
    );

    expect(orderList.innerHTML).toContain('onclick="viewLabel(501, 101)"');
    expect(orderList.innerHTML).toContain('onclick="viewOrderLabel(102)"');
    expect(orderList.innerHTML).not.toContain('onclick="triggerRemotePrint(');

    runInNewContext(
      `const IS_CLOUD_BUILD = true; let currentRole = 'Admin', authToken = 'tok123', cachedOrders = ${JSON.stringify(orders)}; ${viewLabelFn}\nviewLabel(501, 101);`,
      { document: doc }
    );
    expect(removedClasses).toContain('hidden');
    expect(previewTitle.innerText).toBe('Xem trước tem giao hàng');
    expect(previewBody.innerHTML).toContain('/api/bills/501/label?token=tok123&orderId=101');
    expect(previewBody.innerHTML).toContain('triggerRemotePrint(101, 501, this)');
    expect(previewBody.innerHTML).toContain('handleLabelImageError(this, 101)');

    removedClasses.length = 0;
    runInNewContext(
      `const IS_CLOUD_BUILD = true; let currentRole = 'Staff', authToken = 'tok123', cachedOrders = ${JSON.stringify(orders)}; ${viewLabelFn}\nviewOrderLabel(102);`,
      { document: doc }
    );
    expect(removedClasses).toContain('hidden');
    expect(previewTitle.innerText).toBe('Xem trước tem đóng gói');
    expect(previewBody.innerHTML).toContain('/api/orders/102/label?token=tok123');
    expect(previewBody.innerHTML).toContain('triggerRemotePrint(102, null, this)');
    expect(previewBody.innerHTML).toContain('handleLabelImageError(this, null)');
  });
it('renders initial Ready gallery card with img-holder and retryGalleryBatch fallback for R2-404 self-heal', () => {
    const readyBranch = script.slice(
      script.indexOf("if (item.status === 'Ready' && item.thumbnailUrl)"),
      script.indexOf('missingItems.push({ fileId: item.fileId')
    );
    expect(readyBranch).toContain('id="img-holder-${item.fileId}"');
    expect(readyBranch).toContain('retryGalleryBatch(${orderId}');
  });
});
