using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

public static class CategoriesEndpoints
{
    public static IEndpointRouteBuilder MapCategoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories").RequireAuthorization();

        group.MapGet("/", GetCategories);

        return app;
    }

    /// <summary>GET /api/categories — все категории пользователя (доходы и расходы).</summary>
    private static async Task<List<CategoryDto>> GetCategories(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        return await db.Categories
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Id) // порядок создания: "Другое" окажется в конце своего списка
            .Select(c => new CategoryDto(c.Id, c.Name, c.Type))
            .ToListAsync(ct);
    }
}
