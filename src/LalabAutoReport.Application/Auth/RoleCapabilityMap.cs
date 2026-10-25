using System;
using System.Collections.Generic;
using LalabAutoReport.Contracts;
using LalabAutoReport.Core.Domain;

namespace LalabAutoReport.ApplicationCore.Auth;

public static class RoleCapabilityMap
{
    private static readonly HashSet<string> StaffCapabilities = new(StringComparer.Ordinal)
    {
        Capabilities.OrdersRead,
        Capabilities.OrdersSetDelivered,
        Capabilities.OrdersSetPrinted,
        Capabilities.OrdersEditNote,
        Capabilities.GalleryRead,
        Capabilities.LabelsPrintOrder
    };

    private static readonly HashSet<string> AdminCapabilities = new(StringComparer.Ordinal)
    {
        Capabilities.OrdersRead,
        Capabilities.OrdersSetDelivered,
        Capabilities.OrdersSetPrinted,
        Capabilities.OrdersEditNote,
        Capabilities.GalleryRead,
        Capabilities.LabelsPrintOrder,
        Capabilities.FinanceRead,
        Capabilities.BillsDraft,
        Capabilities.BillsLock,
        Capabilities.BillsSplit,
        Capabilities.BillsOverrideQuantityPrice,
        Capabilities.BillsReopen,
        Capabilities.PaymentsConfirm,
        Capabilities.PaymentsRevoke,
        Capabilities.CatalogEdit,
        Capabilities.ScanRequest,
        Capabilities.AdminMaintenance,
        Capabilities.AdminSessions
    };

    public static IReadOnlySet<string> GetCapabilities(string? role)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return AdminCapabilities;
        if (string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase))
            return StaffCapabilities;
        return new HashSet<string>();
    }

    public static IReadOnlySet<string> GetCapabilities(MobileUserRole role) =>
        GetCapabilities(role.ToString());

    public static bool HasCapability(string? role, string capability) =>
        GetCapabilities(role).Contains(capability);

    public static bool HasCapability(MobileUserRole role, string capability) =>
        GetCapabilities(role.ToString()).Contains(capability);
}
