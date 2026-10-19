using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Api.Auth;
using OrderHub.Contracts;

namespace OrderHub.Api.Tests;

[TestClass]
public sealed class ProductsApiTests
{
    private CatalogApiFactory _factory = null!;

    // A fresh app and empty database per test: tests can run in parallel and in any order.
    [TestInitialize]
    public void Init() => _factory = new CatalogApiFactory();

    [TestCleanup]
    public void Cleanup() => _factory.Dispose();

    private HttpClient Reader => _factory.CreateClientWithScopes(Scopes.ProductsRead);
    private HttpClient Writer => _factory.CreateClientWithScopes(Scopes.ProductsRead, Scopes.ProductsWrite);

    private async Task<ProductDto> CreateProductAsync(string sku = "SKU-1", decimal price = 9.99m)
    {
        var response = await Writer.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(sku, "Widget", price));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    [TestMethod]
    public async Task Health_endpoint_is_anonymous()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task No_token_returns_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/products");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Read_scope_cannot_write_returns_403()
    {
        var response = await Reader.PostAsJsonAsync("/api/v1/products", new CreateProductRequest("SKU-1", "Widget", 1m));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Create_then_get_round_trips_and_sets_location_header()
    {
        var response = await Writer.PostAsJsonAsync("/api/v1/products", new CreateProductRequest("SKU-42", "Widget", 12.50m));
        var created = await response.Content.ReadFromJsonAsync<ProductDto>();

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.IsNotNull(response.Headers.Location);

        var fetched = await Reader.GetFromJsonAsync<ProductDto>(response.Headers.Location);
        Assert.AreEqual(created, fetched);
    }

    [TestMethod]
    public async Task Invalid_create_returns_validation_problem_details()
    {
        var response = await Writer.PostAsJsonAsync("/api/v1/products", new CreateProductRequest("lower case!", "", -1m));
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsTrue(problem!.Errors.ContainsKey("Sku"));
        Assert.IsTrue(problem.Errors.ContainsKey("Name"));
        Assert.IsTrue(problem.Errors.ContainsKey("Price"));
    }

    [TestMethod]
    public async Task Duplicate_sku_returns_409()
    {
        await CreateProductAsync("SKU-DUP");

        var response = await Writer.PostAsJsonAsync("/api/v1/products", new CreateProductRequest("SKU-DUP", "Again", 1m));

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task Update_with_stale_version_returns_409_concurrency_conflict()
    {
        var product = await CreateProductAsync();

        // First writer wins and gets a new Version...
        var first = await Writer.PutAsJsonAsync($"/api/v1/products/{product.Id}", new UpdateProductRequest("First", 1m, product.Version));
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        // ...the second writer still holds the old Version and must not silently overwrite.
        var second = await Writer.PutAsJsonAsync($"/api/v1/products/{product.Id}", new UpdateProductRequest("Second", 2m, product.Version));
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
        Assert.AreEqual(409, problem!.Status);
    }

    [TestMethod]
    public async Task Delete_then_get_returns_404_problem()
    {
        var product = await CreateProductAsync();

        var delete = await Writer.DeleteAsync($"/api/v1/products/{product.Id}");
        var get = await Reader.GetAsync($"/api/v1/products/{product.Id}");

        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, get.StatusCode);
        Assert.AreEqual("application/problem+json", get.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task V2_returns_new_shape_and_reports_supported_versions()
    {
        await CreateProductAsync("SKU-V2", 5m);

        var response = await Reader.GetAsync("/api/v2/products");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<ProductV2Dto>>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Widget (SKU-V2)", page!.Items[0].DisplayName);
        Assert.AreEqual(new MoneyDto(5m, "USD"), page.Items[0].Price);
        Assert.IsTrue(response.Headers.TryGetValues("api-supported-versions", out var versions));
        StringAssert.Contains(string.Join(",", versions), "2.0");
    }

    [TestMethod]
    public async Task Paging_clamps_page_size()
    {
        for (var i = 0; i < 3; i++)
        {
            await CreateProductAsync($"SKU-P{i}");
        }

        var page = await Reader.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?page=2&pageSize=2");

        Assert.AreEqual(3, page!.TotalCount);
        Assert.AreEqual(1, page.Items.Count);
    }
}
