namespace LalabAutoReport.Contracts;

public static class Capabilities
{
    public const string OrdersRead = "orders.read";
    public const string OrdersSetDelivered = "orders.setDelivered";
    public const string OrdersSetPrinted = "orders.setPrinted";
    public const string OrdersEditNote = "orders.editNote";
    public const string GalleryRead = "gallery.read";
    public const string LabelsPrintOrder = "labels.printOrder";
    public const string FinanceRead = "finance.read";
    public const string BillsDraft = "bills.draft";
    public const string BillsLock = "bills.lock";
    public const string BillsSplit = "bills.split";
    public const string BillsOverrideQuantityPrice = "bills.overrideQuantityPrice";
    public const string BillsReopen = "bills.reopen";
    public const string PaymentsConfirm = "payments.confirm";
    public const string PaymentsRevoke = "payments.revoke";
    public const string CatalogEdit = "catalog.edit";
    public const string ScanRequest = "scan.request";
    public const string AdminMaintenance = "admin.maintenance";
    public const string AdminSessions = "admin.sessions";
}
