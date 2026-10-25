using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LalabAutoReport.Infrastructure.Web;

public static class ApiV1Endpoints
{
    public static void MapApiV1(this IEndpointRouteBuilder endpoints, IServiceProvider serviceProvider)
    {
        var group = endpoints.MapGroup("/api/v1");

        // 1. Create session (Login)
        group.MapPost("/auth/sessions", async (HttpContext ctx) =>
        {
            var sessionService = serviceProvider.GetService<ISessionService>();
            if (sessionService == null)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ServerBusy, "Dịch vụ xác thực chưa sẵn sàng."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            LoginRequestDto? request = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    request = JsonSerializer.Deserialize<LoginRequestDto>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
            }
            catch
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Dữ liệu yêu cầu không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            string pin = request?.Pin ?? "";
            string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var result = await sessionService.LoginAsync(
                pin: pin,
                deviceId: request?.DeviceId,
                deviceLabel: request?.DeviceLabel,
                clientKind: request?.ClientKind ?? "desktop",
                ip: ip);

            if (result.Status == LoginResultStatus.RateLimited)
            {
                int retrySeconds = (int)Math.Ceiling(result.RetryAfter?.TotalSeconds ?? 60);
                ctx.Response.Headers.RetryAfter = retrySeconds.ToString();
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.LoginRateLimited, result.ErrorMessage ?? "Quá nhiều lần thử sai."),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (result.Status == LoginResultStatus.NotConfigured)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ServerBusy, result.ErrorMessage ?? "Chưa cấu hình mã PIN."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (result.Status == LoginResultStatus.InvalidCredentials || result.Role == null || result.Token == null)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, result.ErrorMessage ?? "Mã PIN không chính xác."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var settingsRepo = serviceProvider.GetService<ISettingsRepository>();
            var settings = settingsRepo != null ? await settingsRepo.GetSettingsAsync() : null;

            var response = new LoginResponseDto(
                Success: true,
                Token: result.Token,
                SessionId: result.SessionId,
                Role: result.Role,
                WorkshopName: settings?.WorkshopName,
                Capabilities: result.Capabilities?.ToList(),
                ExpiresAtUtc: result.ExpiresAtUtc);

            return Results.Ok(response);
        });

