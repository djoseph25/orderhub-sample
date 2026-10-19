using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Auth;
using OrderHub.Api.Data;
using OrderHub.Contracts;

namespace OrderHub.Api.Controllers.V2;

/// <summary>
/// Week 12: v2 ships a breaking response-shape change (DisplayName, Money object) without breaking v1 callers.
///   GET /api/v2/products?page=1&amp;pageSize=20
/// </summary>
[ApiController]
[ApiVersion(2.0)]
[Route("api/v{version:apiVersion}/products")]
[Authorize(Policy = Scopes.ProductsRead)]
public sealed class ProductsController(CatalogDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<ProductV2Dto>>> List(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products.AsNoTracking().OrderBy(p => p.Id);
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductV2Dto(p.Id, p.Sku, p.Name + " (" + p.Sku + ")", new MoneyDto(p.Price, "USD")))
            .ToListAsync(ct);

        return new PagedResult<ProductV2Dto>(items, page, pageSize, total);
    }
}
