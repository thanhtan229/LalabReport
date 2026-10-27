using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using FluentAssertions;
using LalabAutoReport.Infrastructure.Services;
using Xunit;

namespace LalabAutoReport.Tests;

public class NetworkAddressHelperTests
{
    [Fact]
    public void SelectBestIpAddress_PrefersPhysicalEthernetOverVirtualWsl()
    {
        var candidates = new List<NetworkAdapterCandidate>
        {
            // Virtual WSL adapter listed FIRST (same as Windows enumerated order)
            new(
                Name: "vEthernet (WSL (Hyper-V firewall))",
                Description: "Hyper-V Virtual Ethernet Adapter",
                InterfaceType: NetworkInterfaceType.Ethernet,
                Status: OperationalStatus.Up,
                HasGateway: false,
                IPv4Addresses: new[] { IPAddress.Parse("172.21.192.1") }
            ),
            // Physical Realtek Ethernet connected to LAN router
            new(
                Name: "Ethernet",
                Description: "Realtek Gaming 2.5GbE Family Controller",
                InterfaceType: NetworkInterfaceType.Ethernet,
                Status: OperationalStatus.Up,
                HasGateway: true,
                IPv4Addresses: new[] { IPAddress.Parse("192.168.100.24") }
            )
        };

        var selected = NetworkAddressHelper.SelectBestIpAddress(candidates);

        selected.Should().Be("192.168.100.24");
    }

    [Fact]
    public void SelectBestIpAddress_PrefersPhysicalWiFiOverVirtualAdapters()
    {
        var candidates = new List<NetworkAdapterCandidate>
        {
            new(
                Name: "VirtualBox Host-Only Network",
                Description: "VirtualBox Host-Only Ethernet Adapter",
                InterfaceType: NetworkInterfaceType.Ethernet,
                Status: OperationalStatus.Up,
                HasGateway: false,
                IPv4Addresses: new[] { IPAddress.Parse("192.168.56.1") }
            ),
            new(
                Name: "Wi-Fi",
                Description: "Intel(R) Wi-Fi 6 AX200 160MHz",
                InterfaceType: NetworkInterfaceType.Wireless80211,
                Status: OperationalStatus.Up,
                HasGateway: true,
                IPv4Addresses: new[] { IPAddress.Parse("192.168.1.105") }
            )
        };

        var selected = NetworkAddressHelper.SelectBestIpAddress(candidates);

        selected.Should().Be("192.168.1.105");
    }

    [Fact]
    public void SelectBestIpAddress_WhenNoPhysicalWithGateway_PicksPhysicalWithoutGateway()
    {
        var candidates = new List<NetworkAdapterCandidate>
        {
            new(
                Name: "Ethernet",
                Description: "Intel Ethernet Connection",
                InterfaceType: NetworkInterfaceType.Ethernet,
                Status: OperationalStatus.Up,
                HasGateway: false,
                IPv4Addresses: new[] { IPAddress.Parse("10.0.0.50") }
            )
        };

        var selected = NetworkAddressHelper.SelectBestIpAddress(candidates);

        selected.Should().Be("10.0.0.50");
    }

    [Fact]
    public void SelectBestIpAddress_WhenEmpty_ReturnsLoopback()
    {
        var candidates = new List<NetworkAdapterCandidate>();

        var selected = NetworkAddressHelper.SelectBestIpAddress(candidates);

        selected.Should().Be("127.0.0.1");
    }

    [Fact]
    public void IsVirtualOrExcluded_CorrectlyIdentifiesVirtualAdapters()
    {
        NetworkAddressHelper.IsVirtualOrExcluded("vEthernet (WSL)", "Hyper-V Virtual Adapter").Should().BeTrue();
        NetworkAddressHelper.IsVirtualOrExcluded("VirtualBox Host-Only", "VirtualBox Adapter").Should().BeTrue();
        NetworkAddressHelper.IsVirtualOrExcluded("Ethernet 2", "VMware Virtual Ethernet").Should().BeTrue();
        NetworkAddressHelper.IsVirtualOrExcluded("Local Area Connection", "TAP-Windows Adapter V9").Should().BeTrue();
        NetworkAddressHelper.IsVirtualOrExcluded("Bluetooth Device", "Bluetooth Personal Area Network").Should().BeTrue();

        // Physical cards should NOT be marked virtual
        NetworkAddressHelper.IsVirtualOrExcluded("Ethernet", "Realtek Gaming 2.5GbE Family Controller").Should().BeFalse();
        NetworkAddressHelper.IsVirtualOrExcluded("Wi-Fi", "Intel(R) Wi-Fi 6 AX200").Should().BeFalse();
    }

    [Fact]
    public void GetLocalIpAddress_OnRealMachine_ReturnsValidLanIpNotVirtualWsl()
    {
        var localIp = NetworkAddressHelper.GetLocalIpAddress();

        localIp.Should().NotBeNullOrWhiteSpace();
        localIp.Should().NotStartWith("172.21.192.", "Should not pick Hyper-V WSL virtual switch");
        System.Net.IPAddress.TryParse(localIp, out var parsedIp).Should().BeTrue("Should return a valid IPv4 address");
        parsedIp!.AddressFamily.Should().Be(System.Net.Sockets.AddressFamily.InterNetwork);
        System.Net.IPAddress.IsLoopback(parsedIp).Should().BeFalse("Should pick a LAN address, not loopback");
    }
}
