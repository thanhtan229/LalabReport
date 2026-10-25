export interface Env {
  DB: D1Database;
  BUCKET?: R2Bucket;
  ASSETS?: Fetcher;
  WORKSHOP_NAME?: string;
  WORKSHOP_PHONE?: string;
  WORKSHOP_ADDRESS?: string;
  CUSTOM_DOMAIN?: string;
  ADMIN_PIN?: string;
  STAFF_PIN?: string;
  JWT_SECRET?: string;
  SYNC_SECRET?: string;
}

export type UserRole = 'Admin' | 'Staff';

export interface AuthTokenPayload {
  role: UserRole;
  exp: number; // Unix timestamp in seconds
}

export interface CloudOrderDto {
  id: number;
  orderCode: string;
  customerName: string;
  folderName: string;
  workDate: string;
  status: string;
  isPrinted: boolean;
  printedAt?: string | null;
  isDelivered: boolean;
  deliveredAt?: string | null;
  deliveredBy?: string | null;
  note?: string | null;
  hasIssues: boolean;
  isLocked: boolean;
  hasThumbnail: boolean;
  thumbnailFileName?: string | null;
  thumbnailStatus: 'NONE' | 'PENDING' | 'READY';
  thumbnailKey?: string | null;
  thumbnailVersion?: string | null;
  customerBillId?: number | null;
  totalQuantity: number;
  items: Array<{
    id: number;
    specName: string;
    variantName: string;
    size?: string | null;
    billQuantity: number;
    printCount?: number | null;
    isMissingPrintFolder: boolean;
    isAmbiguous: boolean;
  }>;
}

export interface CloudBillDto {
  id: number;
  billNumber: string;
  billType: string;
  customerId?: number | null;
  customerNameSnapshot: string;
  phoneSnapshot?: string | null;
  shippingAddressSnapshot?: string | null;
  note?: string | null;
  periodDate: string;
  totalAmount: number;
  productSubtotal?: number | null;
  adjustmentsTotal?: number | null;
  isPaid: boolean;
  paidAt?: string | null;
  hasJpeg: boolean;
  lockedAt?: string | null;
  exportedAt?: string | null;
  lines: Array<{
    id: number;
    productName?: string;
    description?: string;
    size?: string | null;
    quantity: number;
    unitPrice: number;
    lineTotal: number;
    note?: string | null;
  }>;
  adjustments: Array<{
    id: number;
    type: string;
    direction?: string;
    amount: number;
    description?: string | null;
  }>;
}

export interface SyncBatchPayload {
  orders?: Array<{
    id: number;
    orderCode: string;
    customerName: string;
    folderName: string;
    workDate: string;
    status: string;
    isPrinted: boolean;
    printedAt?: string | null;
    isDelivered: boolean;
    deliveredAt?: string | null;
    deliveredBy?: string | null;
    note?: string | null;
    hasIssues: boolean;
    isLocked: boolean;
    hasThumbnail: boolean;
    thumbnailFileName?: string | null;
    thumbnailStatus?: 'NONE' | 'PENDING' | 'READY';
    thumbnailKey?: string | null;
    thumbnailVersion?: string | null;
    customerBillId?: number | null;
    totalQuantity: number;
    itemsJson: string;
  }>;
  bills?: Array<{
    id: number;
    billNumber: string;
    billType: string;
    customerId?: number | null;
    customerNameSnapshot: string;
    phoneSnapshot?: string | null;
    shippingAddressSnapshot?: string | null;
    note?: string | null;
    periodDate: string;
    totalAmount: number;
    productSubtotal?: number | null;
    adjustmentsTotal?: number | null;
    isPaid: boolean;
    paidAt?: string | null;
    linesJson: string;
    adjustmentsJson: string;
    hasJpeg: boolean;
    lockedAt?: string | null;
    exportedAt?: string | null;
  }>;
  reports?: Array<{
    key: string;
    dataJson: string;
  }>;
}

export interface OperationalEventDto {
  operationId?: string | null;
  id: number;
  entityType: string;
  entityId: number;
  payloadJson: string;
  createdAt: string;
}

export interface HeartbeatPayload {
  deviceId?: string;
  lanUrl: string;
  appVersion?: string | null;
}

export interface SystemStatusDto {
  isDesktopOnline: boolean;
  lanUrl?: string | null;
  appVersion?: string | null;
  lastHeartbeatAt?: string | null;
  secondsSinceHeartbeat?: number | null;
}

export interface PrintCommandDto {
  id: number;
  orderId: number;
  billId?: number | null;
  commandType: 'print_order_label' | 'print_bill_label';
  status: 'PENDING' | 'PROCESSING' | 'COMPLETED' | 'FAILED';
  requestedBy?: string | null;
  createdAt: string;
  processedAt?: string | null;
  errorMessage?: string | null;
}

