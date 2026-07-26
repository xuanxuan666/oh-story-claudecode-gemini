using System.Text;
using GeminiBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("config stores api key in plaintext", ConfigStoresApiKeyInPlaintext),
        ("config rejects insecure remote http by default", ConfigRejectsInsecureHttp),
        ("sandbox rejects traversal", SandboxRejectsTraversal),
        ("client authenticates and lists models", ClientListsModels),
        ("login validates and writes plaintext config", LoginWritesPlaintextConfig),
        ("agent executes required read tool", AgentExecutesRequiredRead),
        ("agent repairs missing required read", AgentRepairsMissingRead)
    ];

    public static async Task<int> Main()
    {
        var failed = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{Tests.Count - failed}/{Tests.Count} tests passed");
        return failed == 0 ? 0 : 1;
    }

    private static async Task ConfigStoresApiKeyInPlaintext()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "config.json");
        var config = TestConfig("https://proxy.example.com");
        config.Server.ApiKey = "plain-secret-value";
        await ConfigStore.SaveAsync(path, config, CancellationToken.None);
        var text = await File.ReadAllTextAsync(path);
        Assert(text.Contains("\"apiKey\": \"plain-secret-value\"", StringComparison.Ordinal), "API Key was not plaintext.");
        var displayed = ConfigStore.SerializeForDisplay(config, showSecrets: false);
        Assert(!displayed.Contains("plain-secret-value", StringComparison.Ordinal), "Redacted display leaked API Key.");
    }

    private static Task ConfigRejectsInsecureHttp()
    {
        var config = TestConfig("http://remote.example.com");
        config.Security.AllowInsecureHttp = false;
        var exception = Capture(() => config.Validate(requireCredentials: true));
        Assert(exception.ExitCode == ExitCode.Security, "Expected security exit code.");
        return Task.CompletedTask;
    }

    private static Task SandboxRejectsTraversal()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "inside.md"), "inside", Encoding.UTF8);
        var sandbox = new ProjectSandbox(temporary.Path, new SecurityConfig());
        var result = sandbox.Execute(
            "read_file",
            new Dictionary<string, object?> { ["path"] = "../outside.md" });
        Assert(!result.Success, "Traversal unexpectedly succeeded.");
        return Task.CompletedTask;
    }

    private static async Task ClientListsModels()
    {
        await using var server = await FakeServer.StartAsync(context =>
        {
            Assert(
                context.Request.Headers.Authorization == "Bearer test-api-key",
                "Authorization header mismatch.");
            return RespondJson(context, """{"data":[{"id":"gemini-pro-agent"},{"id":"gemini-flash"}]}""");
        });
        var config = TestConfig(server.BaseUrl);
        using var client = new CliProxyClient(config, new NullBridgeLogger());
        var models = await client.GetModelsAsync(CancellationToken.None);
        Assert(models.SequenceEqual(new[] { "gemini-flash", "gemini-pro-agent" }), "Unexpected model list.");
    }

    private static async Task LoginWritesPlaintextConfig()
    {
        await using var server = await FakeServer.StartAsync(context =>
            RespondJson(
                context,
                """{"data":[{"id":"gemini-pro-agent"},{"id":"gemini-flash"}]}"""));
        using var temporary = new TemporaryDirectory();
        var configPath = Path.Combine(temporary.Path, "config.json");
        var initial = TestConfig(server.BaseUrl);
        initial.Server.ApiKey = "";
        await ConfigStore.SaveAsync(configPath, initial, CancellationToken.None);

        var originalInput = Console.In;
        var originalOutput = Console.Out;
        var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader("plain-login-key\ngemini-flash\n"));
            Console.SetOut(output);
            var arguments = CliArguments.Parse(
            [
                "--login",
                "--config", configPath,
                "--base-url", server.BaseUrl,
                "--api-key-stdin"
            ]);
            var exitCode = await GeminiBridge.Program.RunAsync(arguments, CancellationToken.None);
            Assert(exitCode == ExitCode.Success, "Login command failed.");
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }

        var savedText = await File.ReadAllTextAsync(configPath);
        Assert(savedText.Contains("plain-login-key", StringComparison.Ordinal), "Login did not persist plaintext key.");
        Assert(savedText.Contains("\"id\": \"gemini-flash\"", StringComparison.Ordinal), "Login did not persist selected model.");
        var loginOutput = output.ToString();
        Assert(loginOutput.Contains("可用模型（2）", StringComparison.Ordinal), "Login did not print the model count.");
        Assert(
            loginOutput.Contains("gemini-pro-agent", StringComparison.Ordinal)
            && loginOutput.Contains("gemini-flash", StringComparison.Ordinal),
            "Login did not print available model IDs.");
    }

    private static async Task AgentExecutesRequiredRead()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "required.md"), "角色设定", new UTF8Encoding(false));
        var requestCount = 0;
        var secondRequestContainsToolResponse = false;
        await using var server = await FakeServer.StartAsync(async context =>
        {
            var body = await ReadBody(context.Request);
            requestCount++;
            if (requestCount == 1)
            {
                await RespondJson(
                    context,
                    """
                    {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"read_file","args":{"path":"required.md"}}}]}}]}
                    """);
            }
            else
            {
                secondRequestContainsToolResponse = body.Contains("functionResponse", StringComparison.Ordinal);
                await RespondJson(
                    context,
                    """{"candidates":[{"content":{"role":"model","parts":[{"text":"正文内容"}]}}]}""");
            }
        });
        var config = TestConfig(server.BaseUrl);
        var sandbox = new ProjectSandbox(temporary.Path, config.Security);
        using var client = new CliProxyClient(config, new NullBridgeLogger());
        var agent = new GeminiAgent(config, client, sandbox, new NullBridgeLogger());
        var result = await agent.WriteAsync("写一段", new[] { "required.md" }, CancellationToken.None);
        Assert(result == "正文内容", "Unexpected prose.");
        Assert(requestCount == 2, "Unexpected request count.");
        Assert(secondRequestContainsToolResponse, "Tool response was not sent back.");
    }

    private static async Task AgentRepairsMissingRead()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "required.md"), "场景设定", new UTF8Encoding(false));
        var requestCount = 0;
        var repairWasSent = false;
        await using var server = await FakeServer.StartAsync(async context =>
        {
            var body = await ReadBody(context.Request);
            requestCount++;
            switch (requestCount)
            {
                case 1:
                    await RespondJson(
                        context,
                        """{"candidates":[{"content":{"role":"model","parts":[{"text":"未读就写"}]}}]}""");
                    break;
                case 2:
                    repairWasSent = JsonContainsStringFragment(body, "上一稿作废");
                    await RespondJson(
                        context,
                        """
                        {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"read_file","args":{"path":"required.md"}}}]}}]}
                        """);
                    break;
                default:
                    await RespondJson(
                        context,
                        """{"candidates":[{"content":{"role":"model","parts":[{"text":"修复后的正文"}]}}]}""");
                    break;
            }
        });
        var config = TestConfig(server.BaseUrl);
        var sandbox = new ProjectSandbox(temporary.Path, config.Security);
        using var client = new CliProxyClient(config, new NullBridgeLogger());
        var agent = new GeminiAgent(config, client, sandbox, new NullBridgeLogger());
        var result = await agent.WriteAsync("写一段", new[] { "required.md" }, CancellationToken.None);
        Assert(result == "修复后的正文", "Repair result mismatch.");
        Assert(requestCount == 3 && repairWasSent, "Missing-read repair was not enforced.");
    }

    private static BridgeConfig TestConfig(string baseUrl) =>
        new()
        {
            Server = new ServerConfig
            {
                BaseUrl = baseUrl,
                ApiKey = "test-api-key"
            },
            Model = new ModelConfig
            {
                Id = "gemini-pro-agent",
                Protocol = "gemini",
                ThinkingLevel = "high"
            },
            Agent = new AgentConfig
            {
                TimeoutSeconds = 5,
                RequestRetries = 0,
                MaxToolTurns = 10,
                RequiredReadRetries = 2,
                EmptyResponseRetries = 1,
                MaxOutputTokens = 4096
            },
            Security = new SecurityConfig
            {
                AllowInsecureHttp = true,
                MaximumFileBytes = 1024 * 1024
            }
        };

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task RespondJson(HttpContext context, string json)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json, Encoding.UTF8);
    }

    private static BridgeException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (BridgeException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected BridgeException.");
    }

    private static bool JsonContainsStringFragment(string json, string fragment)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return Contains(document.RootElement);

        bool Contains(System.Text.Json.JsonElement element)
        {
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String =>
                    element.GetString()?.Contains(fragment, StringComparison.Ordinal) == true,
                System.Text.Json.JsonValueKind.Array =>
                    element.EnumerateArray().Any(Contains),
                System.Text.Json.JsonValueKind.Object =>
                    element.EnumerateObject().Any(property => Contains(property.Value)),
                _ => false
            };
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gemini-bridge-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private FakeServer(string baseUrl, WebApplication application)
        {
            BaseUrl = baseUrl;
            _application = application;
        }

        public string BaseUrl { get; }

        public static async Task<FakeServer> StartAsync(Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var application = builder.Build();
            application.Run((RequestDelegate)(context => handler(context)));
            await application.StartAsync();
            return new FakeServer(application.Urls.Single(), application);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
