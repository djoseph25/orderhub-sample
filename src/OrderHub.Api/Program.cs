using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Auth;
using OrderHub.Api.Data;
using OrderHub.Api.Errors;

// Top-level hosting. If your codebase uses Startup.cs, ConfigureServices = the builder.Services section
// and Configure = the app.Use*/Map* section. Same concepts in a different file layout (Week 9).
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton(TimeProvider.System);

// Week 10: Auth0 (or any OIDC provider) issues the JWT. The API checks issuer, audience, signature and expiry.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://{builder.Configuration["Auth0:Domain"]}/";
        options.Audience = builder.Configuration["Auth0:Audience"];
        options.MapInboundClaims = false; // keep "sub", "scope" etc. as-is instead of the long SOAP-style claim URIs
    });

// Week 10: policy-based authorization. Controllers declare which scope they need; the handler checks the token.
builder.Services.AddSingleton<IAuthorizationHandler, HasScopeHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Scopes.ProductsRead, p => p.AddRequirements(new HasScopeRequirement(Scopes.ProductsRead)))
    .AddPolicy(Scopes.ProductsWrite, p => p.AddRequirements(new HasScopeRequirement(Scopes.ProductsWrite)));

// Week 12: URL-segment versioning (/api/v1/..., /api/v2/...) with Asp.Versioning,
// the replacement for the deprecated Microsoft.AspNetCore.Mvc.Versioning.
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.ReportApiVersions = true; // adds api-supported-versions response header
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    })
    // Built-in OpenAPI, one document per API version: /openapi/v1.json and /openapi/v2.json
    .AddOpenApi();

// Week 12: one error format everywhere (RFC 9457 ProblemDetails), including 401/403/404 and unhandled exceptions.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx => ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Week 11: EF Core with SQL Server. Tests swap in SQLite (see tests/OrderHub.Api.Tests).
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Catalog")));

builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();

    // Convenient locally. In shared environments, run migrations from the pipeline (migration bundle) instead.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

await app.RunAsync();

/// <summary>Makes the implicit Program class visible to WebApplicationFactory in the test project (Week 14).</summary>
public partial class Program;
