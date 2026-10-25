using Laraue.Boards.Agent.Config;

namespace Laraue.Boards.Agent.Boards;

/// <summary>Where the runner's API key comes from: the environment variable when set, else the credential store.</summary>
static class ApiKeySource
{
    public static string? Resolve(RunnerConfig config)
    {
        if (Environment.GetEnvironmentVariable(config.ApiKeyEnv) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        return config.OrganizationId is { } organizationId
            ? CredentialStore.Load(CredentialStore.Account(organizationId))
            : null;
    }
}
