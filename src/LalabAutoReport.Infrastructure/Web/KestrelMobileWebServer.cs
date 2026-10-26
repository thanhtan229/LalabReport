using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Services;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts.Commands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Web;

public class KestrelMobileWebServer : IMobileWebServer, IDisposable
{
    private static readonly Regex ImageQueryTokenAllowedRegex = new(
        @"^/api/(orders/\d+/(thumbnail|gallery/images/[^/]+|label)|bills/\d+/(image|label|qr))/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IMobileAuthService _authService;
    private readonly MobileWebServerOptions _options;
    private readonly ILogger<KestrelMobileWebServer>? _logger;

    private WebApplication? _webApp;
    private int _port = 5050;
    private string? _localIp;
    private string? _errorMessage;
    private readonly object _lock = new();

    public event EventHandler? StatusChanged;

    public bool IsRunning => _webApp != null;
    public int Port => _port;
    public string? LocalIpAddress => _localIp;
    public string? LocalUrl => IsRunning && !string.IsNullOrWhiteSpace(_localIp) ? $"http://{_localIp}:{_port}" : null;
    public string? RemoteUrl => null;

    public KestrelMobileWebServer(
        IServiceProvider serviceProvider,
        ISettingsRepository settingsRepository,
        IMobileAuthService authService,
        MobileWebServerOptions? options,
        ILogger<KestrelMobileWebServer>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _settingsRepository = settingsRepository;
        _authService = authService;
        _options = options ?? new MobileWebServerOptions();
        _logger = logger;
    }

    public KestrelMobileWebServer(
        IServiceProvider serviceProvider,
        ISettingsRepository settingsRepository,
        IMobileAuthService authService,
        ILogger<KestrelMobileWebServer>? logger = null)
        : this(serviceProvider, settingsRepository, authService, null, logger)
    {
    }

    public static bool IsAllowedWritePath(string method, string? path) => LanWriteGate.IsAlwaysAllowed(method, path);

    public static bool IsAllowedWritePath(string? path) => IsAllowedWritePath(HttpMethods.Post, path);

    public MobileServerStatus GetStatus()
    {
        lock (_lock)
        {
            return new MobileServerStatus(
                IsRunning: IsRunning,
                Port: _port,
                LocalIp: _localIp,
                LocalUrl: LocalUrl,
                RemoteUrl: RemoteUrl,
                ErrorMessage: _errorMessage
            );
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (IsRunning) return;
        }

        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!_options.LoopbackOnly && !settings.EnableMobileServer)
        {
            _logger?.LogInformation("Mobile Web Server is disabled in settings.");
            return;
        }

        _port = _options.Port > 0 ? _options.Port : (settings.MobileServerPort > 0 ? settings.MobileServerPort : 5050);
        _localIp = _options.LoopbackOnly ? "127.0.0.1" : NetworkAddressHelper.GetLocalIpAddress();
        _errorMessage = null;

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options =>
            {
                var bindAddress = _options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any;
                options.Listen(bindAddress, _port);
            });

            builder.Logging.ClearProviders();

            builder.Services.AddCors(cors =>
            {
                cors.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
            });

            var app = builder.Build();
            app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api/v1"), branch => branch.UseCors());

            ConfigureEndpoints(app);

            await app.StartAsync(cancellationToken);

            lock (_lock)
            {
                _webApp = app;
            }

            _logger?.LogInformation("Mobile Web Server started on port {Port}. LAN URL: {Url}", _port, LocalUrl);
            NotifyStatusChanged();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start Kestrel Mobile Web Server on port {Port}", _port);
            lock (_lock)
            {
                _errorMessage = $"Không thể mở cổng {_port}: {ex.Message}";
                _webApp = null;
            }
            NotifyStatusChanged();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WebApplication? appToStop;
        lock (_lock)
        {
            appToStop = _webApp;
            _webApp = null;
        }

