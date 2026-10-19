namespace OrderHub.Contracts;

public sealed record ProductDto(int Id, string Sku, string Name, decimal Price, Guid Version);

public sealed record CreateProductRequest(string Sku, string Name, decimal Price);

/// <summary>The caller sends back the <see cref="Version"/> it read, for optimistic concurrency (Week 11).</summary>
public sealed record UpdateProductRequest(string Name, decimal Price, Guid Version);

/// <summary>v2 response shape, showing a breaking change shipped behind API versioning (Week 12).</summary>
public sealed record ProductV2Dto(int Id, string Sku, string DisplayName, MoneyDto Price);

public sealed record MoneyDto(decimal Amount, string Currency);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
