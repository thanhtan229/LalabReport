using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
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

            var response = new LoginResponseDto(
                Success: true,
                Token: result.Token,
                SessionId: result.SessionId,
                Role: result.Role,
                WorkshopName: null,
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
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(30), // informational
                ClientKind: "desktop",
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
                catch { }
            }

            await sessionService.RevokeAllAsync(req?.Role, "admin", actor);
            return Results.Ok(new { success = true });
        });
    }
}
