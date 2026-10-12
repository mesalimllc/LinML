using System.Text.Json;
using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Tests for `linml serve`: long-lived session over JSON-lines stdio,
/// sharing selection/fallback semantics with `run`.
/// </summary>
public class ServeCommandTests
{
    private static bool HasTinyFixture() =>
        File.Exists(CliTestHost.Fixture("model.onnx")) && File.Exists(CliTestHost.Fixture("inputs.json"));

    private static string TinyRequestLine()
    {
        var json = File.ReadAllText(CliTestHost.Fixture("inputs.json"));
        // Single line: the serve protocol is one JSON document per line.
        return json.Replace("\n", "").Replace("\r", "");
    }

    private static float[] OutputData(string responseLine)
    {
        using var doc = JsonDocument.Parse(responseLine);
        return doc.RootElement.GetProperty("outputs")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }

    [Fact]
    public async Task Serve_TwoRequests_BothSucceedWithExpectedOutputs()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var request = TinyRequestLine();
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [request, request],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--provider", "cpu"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(line));
        }

        // Both responses select the same provider: the session is held, not recreated.
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(
            first.RootElement.GetProperty("selectedProvider").GetString(),
            second.RootElement.GetProperty("selectedProvider").GetString());
    }

    [Fact]
    public async Task Serve_Warmup_ReportsReadyAndServes()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, stderr) = await Task.Run(() => CliTestHost.RunWithStdin(
            [TinyRequestLine()],
            "serve", "--model", CliTestHost.Fixture("model.onnx"),
            "--provider", "cpu",
            "--warmup-inputs", CliTestHost.Fixture("inputs.json")));

        Assert.Equal(0, exitCode);
        Assert.Contains("Warmup:", stderr);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(lines[0]));
    }

    [Fact]
    public async Task Serve_BadRequestLine_EmitsErrorAndContinues()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            ["this is not json", TinyRequestLine()],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--provider", "cpu"));

        // Server survives the bad line: error envelope + good response, exit 0 on EOF.
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        using var error = JsonDocument.Parse(lines[0]);
        Assert.True(error.RootElement.TryGetProperty("error", out _));
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(lines[1]));
    }

    [Fact]
    public async Task Serve_MissingModel_Returns2()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("does-not-exist.onnx")));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Serve_MissingWarmupInputs_Returns2()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx")))
        {
            return;
        }

        var (exitCode, _, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("model.onnx"),
            "--warmup-inputs", CliTestHost.Fixture("does-not-exist.json")));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Serve_EmptyStdin_ExitsZero()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx")))
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--provider", "cpu"));

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stdout));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task Serve_InvalidPort_Returns2(int invalidPort)
    {
        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--port", invalidPort.ToString()));

        Assert.Equal(2, exitCode);
        Assert.Contains("Invalid port number", stderr);
    }

    [Fact]
    public async Task ServeHttp_HealthAndPredictAndShutdown_SucceedsEndToEnd()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        int port = GetAvailablePort();
        using var client = new HttpClient();
        string baseUrl = $"http://127.0.0.1:{port}";

        // Start serve in HTTP mode in background task
        var serveTask = Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("model.onnx"),
            "--provider", "cpu",
            "--port", port.ToString()));

        // Poll /health until server is ready
        bool ready = false;
        for (int i = 0; i < 50; i++)
        {
            try
            {
                var healthResp = await client.GetAsync($"{baseUrl}/health");
                if (healthResp.IsSuccessStatusCode)
                {
                    ready = true;
                    break;
                }
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        Assert.True(ready, "Server failed to start within timeout");

        // 1. GET / (root metadata)
        var rootResp = await client.GetAsync($"{baseUrl}/");
        Assert.True(rootResp.IsSuccessStatusCode);
        var rootJson = await rootResp.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(rootJson))
        {
            Assert.Equal("LinML", doc.RootElement.GetProperty("service").GetString());
            Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
        }

        // 2. GET /health
        var healthResponse = await client.GetAsync($"{baseUrl}/health");
        Assert.True(healthResponse.IsSuccessStatusCode);
        var healthJson = await healthResponse.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(healthJson))
        {
            Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("CPUExecutionProvider", doc.RootElement.GetProperty("selectedProvider").GetString());
        }

        // 3. POST /health should return 405 Method Not Allowed
        var postHealth = await client.PostAsync($"{baseUrl}/health", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, postHealth.StatusCode);

        // 4. POST /v1/models/predict with valid inputs
        var inputJson = File.ReadAllText(CliTestHost.Fixture("inputs.json"));
        var predictResp = await client.PostAsync(
            $"{baseUrl}/v1/models/predict",
            new StringContent(inputJson, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(predictResp.IsSuccessStatusCode);
        var predictBody = await predictResp.Content.ReadAsStringAsync();
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(predictBody));

        // 5. POST /predict (short route alias)
        var aliasResp = await client.PostAsync(
            $"{baseUrl}/predict",
            new StringContent(inputJson, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(aliasResp.IsSuccessStatusCode);

        // 6. POST /v1/models/predict with bad JSON returns 400 Bad Request
        var badResp = await client.PostAsync(
            $"{baseUrl}/v1/models/predict",
            new StringContent("{bad-json}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badResp.StatusCode);

        // 7. GET /not-found returns 404
        var notFoundResp = await client.GetAsync($"{baseUrl}/unknown-route");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, notFoundResp.StatusCode);

        // 8. POST /v1/shutdown shuts down cleanly
        var shutdownResp = await client.PostAsync(
            $"{baseUrl}/v1/shutdown",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.True(shutdownResp.IsSuccessStatusCode);

        // Server exits with code 0
        var (exitCode, stdout, stderr) = await serveTask;
        Assert.Equal(0, exitCode);
        Assert.Contains("Serving HTTP:", stderr);
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
