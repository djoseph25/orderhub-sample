using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderHub.Api.Data;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace OrderHub.Api.Tests;

/// <summary>
/// Week 14: runs the real Program.cs (routing, versioning, auth policies, validation, ProblemDetails, EF) in memory.
/// Two things are swapped out:
///   1. SQL Server, replaced by SQLite in-memory (real SQL, real constraints, no server needed).
///      Use Testcontainers when you need exact SQL Server behavior.
///   2. Auth0 JWT validation, replaced by a test scheme that grants whatever scopes the X-Test-Scopes header lists.
///      The authorization policies under test stay the real ones.
/// </summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public CatalogApiFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // skips the Development-only MigrateAsync against SQL Server

        builder.ConfigureTestServices(services =>
        {
            // EF Core 9+ registers the provider via IDbContextOptionsConfiguration<T>. Remove it too, or SqlServer stays configured.
            services.RemoveAll<DbContextOptions<CatalogDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CatalogDbContext>>();
            services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(_connection));

            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                o.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreated();
        return host;
    }

    public HttpClient CreateClientWithScopes(params string[] scopes)
    {
        var client = CreateClient();
        if (scopes.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, string.Join(' ', scopes));
        }
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

/// <summary>Builds a principal shaped like an Auth0 access token: a "sub" claim plus a space-delimited "scope" claim.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string ScopesHeader = "X-Test-Scopes";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ScopesHeader, out var scopes))
        {
            return Task.FromResult(AuthenticateResult.NoResult()); // no "token", so 401
        }

        var identity = new ClaimsIdentity([new Claim("sub", "test-client@clients"), new Claim("scope", scopes.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
