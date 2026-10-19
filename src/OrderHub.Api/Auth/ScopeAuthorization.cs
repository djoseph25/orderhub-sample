using Microsoft.AspNetCore.Authorization;

namespace OrderHub.Api.Auth;

public static class Scopes
{
    public const string ProductsRead = "read:products";
    public const string ProductsWrite = "write:products";
}

public sealed record HasScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>
/// Week 10: Auth0 puts granted scopes in a space-delimited "scope" claim. With RBAC enabled it also adds a "permissions" array claim.
/// Checking both keeps this working for machine-to-machine (client-credentials) and user tokens.
/// </summary>
public sealed class HasScopeHandler : AuthorizationHandler<HasScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, HasScopeRequirement requirement)
    {
        var scopes = context.User.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(context.User.FindAll("permissions").Select(c => c.Value));

        if (scopes.Contains(requirement.Scope, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
