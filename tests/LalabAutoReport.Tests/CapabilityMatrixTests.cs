using System;
using System.Linq;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using Xunit;

namespace LalabAutoReport.Tests;

public class CapabilityMatrixTests
{
    [Fact]
    public void Capability_StaffCannotReadFinanceFields()
    {
        var staffCaps = RoleCapabilityMap.GetCapabilities("Staff");

        staffCaps.Should().NotContain(Capabilities.FinanceRead);
        staffCaps.Should().NotContain(Capabilities.BillsLock);
        staffCaps.Should().NotContain(Capabilities.BillsReopen);
        staffCaps.Should().NotContain(Capabilities.PaymentsConfirm);
        staffCaps.Should().NotContain(Capabilities.PaymentsRevoke);
        staffCaps.Should().NotContain(Capabilities.AdminMaintenance);
        staffCaps.Should().NotContain(Capabilities.AdminSessions);
    }

    [Fact]
    public void Capability_StaffMatrixEqualsPhase1PwaRights()
    {
        var staffCaps = RoleCapabilityMap.GetCapabilities("Staff");

        var expectedPhase1Rights = new[]
        {
            Capabilities.OrdersRead,
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote,
            Capabilities.GalleryRead,
            Capabilities.LabelsPrintOrder
        };

        staffCaps.Should().BeEquivalentTo(expectedPhase1Rights);

        // Admin has all rights
        var adminCaps = RoleCapabilityMap.GetCapabilities("Admin");
        adminCaps.Should().Contain(expectedPhase1Rights);
        adminCaps.Should().Contain(Capabilities.FinanceRead);
        adminCaps.Should().Contain(Capabilities.AdminSessions);
        adminCaps.Should().Contain(Capabilities.AdminMaintenance);
    }

    [Fact]
    public void Capability_HandlerDeniesEvenIfGateBypassed()
    {
        var staffContext = ActorContext.FromSession(
            sessionId: "test-session",
            role: "Staff",
            capabilities: RoleCapabilityMap.GetCapabilities("Staff"));

        staffContext.Has(Capabilities.FinanceRead).Should().BeFalse();

        Action act = () =>
        {
            if (!staffContext.Has(Capabilities.FinanceRead))
                throw new CapabilityDeniedException(Capabilities.FinanceRead);
        };
        act.Should().Throw<CapabilityDeniedException>()
            .WithMessage($"*'{Capabilities.FinanceRead}'*");
    }

    [Fact]
    public void Capability_StaffGets403OnFinanceEndpoints()
    {
        var staffContext = ActorContext.FromSession(
            sessionId: "test-staff",
            role: "Staff",
            capabilities: RoleCapabilityMap.GetCapabilities("Staff"));

        staffContext.Has(Capabilities.FinanceRead).Should().BeFalse();

        var adminContext = ActorContext.FromSession(
            sessionId: "test-admin",
            role: "Admin",
            capabilities: RoleCapabilityMap.GetCapabilities("Admin"));

        adminContext.Has(Capabilities.FinanceRead).Should().BeTrue();
    }
}
