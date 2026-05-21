using System.Net;
using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Models;

namespace MerinoOne.SupplierPortal.Middlewares;

public class GlobalExceptionHandler
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandler> _logger;
    public GlobalExceptionHandler(RequestDelegate next, ILogger<GlobalExceptionHandler> logger) { _next = next; _logger = logger; }

    public async Task Invoke(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (ValidationException ve)  { await Write(ctx, HttpStatusCode.BadRequest, ve.Errors.SelectMany(kvp => kvp.Value).ToArray()); }
        catch (NotFoundException nfe)   { await Write(ctx, HttpStatusCode.NotFound, new[] { nfe.Message }); }
        catch (ForbiddenException fe)   { await Write(ctx, HttpStatusCode.Forbidden, new[] { fe.Message }); }
        catch (ConflictException ce)    { await Write(ctx, HttpStatusCode.Conflict, new[] { ce.Message }); }
        catch (UnauthorizedAccessException) { await Write(ctx, HttpStatusCode.Unauthorized, new[] { "Unauthorized." }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await Write(ctx, HttpStatusCode.InternalServerError, new[] { "An unexpected error occurred.", ex.Message });
        }
    }

    private static async Task Write(HttpContext ctx, HttpStatusCode status, string[] errors)
    {
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/json";
        var body = new Result { Success = false, Errors = errors.ToList(), TraceId = ctx.TraceIdentifier };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
