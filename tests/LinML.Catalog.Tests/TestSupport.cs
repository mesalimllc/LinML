using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "linml-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A wheel-shaped zip built in memory, served by <see cref="FakeWheelHandler"/>.</summary>
internal static class FakeWheel
{
    public const string Prefix = "fake_ep/";
    public const string Library = "libfake_plugin.so";

    public static byte[] Build(bool includeLibrary = true)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeLibrary)
            {
                Add(zip, Prefix + Library, "plugin");
            }

            Add(zip, Prefix + "libruntime.so.2621", "runtime");
            Add(zip, Prefix + "cache.json", "{}");
            Add(zip, Prefix + "__init__.py", "python");
            Add(zip, Prefix + "nested/libnested.so", "nested");
            Add(zip, "fake_ep-1.0.0.dist-info/METADATA", "meta");
        }

        return buffer.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static PluginPackageSpec Spec(byte[] wheel, string? sha256 = null) => new(
        Id: new ProviderId("FakeExecutionProvider"),
        RegistrationName: "linml.test.fake-" + Guid.NewGuid().ToString("N"),
        Version: "1.0.0",
        WheelUrl: new Uri("https://example.invalid/fake_ep-1.0.0-py3-none-any.whl"),
        WheelSha256: sha256 ?? Sha256(wheel),
        PackagePrefix: Prefix,
        InstallSubdirectory: System.IO.Path.Combine("ep-plugins", "fake", "fake_ep"),
        LibraryFileName: Library,
        MinimumOrtVersion: new Version(1, 23, 0));

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}

internal sealed class FakeWheelHandler(byte[] body) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}

/// <summary>Simulates HttpClient.Timeout, which surfaces as TaskCanceledException.</summary>
internal sealed class TimingOutHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
}

internal sealed class FakeRegistrar : IOrtRegistrar
{
    private readonly HashSet<string> _registered = [];

    public Version OrtVersion { get; init; } = new(1, 31, 0);

    public Exception? RegisterThrows { get; init; }

    public bool IsRegistered(string registrationName) => _registered.Contains(registrationName);

    public List<(string Name, string Path)> Registrations { get; } = [];

    public void Register(string registrationName, string libraryPath)
    {
        Registrations.Add((registrationName, libraryPath));
        if (RegisterThrows is not null)
        {
            throw RegisterThrows;
        }

        _registered.Add(registrationName);
    }

    // OrtEpDevice has no public constructor, so the fake always reports zero devices.
    public IReadOnlyList<OrtEpDevice> GetDevices(string epName) => [];

    public IReadOnlyCollection<string> AvailableProviders { get; init; } = ["CPUExecutionProvider"];
}
