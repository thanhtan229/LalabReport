using System;
using System.Data;
using System.IO;
using FluentAssertions;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Services;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class LalabServiceRegistrationTests
{
    [Fact]
    public void AddLalabCore_Default_ResolvesCoreServicesAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLalabCore();

        using var provider = services.BuildServiceProvider();

        var scanService1 = provider.GetRequiredService<IScanService>();
        var scanService2 = provider.GetRequiredService<IScanService>();
        scanService1.Should().NotBeNull();
        scanService1.Should().BeSameAs(scanService2);

        var billing1 = provider.GetRequiredService<ICustomerBillingService>();
        var billing2 = provider.GetRequiredService<ICustomerBillingService>();
        billing1.Should().NotBeNull();
        billing1.Should().BeSameAs(billing2);

        var gallery1 = provider.GetRequiredService<IOrderGalleryService>();
        var gallery2 = provider.GetRequiredService<IOrderGalleryService>();
        gallery1.Should().NotBeNull();
        gallery1.Should().BeSameAs(gallery2);

        var thumbnail1 = provider.GetRequiredService<IThumbnailService>();
        var thumbnail2 = provider.GetRequiredService<IThumbnailService>();
        thumbnail1.Should().NotBeNull();
        thumbnail1.Should().BeSameAs(thumbnail2);

        var mobileWeb1 = provider.GetRequiredService<IMobileWebServer>();
        var mobileWeb2 = provider.GetRequiredService<IMobileWebServer>();
        mobileWeb1.Should().NotBeNull();
        mobileWeb1.Should().BeSameAs(mobileWeb2);

        var cloudSync1 = provider.GetRequiredService<ICloudSyncService>();
        var cloudSync2 = provider.GetRequiredService<ICloudSyncService>();
        cloudSync1.Should().NotBeNull();
        cloudSync1.Should().BeSameAs(cloudSync2);
    }

    [Fact]
    public void AddLalabCore_WithEnableCloudSyncFalse_CloudSyncServiceIsAbsent()
    {
        var services = new ServiceCollection();
        services.AddLalabCore(options => options.EnableCloudSync = false);

        services.Should().NotContain(sd => sd.ServiceType == typeof(ICloudSyncService));
        services.Should().NotContain(sd => sd.ServiceType == typeof(ICloudMediaSyncService));
    }

    [Fact]
    public void AddLalabCore_WithEnableMobileWebServerFalse_MobileWebServerIsAbsent()
    {
        var services = new ServiceCollection();
        services.AddLalabCore(options => options.EnableMobileWebServer = false);

        services.Should().NotContain(sd => sd.ServiceType == typeof(IMobileWebServer));
    }

    [Fact]
    public void AddLalabCore_WithOptions_ConfiguresWorkingConnectionFactoryAndThumbnailServiceInTempDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_test_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempDir, "data");
        string thumbsDir = Path.Combine(tempDir, "thumbnails");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLalabCore(options =>
            {
                options.DataDirectory = dataDir;
                options.ThumbnailsDirectory = thumbsDir;
                options.EnableCloudSync = false;
                options.EnableMobileWebServer = false;
            });

            using var provider = services.BuildServiceProvider();

            var connFactory = provider.GetRequiredService<ISqliteConnectionFactory>();
            connFactory.Should().NotBeNull();
            string expectedDbPath = Path.Combine(dataDir, "lalab_autoreport.db");
            connFactory.DatabasePath.Should().Be(expectedDbPath);

            using (var conn = connFactory.CreateConnection())
            {
                conn.State.Should().Be(ConnectionState.Open);
            }
            File.Exists(expectedDbPath).Should().BeTrue();

            var thumbnailService = provider.GetRequiredService<IThumbnailService>();
            thumbnailService.Should().NotBeNull();
            Directory.Exists(thumbsDir).Should().BeTrue();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup in temp
            }
        }
    }
}
