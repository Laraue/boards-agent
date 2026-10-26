namespace Laraue.Boards.Agent.Infrastructure;

public static class RunnerVersion
{
    /// <summary>The version the release was built with (-p:Version), without the source revision suffix.</summary>
    public static readonly string Value =
        (typeof(RunnerVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev")
        .Split('+')[0];
}
