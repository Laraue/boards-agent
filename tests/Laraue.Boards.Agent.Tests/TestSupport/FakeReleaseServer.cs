using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Laraue.Boards.Agent.Tests.TestSupport;

/// <summary>GitHub's releases as an HttpClient sees them, from memory: the release list and each release's files.</summary>
sealed class FakeReleaseServer : HttpMessageHandler
{
    public const string ApiUrl = "https://api.test/repos/acme/agent";
    public const string DownloadUrl = "https://download.test/{tag}";

    private readonly Dictionary<string, byte[]> _files = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Lists the releases, newest first.</summary>
    public void ListReleases(params string[] tags)
    {
        _files[$"{ApiUrl}/releases?per_page=1"] = Encoding.UTF8.GetBytes(
            "[" + string.Join(",", tags.Select(tag => $$"""{"tag_name":"{{tag}}","prerelease":true}""")) + "]");
    }

    /// <summary>A release with a tar.gz of the runner holding <paramref name="content"/>, and SHA256SUMS for it.</summary>
    public void Publish(string tag, string runtime, string content, bool wrongChecksum = false)
    {
        var archive = $"laraue-boards-agent-{tag}-{runtime}.tar.gz";
        var bytes = TarGz(content);
        var sum = Convert.ToHexStringLower(SHA256.HashData(wrongChecksum ? [1, 2, 3] : bytes));
        var baseUrl = DownloadUrl.Replace("{tag}", tag);
        _files[$"{baseUrl}/{archive}"] = bytes;
        _files[$"{baseUrl}/SHA256SUMS"] = Encoding.UTF8.GetBytes($"{sum}  {archive}\n0000  other-file.zip\n");
    }

    public void Fail()
    {
        _files.Clear();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_files.TryGetValue(request.RequestUri!.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static byte[] TarGz(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "laraue-boards-agent/laraue-boards-agent")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            };
            tar.WriteEntry(entry);
        }

        return output.ToArray();
    }
}
