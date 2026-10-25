using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LalabAutoReport.Contracts;
using Microsoft.AspNetCore.Http;

namespace LalabAutoReport.Infrastructure.Web;

public static class LanWriteGate
{
    private static readonly (string Method, Regex Regex, string Capability)[] RouteTable =
    [
        // Orders: delivered
        (HttpMethods.Post, new Regex(@"^/api/v1/orders/[^/]+/delivered/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.OrdersSetDelivered),
        (HttpMethods.Post, new Regex(@"^/api/orders/[^/]+/toggle-delivered/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.OrdersSetDelivered),

        // Orders: printed
        (HttpMethods.Post, new Regex(@"^/api/v1/orders/[^/]+/printed/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.OrdersSetPrinted),
        (HttpMethods.Post, new Regex(@"^/api/orders/[^/]+/toggle-printed/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.OrdersSetPrinted),

        // Orders: note
        (HttpMethods.Post, new Regex(@"^/api/v1/orders/[^/]+/note/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.OrdersEditNote),
        (HttpMethods.Post, new Regex(@"^/api/orders/[^/]+/note/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.OrdersEditNote),

        // Orders: print-label
        (HttpMethods.Post, new Regex(@"^/api/orders/[^/]+/print-label/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.LabelsPrintOrder),

        // Bills: payment
        (HttpMethods.Post, new Regex(@"^/api/bills/[^/]+/toggle-payment/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.PaymentsConfirm),

        // Bills: print
        (HttpMethods.Post, new Regex(@"^/api/bills/[^/]+/print/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.BillsLock),

        // Admin / Maintenance: thumbnails
        (HttpMethods.Post, new Regex(@"^/api/settings/thumbnails/(scan|rebuild|cleanup|clear)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), Capabilities.AdminMaintenance),
    ];

    private static readonly Regex GalleryAllowedPathRegex =
        new(@"^/api/orders/\d+/gallery/(?:requests|touch)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ResolveCapability(string method, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var cleanPath = CleanPath(path);
        foreach (var entry in RouteTable)
        {
            if (string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase) &&
                entry.Regex.IsMatch(cleanPath))
            {
                return entry.Capability;
            }
        }

        return null;
    }

    public static bool IsAlwaysAllowed(string method, string? path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return true;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var cleanPath = CleanPath(path);

        if (HttpMethods.IsDelete(method))
        {
            return string.Equals(cleanPath, "/api/v1/auth/sessions/current", StringComparison.OrdinalIgnoreCase);
        }

        if (HttpMethods.IsPost(method))
        {
            if (string.Equals(cleanPath, "/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanPath, "/api/v1/auth/sessions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanPath, "/api/v1/admin/sessions/revoke-all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GalleryAllowedPathRegex.IsMatch(cleanPath);
        }

        return false;
    }

    public static bool IsAllowed(string method, string? path, IReadOnlySet<string>? enabledCapabilities)
    {
        // 1. Safe HTTP methods & always-allowed auth/gallery routes
        if (IsAlwaysAllowed(method, path))
            return true;

        // 2. If enabledCapabilities is null => standalone mode / all writes enabled
        if (enabledCapabilities == null)
            return true;

        // 3. Resolve capability from static route table
        var capability = ResolveCapability(method, path);
        if (capability == null)
        {
            // Unmapped routes fail closed
            return false;
        }

        return enabledCapabilities.Contains(capability);
    }

    private static string CleanPath(string path)
    {
        int queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path.Substring(0, queryIndex);
        }
        return path.TrimEnd('/');
    }
}
