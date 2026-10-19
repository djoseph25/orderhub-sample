using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Auth;
using OrderHub.Api.Data;
using OrderHub.Contracts;

namespace OrderHub.Api.Controllers.V1;

/// <summary>
/// Routes:
///   GET    /api/v1/products?page=1&amp;pageSize=20   read:products
///   GET    /api/v1/products/{id}                     read:products
///   POST   /api/v1/products                          write:products
///   PUT    /api/v1/products/{id}                     write:products  (optimistic concurrency via Version)
///   DELETE /api/v1/products/{id}                     write:products
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/products")]
[Authorize(Policy = Scopes.ProductsRead)]
public sealed class ProductsController(CatalogDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<ProductDto>>> List(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Week 11: AsNoTracking for read-only queries, and project with Select so only the needed columns are fetched.
        var query = db.Products.AsNoTracking().OrderBy(p => p.Id);
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Price, p.Version))
            .ToListAsync(ct);

        return new PagedResult<ProductDto>(items, page, pageSize, total);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductDto>> GetById(int id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Price, p.Version))
            .FirstOrDefaultAsync(ct);

        return product is null ? Problem(statusCode: 404, detail: $"Product {id} was not found.") : product;
    }

    [HttpPost]
    [Authorize(Policy = Scopes.ProductsWrite)]
    public async Task<ActionResult<ProductDto>> Create(
        CreateProductRequest request, [FromServices] IValidator<CreateProductRequest> validator, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        if (await db.Products.AnyAsync(p => p.Sku == request.Sku, ct))
        {
            return Problem(statusCode: 409, detail: $"SKU '{request.Sku}' already exists.");
        }

        var product = new Product { Sku = request.Sku, Name = request.Name, Price = request.Price, CreatedAt = time.GetUtcNow() };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        var dto = new ProductDto(product.Id, product.Sku, product.Name, product.Price, product.Version);
        return CreatedAtAction(nameof(GetById), new { id = product.Id, version = "1" }, dto);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Scopes.ProductsWrite)]
    public async Task<ActionResult<ProductDto>> Update(
        int id, UpdateProductRequest request, [FromServices] IValidator<UpdateProductRequest> validator, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Problem(statusCode: 404, detail: $"Product {id} was not found.");
        }

        // Tell EF which version the client last saw. If the row changed since, SaveChanges throws
        // DbUpdateConcurrencyException and GlobalExceptionHandler turns it into a 409.
        db.Entry(product).Property(p => p.Version).OriginalValue = request.Version;

        product.Name = request.Name;
        product.Price = request.Price;
        product.Version = Guid.NewGuid();
        product.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return new ProductDto(product.Id, product.Sku, product.Name, product.Price, product.Version);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Scopes.ProductsWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        // Week 11: set-based delete, a single SQL statement with no load-then-remove round trip.
        var deleted = await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Problem(statusCode: 404, detail: $"Product {id} was not found.") : NoContent();
    }
}