        // 2. Current session info
        group.MapGet("/auth/sessions/current", (HttpContext ctx) =>
        {
            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated || string.IsNullOrEmpty(actor.SessionId))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new CurrentSessionResponseDto(
                SessionId: actor.SessionId,
                Role: actor.Role,
                Capabilities: actor.Capabilities.ToList(),
                ExpiresAtUtc: actor.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(30),
                ClientKind: actor.ClientKind ?? "desktop",
                DeviceId: actor.DeviceId,
                DeviceLabel: actor.DeviceLabel));
        });

        // 3. Logout / revoke current session
        group.MapDelete("/auth/sessions/current", async (HttpContext ctx) =>
        {
            var sessionService = serviceProvider.GetService<ISessionService>();
            if (sessionService == null)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ServerBusy, "Dịch vụ xác thực chưa sẵn sàng."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated || string.IsNullOrEmpty(actor.SessionId))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await sessionService.RevokeSessionAsync(actor.SessionId, "logout", actor);
            return Results.Ok(new { success = true });
        });

        // 4. Admin revoke all sessions
        group.MapPost("/admin/sessions/revoke-all", async (HttpContext ctx) =>
        {
            var sessionService = serviceProvider.GetService<ISessionService>();
            if (sessionService == null)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ServerBusy, "Dịch vụ xác thực chưa sẵn sàng."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu đăng nhập."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!actor.Has(Capabilities.AdminSessions))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ForbiddenCapability, "Không có quyền thu hồi phiên làm việc."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            RevokeAllSessionsRequestDto? req = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    req = JsonSerializer.Deserialize<RevokeAllSessionsRequestDto>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch
                {
                    return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Dữ liệu yêu cầu không hợp lệ."),
                        statusCode: StatusCodes.Status422UnprocessableEntity);
                }
            }

            await sessionService.RevokeAllAsync(req?.Role, "admin", actor);
            return Results.Ok(new { success = true });
        });

        // 5. Order Operational State (Read)
        group.MapGet("/orders/{id:long}/operational-state", async (long id, HttpContext ctx) =>
        {
            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!actor.Has(Capabilities.OrdersRead))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ForbiddenCapability, "Không có quyền đọc trạng thái đơn hàng."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var orderRepo = serviceProvider.GetRequiredService<IOrderRepository>();
            var order = await orderRepo.GetOrderByIdAsync(id);
            if (order == null)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.NotFound, "Không tìm thấy đơn hàng."),
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(new
            {
                delivered = new
                {
                    value = order.IsDelivered,
                    at = order.DeliveredAt?.ToString("o"),
                    by = order.DeliveredBy,
                    revision = order.DeliveredRevision
                },
                printed = new
                {
                    value = order.IsPrinted,
                    at = order.PrintedAt?.ToString("o"),
                    revision = order.PrintedRevision
                },
                note = new
                {
                    value = order.Note,
                    revision = order.NoteRevision
                }
            });
        });

        // 6. Set Order Delivered (Command)
        group.MapPost("/orders/{id:long}/delivered", async (long id, HttpContext ctx) =>
        {
            var headerCheck = ValidateClientApiHeader(ctx);
            if (headerCheck != null) return headerCheck;

            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!actor.Has(Capabilities.OrdersSetDelivered))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ForbiddenCapability, "Không có quyền thực hiện thao tác này."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            SetOrderDeliveredRequest? req = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    req = JsonSerializer.Deserialize<SetOrderDeliveredRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Dữ liệu yêu cầu không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (req == null || string.IsNullOrWhiteSpace(req.OperationId) || !Guid.TryParse(req.OperationId, out _))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Mã thao tác (operationId) không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var handler = serviceProvider.GetRequiredService<OrderOperationalCommandHandler>();
            try
            {
                var result = await handler.SetDeliveredAsync(actor, id, req, cancellationToken: ctx.RequestAborted);
                return Results.Ok(result.FieldState);
            }
            catch (Exception ex)
            {
                return MapOperationalException(ex);
            }
        });

        // 7. Set Order Printed (Command)
        group.MapPost("/orders/{id:long}/printed", async (long id, HttpContext ctx) =>
        {
            var headerCheck = ValidateClientApiHeader(ctx);
            if (headerCheck != null) return headerCheck;

            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!actor.Has(Capabilities.OrdersSetPrinted))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ForbiddenCapability, "Không có quyền thực hiện thao tác này."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            SetOrderPrintedRequest? req = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    req = JsonSerializer.Deserialize<SetOrderPrintedRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Dữ liệu yêu cầu không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (req == null || string.IsNullOrWhiteSpace(req.OperationId) || !Guid.TryParse(req.OperationId, out _))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Mã thao tác (operationId) không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var handler = serviceProvider.GetRequiredService<OrderOperationalCommandHandler>();
            try
            {
                var result = await handler.SetPrintedAsync(actor, id, req, cancellationToken: ctx.RequestAborted);
                return Results.Ok(result.FieldState);
            }
            catch (Exception ex)
            {
                return MapOperationalException(ex);
            }
        });

        // 8. Set Order Note (Command)
        group.MapPost("/orders/{id:long}/note", async (long id, HttpContext ctx) =>
        {
            var headerCheck = ValidateClientApiHeader(ctx);
            if (headerCheck != null) return headerCheck;

            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!actor.Has(Capabilities.OrdersEditNote))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ForbiddenCapability, "Không có quyền thực hiện thao tác này."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            SetOrderNoteRequest? req = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    req = JsonSerializer.Deserialize<SetOrderNoteRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Dữ liệu yêu cầu không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (req == null || string.IsNullOrWhiteSpace(req.OperationId) || !Guid.TryParse(req.OperationId, out _))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Mã thao tác (operationId) không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (req.Note != null && req.Note.Length > 4000)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Ghi chú không được vượt quá 4000 ký tự."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var handler = serviceProvider.GetRequiredService<OrderOperationalCommandHandler>();
            try
            {
                var result = await handler.SetNoteAsync(actor, id, req, cancellationToken: ctx.RequestAborted);
                return Results.Ok(result.FieldState);
            }
            catch (Exception ex)
            {
                return MapOperationalException(ex);
            }
        });

        // 9. Operation Lookup
        group.MapGet("/operations/{operationId}", async (string operationId, HttpContext ctx) =>
        {
            if (ctx.Items["ActorContext"] is not ActorContext actor || !actor.IsAuthenticated)
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.Unauthorized, "Yêu cầu phiên đăng nhập hợp lệ."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(operationId) || !Guid.TryParse(operationId, out _))
            {
                return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, "Mã thao tác (operationId) không hợp lệ."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            string normalizedOpId = Guid.Parse(operationId).ToString("N");

            var lookupStore = serviceProvider.GetRequiredService<IOperationLookupStore>();
            var lookup = await lookupStore.LookupOperationAsync(normalizedOpId, ctx.RequestAborted);

            if (lookup.Status == OperationLookupStatus.Committed && lookup.Capability != null)
            {
                if (actor.Has(lookup.Capability))
                {
                    object? resultObj = null;
                    if (!string.IsNullOrWhiteSpace(lookup.ResultJson))
                    {
                        try
                        {
                            resultObj = JsonDocument.Parse(lookup.ResultJson).RootElement;
                        }
                        catch { }
                    }

                    return Results.Ok(new
                    {
                        status = "committed",
                        capability = lookup.Capability,
                        entityType = lookup.EntityType,
                        entityId = lookup.EntityId,
                        result = resultObj
                    });
                }

                // If caller does not hold the receipt's capability, return not_found
                return Results.Ok(new { status = "not_found" });
            }

            var inFlight = serviceProvider.GetRequiredService<InFlightOperationRegistry>();
            if (inFlight.IsInFlight(normalizedOpId))
            {
                return Results.Ok(new { status = "pending" });
            }

            return Results.Ok(new { status = "not_found" });
        });
    }

    private static IResult? ValidateClientApiHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Lalab-Client-Api", out var headerVal) || string.IsNullOrWhiteSpace(headerVal))
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ClientTooOld, "Yêu cầu tiêu đề X-Lalab-Client-Api: 1.x."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }

        string versionStr = headerVal.ToString().Trim();
        var parts = versionStr.Split('.');
        if (!int.TryParse(parts[0], out int major))
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ClientTooOld, "Phiên bản client API không hợp lệ."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }
        if (major < 1)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ClientTooOld, "Ứng dụng máy khách quá cũ. Vui lòng tải lại trang."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }
        if (major > 1)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ServerTooOld, "Máy chủ chưa hỗ trợ phiên bản API này."),
                statusCode: StatusCodes.Status426UpgradeRequired);
        }

        return null;
    }

    private static IResult MapOperationalException(Exception ex)
    {
        string correlationId = Guid.NewGuid().ToString("N");
        if (ex is StaleRevisionException stale)
        {
            return Results.Json(new
            {
                code = ApiErrorCodes.StaleRevision,
                message = stale.Message,
                entityType = stale.EntityType,
                entityId = stale.EntityId,
                fieldGroup = stale.FieldGroup,
                currentRevision = stale.CurrentRevision,
                currentValue = stale.CurrentValue,
                correlationId = correlationId
            }, statusCode: StatusCodes.Status409Conflict);
        }

        if (ex is IdempotencyConflictException idemp)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.IdempotencyConflict, idemp.Message, correlationId),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (ex is OperationInProgressException oip)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.OperationInProgress, oip.Message, correlationId),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (ex is OrderNotFoundException onf)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.NotFound, onf.Message, correlationId),
                statusCode: StatusCodes.Status404NotFound);
        }

        if (ex is CommandValidationException cve)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ValidationFailed, cve.Message, correlationId),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (ex is CapabilityDeniedException)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ForbiddenCapability, "Không có quyền thực hiện thao tác này.", correlationId),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (ex is ServerBusyException sbe)
        {
            return Results.Json(new ApiErrorResponse(ApiErrorCodes.ServerBusy, sbe.Message, correlationId),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new ApiErrorResponse("INTERNAL_SERVER_ERROR", "Đã xảy ra lỗi nội bộ hệ thống.", correlationId),
            statusCode: StatusCodes.Status500InternalServerError);
    }
}
