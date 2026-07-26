using System.Text.Json.Serialization;

namespace GeminiBridge;

public sealed class BridgeConfig
{
    public int Version { get; set; } = 1;

    public ServerConfig Server { get; set; } = new();

    public ModelConfig Model { get; set; } = new();

    public AgentConfig Agent { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public static BridgeConfig CreateDefault() => new();

    public void Validate(bool requireCredentials)
    {
        if (Version != 1)
        {
            throw new BridgeException(ExitCode.Usage, $"不支持的配置版本：{Version}");
        }

        if (!Uri.TryCreate(Server.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new BridgeException(ExitCode.Usage, "server.baseUrl 必须是没有查询参数和片段的 HTTP(S) 绝对地址。");
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !Security.AllowInsecureHttp)
        {
            throw new BridgeException(
                ExitCode.Security,
                "远程 CLIProxyAPI 必须使用 HTTPS；如确需明文 HTTP，请显式设置 security.allowInsecureHttp=true。");
        }

        if (requireCredentials && string.IsNullOrWhiteSpace(Server.ApiKey))
        {
            throw new BridgeException(ExitCode.Authentication, "配置文件中的 server.apiKey 为空，请先运行 --login。");
        }

        if (string.IsNullOrWhiteSpace(Model.Id))
        {
            throw new BridgeException(ExitCode.Usage, "model.id 不能为空。");
        }

        if (!string.Equals(Model.Protocol, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeException(ExitCode.Usage, "当前版本只支持 model.protocol=gemini。");
        }

        if (Agent.TimeoutSeconds is < 1 or > 3600)
        {
            throw new BridgeException(ExitCode.Usage, "agent.timeoutSeconds 必须在 1 到 3600 之间。");
        }

        if (Agent.RequestRetries is < 0 or > 10
            || Agent.MaxToolTurns is < 1 or > 100
            || Agent.RequiredReadRetries is < 0 or > 10
            || Agent.EmptyResponseRetries is < 0 or > 10
            || Agent.MaxOutputTokens is < 256 or > 131072)
        {
            throw new BridgeException(ExitCode.Usage, "agent 配置超出允许范围。");
        }

        if (Security.MaximumFileBytes is < 1 or > 100 * 1024 * 1024)
        {
            throw new BridgeException(ExitCode.Usage, "security.maximumFileBytes 必须在 1 到 104857600 之间。");
        }

        if (!string.Equals(Logging.Format, "text", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Logging.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeException(ExitCode.Usage, "logging.format 只能是 text 或 json。");
        }
    }
}

public sealed class ServerConfig
{
    public string BaseUrl { get; set; } = "https://proxy.example.com";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
}

public sealed class ModelConfig
{
    public string Id { get; set; } = "gemini-pro-agent";

    public string Protocol { get; set; } = "gemini";

    public string ThinkingLevel { get; set; } = "high";
}

public sealed class AgentConfig
{
    public int TimeoutSeconds { get; set; } = 600;

    public int RequestRetries { get; set; } = 3;

    public int MaxToolTurns { get; set; } = 20;

    public int RequiredReadRetries { get; set; } = 2;

    public int EmptyResponseRetries { get; set; } = 2;

    public int MaxOutputTokens { get; set; } = 32768;
}

public sealed class SecurityConfig
{
    public bool AllowInsecureHttp { get; set; }

    public bool AllowProjectSymlinks { get; set; }

    public long MaximumFileBytes { get; set; } = 1_048_576;
}

public sealed class LoggingConfig
{
    public string Format { get; set; } = "text";

    public string Level { get; set; } = "info";
}
