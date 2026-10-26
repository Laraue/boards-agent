using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Laraue.Boards.Agent.Config;

namespace Laraue.Boards.Agent.Boards;

public sealed record AgentSignInStart(string DeviceCode, string UserCode, string VerificationPath, DateTime ExpiresAt, int IntervalSeconds);

public sealed record AgentSignInPoll(string Outcome, string? ApiKey, long? OrganizationId, string? OrganizationName, Guid? UserId);

public sealed record SignedIn(string ApiKey, long OrganizationId, string OrganizationName);

/// <summary>
/// "Connect this machine": the runner asks Boards for a sign-in, the person allows it in the browser for one of their
/// organizations, and the runner polls until Boards hands it the organization's new API key.
/// </summary>
public static class BoardsSignIn
{
    /// <summary>Boards allows 37 characters of machine name, so the key name fits.</summary>
    private const int MaxMachineNameLength = 37;

    /// <param name="openBrowser">False prints the link only, e.g. over SSH, to open it on another device.</param>
    /// <param name="replacesApiKey">
    /// The key this machine held so far: Boards revokes exactly that key once the new one is issued (only if it's the
    /// same user's, in the same organization), instead of leaving it active.
    /// </param>
    /// <param name="clientName">How the runner introduces itself (<see cref="ICodingAgent.ClientName"/>): Boards shows it on
    /// the Allow page and names the key "&lt;clientName&gt; (&lt;machine&gt;)".</param>
    public static async Task<SignedIn?> RunAsync(
        RunnerConfig config,
        string clientName,
        bool openBrowser,
        string? replacesApiKey,
        CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/") };

        var start = await PostAsync(
            http,
            "api/agent-sign-in",
            new JsonObject { ["clientName"] = clientName, ["machineName"] = MachineName(), ["replacesApiKey"] = replacesApiKey },
            RunnerJson.Default.AgentSignInStart,
            ct);

        var url = config.WebUrl.TrimEnd('/') + start.VerificationPath;
        Console.WriteLine();
        if (openBrowser)
        {
            Console.WriteLine($"Opening Boards in your browser to connect this machine. Your code: {start.UserCode}");
            Console.WriteLine($"If the browser didn't open, go to {url}");
            OpenBrowser(url);
        }
        else
        {
            Console.WriteLine($"To connect this machine, open {url} and check that it shows the code {start.UserCode}.");
        }

        Console.WriteLine("Waiting for you to click Allow...");

        var interval = TimeSpan.FromSeconds(Math.Max(start.IntervalSeconds, 1));
        while (true)
        {
            await Task.Delay(interval, ct);
            var poll = await PostAsync(
                http,
                "api/agent-sign-in/token",
                new JsonObject { ["deviceCode"] = start.DeviceCode },
                RunnerJson.Default.AgentSignInPoll,
                ct);

            switch (poll.Outcome)
            {
                case "Approved" when poll is { ApiKey: { } apiKey, OrganizationId: { } organizationId }:
                    return new SignedIn(apiKey, organizationId, poll.OrganizationName ?? organizationId.ToString());
                case "SlowDown":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "Denied":
                    Console.WriteLine("You denied the sign-in in the browser.");
                    return null;
                case "Expired":
                    Console.WriteLine("The sign-in timed out. Run `lba setup` again.");
                    return null;
            }
        }
    }

    private static string MachineName()
    {
        var name = Environment.MachineName;
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".local".Length];
        }

        return name.Length > MaxMachineNameLength ? name[..MaxMachineNameLength] : name;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // The URL is printed too; a machine without a browser (e.g. over SSH) opens it elsewhere.
        }
    }

    private static async Task<T> PostAsync<T>(HttpClient http, string path, JsonObject body, JsonTypeInfo<T> result, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Boards answered {(int)response.StatusCode} to {path}: {text}");
        }

        return JsonSerializer.Deserialize(text, result)
            ?? throw new InvalidOperationException($"Boards gave an empty answer to {path}.");
    }
}