        if (appToStop != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                await appToStop.StopAsync(linkedCts.Token);
                await appToStop.DisposeAsync();
            }
            catch { }
        }

        NotifyStatusChanged();
    }

    private void ConfigureEndpoints(WebApplication app)
    {
        if (_options.EnabledWriteCapabilities != null)
        {
            app.Use(async (ctx, next) =>
            {
                if (!LanWriteGate.IsAllowed(ctx.Request.Method, ctx.Request.Path.Value, _options.EnabledWriteCapabilities))
                {
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        code = ApiErrorCodes.WritesDisabledUntilPhase2,
                        message = "Máy chủ đang chạy ở chế độ chỉ đọc LAN Phase 1. Các thao tác ghi bị tạm khóa cho đến Phase 2."
                    });
                    return;
                }

                await next();
            });
        }

        app.Use(async (ctx, next) =>
        {
            try
            {
                var actor = await AuthenticateAsync(ctx);
                ctx.Items["ActorContext"] = actor;

            if (ctx.Items.TryGetValue("LegacyTokenWrite", out var ltw) && ltw is true)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    code = ApiErrorCodes.SessionUpgradeRequired,
                    message = "Phiên đã hết hiệu lực, vui lòng nhập lại PIN."
                });
                return;
            }

            await next();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Request failed: {Path}", ctx.Request.Path);
                throw;
            }
        });

        // 1. Status endpoint
        app.MapGet("/api/status", async (HttpContext ctx) =>
        {
            var settings = await _settingsRepository.GetSettingsAsync();
            return Results.Ok(new
            {
                appName = "Lalab Auto Report",
                version = typeof(KestrelMobileWebServer).Assembly.GetName().Version?.ToString(3) ?? "2.0.0",
                workshopName = settings.WorkshopName,
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                cloudSyncEnabled = settings.EnableCloudSync,
                remoteUrl = settings.CloudSyncApiUrl
            });
        });

        // 2. Authentication: Login with PIN
        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

            string pin = doc.RootElement.TryGetProperty("pin", out var pinElem) ? pinElem.GetString() ?? "" : "";
            var settings = await _settingsRepository.GetSettingsAsync();

            if (!settings.AdminPinConfigured && string.IsNullOrWhiteSpace(settings.AdminPin))
            {
                return Results.Json(new { success = false, message = "Chưa cấu hình mã PIN Admin trên ứng dụng Desktop. Vui lòng vào Cài đặt để thiết lập mã PIN." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!string.IsNullOrEmpty(settings.AdminPin) && !string.IsNullOrEmpty(settings.StaffPin) &&
                string.Equals(settings.AdminPin, settings.StaffPin, StringComparison.Ordinal))
            {
                return Results.Json(new { success = false, message = "Mã PIN Admin và Nhân viên không được trùng nhau. Vui lòng cập nhật lại trong Cài đặt." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var sessionService = _serviceProvider.GetService<ISessionService>();
            if (sessionService != null)
            {
                string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var result = await sessionService.LoginAsync(pin, clientKind: "pwa", ip: ip);

                if (result.Status == LoginResultStatus.RateLimited)
                {
                    int retrySec = (int)Math.Ceiling(result.RetryAfter?.TotalSeconds ?? 60);
                    ctx.Response.Headers.RetryAfter = retrySec.ToString();
                    return Results.Json(new { success = false, code = ApiErrorCodes.LoginRateLimited, message = "Quá nhiều lần thử sai. Vui lòng thử lại sau." }, statusCode: StatusCodes.Status429TooManyRequests);
                }

                if (result.Status == LoginResultStatus.NotConfigured)
                {
                    return Results.Json(new { success = false, message = "Chưa cấu hình mã PIN Admin trên ứng dụng Desktop. Vui lòng vào Cài đặt để thiết lập mã PIN." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (result.Status == LoginResultStatus.InvalidCredentials || result.Role == null || result.Token == null)
                {
                    return Results.Json(new { success = false, message = "Mã PIN không chính xác." }, statusCode: StatusCodes.Status401Unauthorized);
                }

                return Results.Ok(new
                {
                    success = true,
                    role = result.Role,
                    token = result.Token,
                    workshopName = settings.WorkshopName
                });
            }

            if (string.IsNullOrWhiteSpace(settings.AdminPin))
            {
                return Results.Json(new { success = false, message = "Chưa cấu hình mã PIN Admin trên ứng dụng Desktop. Vui lòng vào Cài đặt để thiết lập mã PIN." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!string.IsNullOrWhiteSpace(settings.StaffPin) && string.Equals(settings.AdminPin.Trim(), settings.StaffPin.Trim(), StringComparison.Ordinal))
            {
                return Results.Json(new { success = false, message = "Mã PIN Admin và Nhân viên không được trùng nhau. Vui lòng cấu hình lại trong Cài đặt." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

#pragma warning disable CS0618
            var role = _authService.AuthenticateWithPin(pin, settings);
#pragma warning restore CS0618
            if (role == null)
            {
                return Results.Json(new { success = false, message = "Mã PIN không chính xác." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            string token = _authService.GenerateToken(role.Value);
            return Results.Ok(new
            {
                success = true,
                role = role.Value.ToString(),
                token = token,
                workshopName = settings.WorkshopName
            });
        });

        // 3. Current User Info
        app.MapGet("/api/auth/me", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var settings = await _settingsRepository.GetSettingsAsync();
            return Results.Ok(new
            {
                authenticated = true,
                role = auth.Role,
                workshopName = settings.WorkshopName,
                workshopPhone = settings.WorkshopPhone,
                workshopAddress = settings.WorkshopAddress
            });
        });

        // 4. Orders: List by Date
        app.MapGet("/api/orders", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            string date = ctx.Request.Query.TryGetValue("date", out var d) ? d.ToString() : DateTime.Today.ToString("yyyy-MM-dd");
            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var orders = await orderRepo.GetOrdersByDateAsync(date);

            var result = orders.Select(o =>
            {
                string? thumbRel = o.ThumbnailCandidateRelativePath;
                if (string.IsNullOrWhiteSpace(thumbRel) && o.Items != null && o.Items.Count > 0)
                {
                    thumbRel = o.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ThumbnailCandidateRelativePath))?.ThumbnailCandidateRelativePath;
                }

                bool hasThumb = !string.IsNullOrEmpty(thumbRel) || !string.IsNullOrEmpty(o.RelativePath);

                return new
                {
                    id = o.Id,
                    orderCode = o.OrderCode,
                    customerName = o.Customer?.CanonicalName ?? o.OriginalFolderName,
                    folderName = o.OriginalFolderName,
                    workDate = o.WorkDate,
                    status = o.Status.ToString(),
                    isPrinted = o.IsPrinted,
                    printedAt = o.PrintedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    syncState = o.HasPendingCloudChanges ? "pending" : "confirmed",
                    isDelivered = o.IsDelivered,
                    deliveredAt = o.DeliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    deliveredBy = o.DeliveredBy,
                    deliveredRevision = o.DeliveredRevision,
                    printedRevision = o.PrintedRevision,
                    noteRevision = o.NoteRevision,
                    note = o.Note,
                    hasIssues = o.Status == OrderStatus.NeedsReview || o.Status == OrderStatus.Error,
                    isLocked = o.Status == OrderStatus.Locked,
                    hasThumbnail = hasThumb,
                    thumbnailFileName = !string.IsNullOrEmpty(thumbRel) ? Path.GetFileName(thumbRel) : null,
                    customerBillId = o.Items?.FirstOrDefault(i => i.CustomerBillId != null)?.CustomerBillId,
                    totalQuantity = o.Items?.Sum(i => i.BillQuantity ?? i.PrintCount ?? 0) ?? 0,
                    items = (o.Items ?? (IReadOnlyList<OrderItemScan>)Array.Empty<OrderItemScan>()).Select(i => new
                    {
                        id = i.Id,
                        specName = i.SpecificationFolderName,
                        variantName = i.PrintSpecification?.CanonicalName ?? i.SpecificationFolderName,
                        size = i.PrintSpecification?.CanonicalSize,
                        billQuantity = i.BillQuantity ?? i.PrintCount ?? 0,
                        printCount = i.PrintCount,
                        isMissingPrintFolder = i.PrintFolderStatus == PrintFolderResolutionStatus.NoPrintFolder,
                        isAmbiguous = i.PrintFolderStatus == PrintFolderResolutionStatus.AmbiguousPrintFolder
                    })
                };
            });

            return Results.Ok(result);
        });

        // 5.1 Order Thumbnail
        app.MapGet("/api/orders/{id:long}/thumbnail", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null)
            {
                return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });
            }

            var settings = await _settingsRepository.GetSettingsAsync();
            string root = settings.RootFolder;
            if (order.RootFolderId.HasValue)
            {
                var rootRepo = _serviceProvider.GetService<IRootFolderRepository>();
                var rf = rootRepo == null ? null : await rootRepo.GetByIdAsync(order.RootFolderId.Value);
                if (rf == null || string.IsNullOrWhiteSpace(rf.FullPath))
                    return Results.NotFound(new { message = "Kho của đơn không khả dụng; không dùng kho khác thay thế." });
                root = rf.FullPath;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                return Results.NotFound(new { message = "Không xác định được thư mục gốc." });
            }

            string? candidateRel = order.ThumbnailCandidateRelativePath;
            if (string.IsNullOrWhiteSpace(candidateRel) && order.Items != null && order.Items.Count > 0)
            {
                candidateRel = order.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ThumbnailCandidateRelativePath))?.ThumbnailCandidateRelativePath;
            }

            if (string.IsNullOrWhiteSpace(candidateRel) && !string.IsNullOrWhiteSpace(order.RelativePath))
            {
                string orderFolder = Path.Combine(root, order.RelativePath);
                candidateRel = FindFirstImageRelativePath(orderFolder, root);
            }

            if (string.IsNullOrWhiteSpace(candidateRel))
            {
                return Results.NotFound(new { message = "Không có ảnh thumbnail cho đơn hàng này." });
            }

            string fullImagePath = Path.Combine(root, candidateRel);
            if (!File.Exists(fullImagePath))
            {
                return Results.NotFound(new { message = "Tệp ảnh gốc không tồn tại." });
            }

            var thumbnailService = _serviceProvider.GetRequiredService<IThumbnailService>();
            var thumbResult = await thumbnailService.GetThumbnailAsync(fullImagePath, ctx.RequestAborted);
            if (thumbResult.Status == ThumbnailStatus.Success && !string.IsNullOrWhiteSpace(thumbResult.CachedFilePath) && File.Exists(thumbResult.CachedFilePath))
            {
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.File(thumbResult.CachedFilePath, "image/jpeg");
            }

            if (thumbResult.Status == ThumbnailStatus.UnsupportedFormat)
            {
                return Results.Json(new { unsupported = true, format = thumbResult.FormatExtension }, statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            return Results.NotFound(new { message = "Không tạo được ảnh thumbnail." });
        });

        // 4.1 Order Gallery Manifest Request
        app.MapPost("/api/orders/{id:long}/gallery/requests", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var galleryService = _serviceProvider.GetRequiredService<IOrderGalleryService>();
            try
            {
                var manifest = await galleryService.GetOrderManifestAsync(id, ctx.RequestAborted);
                return Results.Ok(manifest);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // 4.2 Order Gallery Page Items
        app.MapGet("/api/orders/{id:long}/gallery", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            string version = ctx.Request.Query["version"].ToString() ?? string.Empty;
            int offset = 0;
            if (int.TryParse(ctx.Request.Query["offset"], out var parsedOffset) && parsedOffset >= 0)
            {
                offset = parsedOffset;
            }
            int limit = 24;
            if (int.TryParse(ctx.Request.Query["limit"], out var parsedLimit) && parsedLimit > 0 && parsedLimit <= 100)
            {
                limit = parsedLimit;
            }

            var galleryService = _serviceProvider.GetRequiredService<IOrderGalleryService>();
            try
            {
                var page = await galleryService.GetOrderGalleryPageAsync(id, version, offset, limit, ctx.RequestAborted);
                return Results.Ok(page);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });
            }
        });

        // 4.3 Order Gallery Image Thumbnail
        app.MapGet("/api/orders/{id:long}/gallery/images/{fileId}", async (long id, string fileId, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            string version = ctx.Request.Query["v"].ToString() ?? string.Empty;
            var galleryService = _serviceProvider.GetRequiredService<IOrderGalleryService>();

            try
            {
                var result = await galleryService.GetGalleryThumbnailAsync(id, version, fileId, ctx.RequestAborted);
                if (result.Status == ThumbnailStatus.Success && !string.IsNullOrWhiteSpace(result.CachedFilePath) && File.Exists(result.CachedFilePath))
                {
                    ctx.Response.Headers.CacheControl = "private, max-age=86400";
                    return Results.File(result.CachedFilePath, "image/jpeg");
                }

                if (result.Status == ThumbnailStatus.UnsupportedFormat)
                {
                    return Results.Json(new { unsupported = true, format = result.FormatExtension }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                return Results.NotFound(new { message = "Không tìm thấy ảnh hoặc ảnh chưa sẵn sàng." });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });
            }
        });

        // 4.4 Order Gallery Viewport Touch
        app.MapPost("/api/orders/{id:long}/gallery/touch", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<TouchGalleryRequest>(cancellationToken: ctx.RequestAborted);
                if (body != null && body.FileIds != null && body.FileIds.Count > 0)
                {
                    var galleryService = _serviceProvider.GetRequiredService<IOrderGalleryService>();
                    await galleryService.TouchViewedBatchAsync(id, body.Version ?? "", body.FileIds, ctx.RequestAborted);
                }
                return Results.Ok(new { success = true });
            }
            catch
            {
                return Results.BadRequest(new { message = "Dữ liệu yêu cầu không hợp lệ." });
            }
        });

        // 4.5 Admin Settings: Cleanup / Clear Thumbnails
        app.MapPost("/api/settings/thumbnails/cleanup", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.AdminMaintenance)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var thumbService = _serviceProvider.GetRequiredService<IThumbnailService>();
            var settings = await _settingsRepository.GetSettingsAsync(ctx.RequestAborted);
            int days = (settings.ThumbnailRetentionDays == 30 || settings.ThumbnailRetentionDays == 90) ? settings.ThumbnailRetentionDays : 60;
            var summary = await thumbService.CleanupExpiredDiskCacheAsync(TimeSpan.FromDays(days), ctx.RequestAborted);

            settings.LastThumbnailCleanupAt = DateTimeOffset.UtcNow.ToString("o");
            await _settingsRepository.SaveSettingsAsync(settings, ctx.RequestAborted);

            return Results.Ok(new
            {
                success = true,
                filesDeleted = summary.FilesDeleted,
                bytesFreed = summary.BytesFreed,
                errorsCount = summary.ErrorsCount
            });
        });

        app.MapPost("/api/settings/thumbnails/clear", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.AdminMaintenance)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var thumbService = _serviceProvider.GetRequiredService<IThumbnailService>();
            var summary = await thumbService.ClearAllDiskCacheAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                success = true,
                filesDeleted = summary.FilesDeleted,
                bytesFreed = summary.BytesFreed
            });
        });

        // 5. Order Detail
        app.MapGet("/api/orders/{id:long}", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null) return Results.NotFound(new { message = "Không tìm thấy đơn hàng" });

            return Results.Ok(new
            {
                id = order.Id,
                orderCode = order.OrderCode,
                customerName = order.Customer?.CanonicalName ?? order.OriginalFolderName,
                folderName = order.OriginalFolderName,
                workDate = order.WorkDate,
                status = order.Status.ToString(),
                isPrinted = order.IsPrinted,
                printedAt = order.PrintedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                syncState = order.HasPendingCloudChanges ? "pending" : "confirmed",
                isDelivered = order.IsDelivered,
                deliveredAt = order.DeliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                deliveredBy = order.DeliveredBy,
                deliveredRevision = order.DeliveredRevision,
                printedRevision = order.PrintedRevision,
                noteRevision = order.NoteRevision,
                note = order.Note,
                hasIssues = order.Status == OrderStatus.NeedsReview || order.Status == OrderStatus.Error,
                isLocked = order.Status == OrderStatus.Locked,
                customerBillId = order.Items.FirstOrDefault(i => i.CustomerBillId != null)?.CustomerBillId,
                items = order.Items.Select(i => new
                {
                    id = i.Id,
                    specName = i.SpecificationFolderName,
                    variantName = i.PrintSpecification?.CanonicalName ?? i.SpecificationFolderName,
                    size = i.PrintSpecification?.CanonicalSize,
                    billQuantity = i.BillQuantity ?? i.PrintCount ?? 0,
                    printCount = i.PrintCount,
                    printFolder = i.SelectedPrintFolderRelativePath,
                    resolutionMode = i.FolderResolutionMode.ToString()
                })
            });
        });

        // 5.2 Toggle Delivered Status (Legacy Adapter)
        app.MapPost("/api/orders/{id:long}/toggle-delivered", async (long id, HttpContext ctx) =>
        {
            var actor = CheckAuth(ctx);
            if (!actor.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null) return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });

            var handler = _serviceProvider.GetService<OrderOperationalCommandHandler>();
            if (handler != null)
            {
                // Legacy adapter: server-generated operation ID is NOT retry-safe
                string operationId = Guid.NewGuid().ToString("N");
                bool target = !order.IsDelivered;

                try
                {
                    await handler.SetDeliveredAsync(actor, id, new SetOrderDeliveredRequest(operationId, order.DeliveredRevision, target), cancellationToken: ctx.RequestAborted);
                    var updated = await orderRepo.GetOrderByIdAsync(id);
                    return Results.Ok(new
                    {
                        id = order.Id,
                        syncState = updated?.HasPendingCloudChanges == true ? "pending" : "confirmed",
                        isDelivered = updated?.IsDelivered ?? target,
                        deliveredAt = updated?.DeliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                        deliveredBy = updated?.DeliveredBy
                    });
                }
                catch (StaleRevisionException)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.StaleRevision,
                        message = "Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại."
                    }, statusCode: StatusCodes.Status409Conflict);
                }
                catch (CapabilityDeniedException)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.ForbiddenCapability,
                        message = "Bạn không có quyền thực hiện thao tác này."
                    }, statusCode: StatusCodes.Status403Forbidden);
                }
            }
            else
            {
                bool newStatus = !order.IsDelivered;
                DateTimeOffset? deliveredAt = newStatus ? DateTimeOffset.Now : null;
                string? deliveredBy = newStatus ? (actor.Role == "Admin" ? "Chủ tiệm" : "Nhân viên") : null;

                await orderRepo.UpdateOrderDeliveredStatusAsync(id, newStatus, deliveredAt, deliveredBy);

                return Results.Ok(new
                {
                    id = order.Id,
                    syncState = (await orderRepo.GetOrderByIdAsync(id))!.HasPendingCloudChanges ? "pending" : "confirmed",
                    isDelivered = newStatus,
                    deliveredAt = deliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    deliveredBy = deliveredBy
                });
            }
        });

        // 5.3 Toggle Printed Status (Legacy Adapter)
        app.MapPost("/api/orders/{id:long}/toggle-printed", async (long id, HttpContext ctx) =>
        {
            var actor = CheckAuth(ctx);
            if (!actor.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null) return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });

            var handler = _serviceProvider.GetService<OrderOperationalCommandHandler>();
            if (handler != null)
            {
                // Legacy adapter: server-generated operation ID is NOT retry-safe
                string operationId = Guid.NewGuid().ToString("N");
                bool target = !order.IsPrinted;

                try
                {
                    await handler.SetPrintedAsync(actor, id, new SetOrderPrintedRequest(operationId, order.PrintedRevision, target), cancellationToken: ctx.RequestAborted);
                    var updated = await orderRepo.GetOrderByIdAsync(id);
                    return Results.Ok(new
                    {
                        id = order.Id,
                        isPrinted = updated?.IsPrinted ?? target,
                        printedAt = updated?.PrintedAt?.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
                catch (StaleRevisionException)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.StaleRevision,
                        message = "Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại."
                    }, statusCode: StatusCodes.Status409Conflict);
                }
                catch (CapabilityDeniedException)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.ForbiddenCapability,
                        message = "Bạn không có quyền thực hiện thao tác này."
                    }, statusCode: StatusCodes.Status403Forbidden);
                }
            }
            else
            {
                bool newStatus = !order.IsPrinted;
                DateTimeOffset? printedAt = newStatus ? DateTimeOffset.Now : null;

                await orderRepo.UpdateOrderPrintedStatusAsync(id, newStatus, printedAt);

                return Results.Ok(new
                {
                    id = order.Id,
                    isPrinted = newStatus,
                    printedAt = printedAt?.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
        });

        // 5.4 Update Order Note (Legacy Adapter)
        app.MapPost("/api/orders/{id:long}/note", async (long id, HttpContext ctx) =>
        {
            var actor = CheckAuth(ctx);
            if (!actor.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null) return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            string? note = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("note", out var noteProp))
                    {
                        note = noteProp.GetString();
                    }
                }
                catch { }
            }

            var handler = _serviceProvider.GetService<OrderOperationalCommandHandler>();
            if (handler != null)
            {
                // Legacy adapter: server-generated operation ID is NOT retry-safe
                string operationId = Guid.NewGuid().ToString("N");

                try
                {
                    await handler.SetNoteAsync(actor, id, new SetOrderNoteRequest(operationId, order.NoteRevision, note), cancellationToken: ctx.RequestAborted);
                    var updated = await orderRepo.GetOrderByIdAsync(id);
                    return Results.Ok(new
                    {
                        id = order.Id,
                        note = updated?.Note,
                        syncState = updated?.HasPendingCloudChanges == true ? "pending" : "confirmed"
                    });
                }
                catch (StaleRevisionException)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.StaleRevision,
                        message = "Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại."
                    }, statusCode: StatusCodes.Status409Conflict);
                }
                catch (CapabilityDeniedException)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.ForbiddenCapability,
                        message = "Bạn không có quyền thực hiện thao tác này."
                    }, statusCode: StatusCodes.Status403Forbidden);
                }
                catch (CommandValidationException ex)
                {
                    return Results.Json(new
                    {
                        success = false,
                        code = ApiErrorCodes.ValidationFailed,
                        message = ex.Message
                    }, statusCode: StatusCodes.Status422UnprocessableEntity);
                }
            }
            else
            {
                note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
                await orderRepo.UpdateOrderNoteAsync(id, note);
                return Results.Ok(new { id = order.Id, note = note, syncState = (await orderRepo.GetOrderByIdAsync(id))!.HasPendingCloudChanges ? "pending" : "confirmed" });
            }
        });

        // 5.5 Print Order Label (Remote Thermal Print from Mobile)
        app.MapPost("/api/orders/{id:long}/print-label", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null) return Results.NotFound(new { message = "Không tìm thấy đơn hàng." });

            var labelExporter = _serviceProvider.GetRequiredService<IShippingLabelExporter>();
            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var settings = await _settingsRepository.GetSettingsAsync();

            long? billId = order.Items?.FirstOrDefault(i => i.CustomerBillId != null)?.CustomerBillId;
            CustomerBill? bill = null;
            if (auth.Has(Capabilities.FinanceRead) && billId.HasValue)
            {
                bill = await billRepo.GetByIdAsync(billId.Value);
            }

            try
            {
                if (bill != null)
                {
                    await labelExporter.PrintShippingLabelAsync(bill, silent: true);
                }
                else
                {
                    await labelExporter.PrintOrderShippingLabelAsync(order, silent: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to print label for order {Id}", id);
                return Results.Problem(detail: $"Lỗi khi in tem: {ex.Message}", statusCode: 500);
            }

            bool markedDelivered = false;
            DateTimeOffset? deliveredAt = null;
            string? deliveredBy = null;

            if (settings.AutoMarkDeliveredOnPrint)
            {
                deliveredAt = DateTimeOffset.Now;
                deliveredBy = auth.Role == "Admin" ? "Chủ tiệm (Mobile)" : "Nhân viên (Mobile)";
                await orderRepo.UpdateOrderDeliveredStatusAsync(id, true, deliveredAt, deliveredBy);
                markedDelivered = true;
            }

            string orderIdentifier = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : order.OriginalFolderName;
            return Results.Ok(new
            {
                success = true,
                message = markedDelivered 
                    ? $"Đã in tem và đánh dấu ĐÃ GIAO cho đơn '{orderIdentifier}'."
                    : $"Đã in tem cho đơn '{orderIdentifier}'.",
                isDelivered = markedDelivered ? true : order.IsDelivered,
                deliveredAt = deliveredAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? order.DeliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                deliveredBy = deliveredBy ?? order.DeliveredBy
            });
        });

        // 5.6 Get Order Label Image (Preview Order or Bill Label)
        app.MapGet("/api/orders/{id:long}/label", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null) return Results.NotFound();

            var labelExporter = _serviceProvider.GetRequiredService<IShippingLabelExporter>();
            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();

            long? billId = order.Items?.FirstOrDefault(i => i.CustomerBillId != null)?.CustomerBillId;
            if (auth.Has(Capabilities.FinanceRead) && billId.HasValue)
            {
                var bill = await billRepo.GetByIdAsync(billId.Value);
                if (bill != null)
                {
                    try
                    {
                        string billLabelPath = await labelExporter.ExportShippingLabelImageAsync(bill);
                        if (File.Exists(billLabelPath))
                        {
                            return Results.File(billLabelPath, "image/png");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to export shipping label for bill {BillId}", bill.Id);
                    }
                }
            }

            try
            {
                string orderLabelPath = await labelExporter.ExportOrderShippingLabelImageAsync(order);
                if (File.Exists(orderLabelPath))
                {
                    return Results.File(orderLabelPath, "image/png");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to export order shipping label for order {OrderId}", id);
            }

            return Results.NotFound(new { message = "Không thể tạo hình ảnh tem giao hàng." });
        });

        // 6. Bill Detail
        app.MapGet("/api/bills/{id:long}", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();

            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var bill = await billRepo.GetByIdAsync(id);
            if (bill == null) return Results.NotFound(new { message = "Không tìm thấy hóa đơn" });

            bool isAdmin = auth.Has(Capabilities.FinanceRead);
            var billSettings = await _settingsRepository.GetSettingsAsync();

            return Results.Ok(new
            {
                id = bill.Id,
                billNumber = bill.BillNumber,
                billType = bill.BillType.ToString(),
                customerName = bill.CustomerNameSnapshot,
                phone = bill.PhoneSnapshot,
                shippingAddress = bill.ShippingAddressSnapshot,
                periodStart = bill.PeriodStart,
                periodEnd = bill.PeriodEnd,
                status = bill.Status.ToString(),
                syncState = bill.HasPendingCloudChanges ? "pending" : "confirmed",
                isPaid = bill.IsPaid,
                paidAt = bill.PaidAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                note = bill.Note,
                hasPaymentQr = isAdmin && !string.IsNullOrWhiteSpace(billSettings.BankAccountNumber) && !string.IsNullOrWhiteSpace(billSettings.BankBinOrCode),
                hasExportFile = isAdmin && !string.IsNullOrWhiteSpace(bill.ExportFilePath) && File.Exists(bill.ExportFilePath),
                // Financials visible only to Admin
                productSubtotal = isAdmin ? bill.ProductSubtotal : (long?)null,
                adjustmentsTotal = isAdmin ? bill.AdjustmentsTotal : (long?)null,
                grandTotal = isAdmin ? bill.GrandTotal : (long?)null,
                lines = BillSnapshotLines.Included(bill).Select(l => new
                {
                    id = l.Id,
                    description = l.ProductNameSnapshot,
                    size = l.VariantSnapshot,
                    quantity = l.BilledQuantity,
                    unitPrice = isAdmin ? l.BilledUnitPrice : (long?)null,
                    lineTotal = isAdmin ? l.LineTotal : (long?)null
                }),
                adjustments = isAdmin ? bill.Adjustments.Select(a => new
                {
                    id = a.Id,
                    description = a.Label,
                    amount = a.Amount,
                    type = a.Type.ToString(),
                    direction = a.Direction.ToString()
                }) : null
            });
        });

        // 6.1 Bill: Toggle Payment Status
        app.MapPost("/api/bills/{id:long}/toggle-payment", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(403);

            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var bill = await billRepo.GetByIdAsync(id);
            if (bill == null) return Results.NotFound(new { message = "Không tìm thấy hóa đơn" });

            bool newPaid = !bill.IsPaid;
            DateTimeOffset? paidAt = newPaid ? DateTimeOffset.UtcNow : null;
            await billRepo.SetPaymentStatusAsync(id, newPaid, paidAt);

            return Results.Ok(new
            {
                success = true,
                syncState = (await billRepo.GetByIdAsync(id))!.HasPendingCloudChanges ? "pending" : "confirmed",
                isPaid = newPaid,
                paidAt = paidAt?.ToString("yyyy-MM-dd HH:mm:ss")
            });
        });

        // 7. Bill Image (Serve JPG or export on fly)
        app.MapGet("/api/bills/{id:long}/image", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(403);

            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var bill = await billRepo.GetByIdAsync(id);
            if (bill == null) return Results.NotFound();

            string? filePath = bill.ExportFilePath;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                try
                {
                    var exporter = _serviceProvider.GetRequiredService<IJpegBillExporter>();
                    filePath = await exporter.ExportBillToJpegAsync(bill);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to export bill image for bill {Id}", id);
                    return Results.NotFound(new { message = "Chưa có ảnh hóa đơn và không thể tạo tự động." });
                }
            }

            if (!File.Exists(filePath)) return Results.NotFound();
            return Results.File(filePath, "image/jpeg");
        });

        // 8. Shipping Label Image (Serve label image or export on fly)
        app.MapGet("/api/bills/{id:long}/label", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(403);

            var labelExporter = _serviceProvider.GetRequiredService<IShippingLabelExporter>();
            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var bill = await billRepo.GetByIdAsync(id);
            if (bill != null)
            {
                try
                {
                    string labelPath = await labelExporter.ExportShippingLabelImageAsync(bill);
                    if (File.Exists(labelPath))
                    {
                        return Results.File(labelPath, "image/png");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to export shipping label for bill {Id}", id);
                }
            }

            if (ctx.Request.Query.TryGetValue("orderId", out var orderIdVal) && long.TryParse(orderIdVal, out var orderId) && orderId > 0)
            {
                var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
                var order = await orderRepo.GetOrderByIdAsync(orderId);
                if (order != null)
                {
                    try
                    {
                        string orderLabelPath = await labelExporter.ExportOrderShippingLabelImageAsync(order);
                        if (File.Exists(orderLabelPath))
                        {
                            return Results.File(orderLabelPath, "image/png");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to export fallback order shipping label for order {OrderId}", orderId);
                    }
                }
            }

            return Results.NotFound(new { message = "Không thể tạo tem giao hàng." });
        });

        // 8.1 Print Shipping Label directly to Thermal Printer
        app.MapPost("/api/bills/{id:long}/print", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(403);

            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var orderRepo = _serviceProvider.GetRequiredService<IOrderRepository>();
            var labelExporter = _serviceProvider.GetRequiredService<IShippingLabelExporter>();
            var settings = await _settingsRepository.GetSettingsAsync();

            var bill = await billRepo.GetByIdAsync(id);
            Order? fallbackOrder = null;

            if (bill == null)
            {
                if (ctx.Request.Query.TryGetValue("orderId", out var orderIdVal) && long.TryParse(orderIdVal, out var queryOrderId) && queryOrderId > 0)
                {
                    fallbackOrder = await orderRepo.GetOrderByIdAsync(queryOrderId);
                }

                if (fallbackOrder == null)
                {
                    return Results.NotFound(new { message = "Không tìm thấy hóa đơn." });
                }
            }

            try
            {
                if (bill != null)
                {
                    await labelExporter.PrintShippingLabelAsync(bill, silent: true);
                }
                else if (fallbackOrder != null)
                {
                    await labelExporter.PrintOrderShippingLabelAsync(fallbackOrder, silent: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to print shipping label for bill {BillId}", id);
                return Results.Problem(detail: $"Lỗi khi in tem hóa đơn: {ex.Message}", statusCode: 500);
            }

            bool markedDelivered = false;
            DateTimeOffset? deliveredAt = null;
            string? deliveredBy = null;

            if (settings.AutoMarkDeliveredOnPrint)
            {
                deliveredAt = DateTimeOffset.Now;
                deliveredBy = auth.Role == "Admin" ? "Chủ tiệm (Mobile)" : "Nhân viên (Mobile)";

                if (bill?.Orders != null && bill.Orders.Count > 0)
                {
                    foreach (var ord in bill.Orders.Where(o => o.IsIncluded && o.OrderId > 0))
                    {
                        await orderRepo.UpdateOrderDeliveredStatusAsync(ord.OrderId, true, deliveredAt, deliveredBy);
                    }
                    markedDelivered = true;
                }
                else if (fallbackOrder != null)
                {
                    await orderRepo.UpdateOrderDeliveredStatusAsync(fallbackOrder.Id, true, deliveredAt, deliveredBy);
                    markedDelivered = true;
                }
            }

            string targetName = bill != null
                ? $"hóa đơn '{bill.BillNumber}'"
                : $"đơn hàng '{fallbackOrder?.OrderCode ?? fallbackOrder?.OriginalFolderName}'";

            return Results.Ok(new
            {
                success = true,
                message = markedDelivered
                    ? $"Đã in tem bưu kiện và đánh dấu ĐÃ GIAO cho {targetName}."
                    : $"Đã in tem bưu kiện cho {targetName}.",
                isDelivered = markedDelivered,
                deliveredAt = deliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                deliveredBy = deliveredBy
            });
        });

        // 9. VietQR image for bill
        app.MapGet("/api/bills/{id:long}/qr", async (long id, HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(403);

            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var bill = await billRepo.GetByIdAsync(id);
            if (bill == null) return Results.NotFound();

            var settings = await _settingsRepository.GetSettingsAsync();
            if (string.IsNullOrWhiteSpace(settings.BankAccountNumber) || string.IsNullOrWhiteSpace(settings.BankBinOrCode))
            {
                return Results.BadRequest(new { message = "Chưa cấu hình tài khoản ngân hàng trong phần Cài đặt." });
            }

            string qrText = LalabAutoReport.Infrastructure.Reporting.VietQrGenerator.BuildVietQrPayload(
                bankBin: settings.BankBinOrCode,
                accountNumber: settings.BankAccountNumber,
                amount: bill.GrandTotal,
                memo: bill.BillNumber
            );

            using var qrGen = new QRCoder.QRCodeGenerator();
            using var qrData = qrGen.CreateQrCode(qrText, QRCoder.QRCodeGenerator.ECCLevel.Q);
            using var pngQr = new QRCoder.PngByteQRCode(qrData);
            byte[] qrBytes = pngQr.GetGraphic(8);
            return Results.File(qrBytes, "image/png");
        });

        // 10. Reports: Daily (Admin Only)
        app.MapGet("/api/reports/daily", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            string date = ctx.Request.Query.TryGetValue("date", out var d) ? d.ToString() : DateTime.Today.ToString("yyyy-MM-dd");
            var reportService = _serviceProvider.GetRequiredService<IReportService>();
            var report = await reportService.GetDailyReportAsync(date);

            return Results.Ok(report);
        });

        // 11. Reports: Monthly (Admin Only)
        app.MapGet("/api/reports/monthly", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            int year = ctx.Request.Query.TryGetValue("year", out var y) && int.TryParse(y, out var parsedY) ? parsedY : DateTime.Today.Year;
            int month = ctx.Request.Query.TryGetValue("month", out var m) && int.TryParse(m, out var parsedM) ? parsedM : DateTime.Today.Month;

            var reportService = _serviceProvider.GetRequiredService<IReportService>();
            var report = await reportService.GetMonthlyReportAsync(year, month);

            return Results.Ok(report);
        });

        // 12. Customers & Debts (Admin Only)
        app.MapGet("/api/customers/unpaid", async (HttpContext ctx) =>
        {
            var auth = CheckAuth(ctx);
            if (!auth.IsAuthenticated) return Results.Unauthorized();
            if (!auth.Has(Capabilities.FinanceRead)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var billRepo = _serviceProvider.GetRequiredService<ICustomerBillRepository>();
            var unpaidBills = await billRepo.GetUnpaidBillsAsync();

            var grouped = unpaidBills
                .GroupBy(b => new { Id = b.CustomerId ?? 0, Name = b.CustomerNameSnapshot })
                .Select(g => new
                {
                    customerId = g.Key.Id,
                    customerName = g.Key.Name,
                    unpaidBillCount = g.Count(),
                    totalDebt = g.Sum(b => b.GrandTotal),
                    bills = g.Select(b => new
                    {
                        id = b.Id,
                        billNumber = b.BillNumber,
                        date = b.PeriodEnd,
                        grandTotal = b.GrandTotal
                    })
                })
                .OrderByDescending(c => c.totalDebt)
                .ToList();

            return Results.Ok(grouped);
        });

        // Service Worker for PWA
        app.MapGet("/sw.js", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/javascript";
            ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            await ctx.Response.WriteAsync("self.addEventListener('install', e => self.skipWaiting()); self.addEventListener('activate', e => e.waitUntil(clients.claim())); self.addEventListener('fetch', e => e.respondWith(fetch(e.request)));");
        });

        // Local Tailwind runtime (vendored) so the LAN PWA works without internet.
        app.MapGet("/assets/tailwind.js", (HttpContext ctx) =>
        {
            var stream = typeof(KestrelMobileWebServer).Assembly
                .GetManifestResourceStream("LalabAutoReport.Infrastructure.Web.Assets.tailwind.js");
            if (stream == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Stream(stream, "text/javascript; charset=utf-8");
        });

        app.MapGet("/assets/tailwind.LICENSE.txt", (HttpContext ctx) =>
        {
            var stream = typeof(KestrelMobileWebServer).Assembly
                .GetManifestResourceStream("LalabAutoReport.Infrastructure.Web.Assets.tailwind.LICENSE.txt");
            if (stream == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Stream(stream, "text/plain; charset=utf-8");
        });

        // Api v1 endpoints (Phase 2 Auth)
        app.MapApiV1(_serviceProvider);

        // 13. Mobile SPA HTML / Assets fallback
        app.MapFallback(async (HttpContext ctx) =>
        {
            string path = ctx.Request.Path.Value ?? "";
            
            // If requesting manifest or icons
            if (path == "/manifest.json")
            {
                ctx.Response.ContentType = "application/manifest+json";
                await ctx.Response.WriteAsync(GetManifestJson());
                return;
            }

            // Return main Mobile SPA HTML
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            await ctx.Response.WriteAsync(MobileSpaHtmlProvider.GetIndexHtml());
        });
    }

    private ActorContext CheckAuth(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue("ActorContext", out var obj) && obj is ActorContext actor)
        {
            return actor;
        }

        return AuthenticateAsync(ctx).GetAwaiter().GetResult();
    }

    public async Task<ActorContext> AuthenticateAsync(HttpContext ctx)
    {
        string? token = null;

        if (ctx.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            string val = authHeader.ToString();
            if (val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = val.Substring("Bearer ".Length).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                string path = ctx.Request.Path.Value ?? "";
                if (ImageQueryTokenAllowedRegex.IsMatch(path) && (ctx.Request.Query.TryGetValue("token", out var queryToken) || ctx.Request.Query.TryGetValue("auth", out queryToken)))
                {
                    token = queryToken.ToString().Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ActorContext.Anonymous();
        }

        if (token.StartsWith("lsv2.", StringComparison.Ordinal))
        {
            var sessionService = _serviceProvider.GetService<ISessionService>();
            if (sessionService != null)
            {
                var v2Actor = await sessionService.ValidateTokenAsync(token);
                if (v2Actor != null)
                {
                    return v2Actor;
                }
            }
            return ActorContext.Anonymous();
        }

        string reqPath = ctx.Request.Path.Value ?? "";
        if (reqPath.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return ActorContext.Anonymous();
        }

        if (!_authService.TryValidateToken(token, out var role, out var issuedAt))
        {
            return ActorContext.Anonymous();
        }

        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Items["LegacyTokenWrite"] = true;
            return ActorContext.Anonymous();
        }

        var credStore = _serviceProvider.GetService<IRoleCredentialStore>();
        DateTimeOffset legacyGraceUntil = DateTimeOffset.MaxValue;
        DateTimeOffset legacyIssuedCutoff = DateTimeOffset.MinValue;
        if (credStore != null)
        {
            try
            {
                var status = await credStore.GetStatusAsync();
                legacyGraceUntil = status.LegacyTokensValidUntilUtc;
                legacyIssuedCutoff = status.LegacyTokensIssuedBeforeUtc;
            }
            catch { }
        }

        if (DateTimeOffset.UtcNow >= legacyGraceUntil)
        {
            return ActorContext.Anonymous();
        }

        if (issuedAt < legacyIssuedCutoff)
        {
            return ActorContext.FromLegacyToken(role.ToString(), RoleCapabilityMap.GetCapabilities(role.ToString()));
        }

        return ActorContext.Anonymous();
    }

    private static string GetManifestJson()
    {
        return @"{
  ""name"": ""Lalab Auto Report"",
  ""short_name"": ""Lalab Mobile"",
  ""start_url"": ""/"",
  ""display"": ""standalone"",
  ""background_color"": ""#0f172a"",
  ""theme_color"": ""#1e293b"",
  ""icons"": [
    {
      ""src"": ""/icon.png"",
      ""sizes"": ""192x192"",
      ""type"": ""image/png""
    }
  ]
}";
    }

    private static string? FindFirstImageRelativePath(string orderFolder, string rootFolder)
    {
        try
        {
            if (!Directory.Exists(orderFolder)) return null;

            var extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".psd", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng"
            };

            var files = Directory.EnumerateFiles(orderFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => extSet.Contains(Path.GetExtension(f)))
                .ToList();

            if (files.Count == 0) return null;

            var preferred = files.Where(f =>
            {
                var ext = Path.GetExtension(f);
                return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            var targetList = preferred.Count > 0 ? preferred : files;
            targetList.Sort(NaturalStringComparer.Instance);

            return Path.GetRelativePath(rootFolder, targetList[0]);
        }
        catch
        {
            return null;
        }
    }

    private void NotifyStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
