using Microsoft.AspNetCore.Authorization;

namespace DocIntake.Api.Services;

public sealed class AllowAllAuthorizationHandler(
    IHostEnvironment environment,
    IConfiguration configuration) : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (environment.IsDevelopment() && configuration.GetValue<bool>("Development:BypassAuth"))
        {
            foreach (var requirement in context.PendingRequirements.ToArray())
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
