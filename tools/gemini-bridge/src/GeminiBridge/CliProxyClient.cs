using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeminiBridge;

public sealed class CliProxyClient : IDisposable
{
    private readonly BridgeConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IBridgeLogger _logger;

    public CliProxyClient(BridgeConfig config, IBridgeLogger logger, HttpClient? httpClient = null)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/models");
        using var response = await SendWithRetryAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response.StatusCode, body);

        try
        {
            using var document = JsonDocument.Parse(body);
            var models = new List<string>();
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        models.Add(id.GetString()!);
                    }
                }
            }
            else if (document.RootElement.TryGetProperty("models", out var nativeModels)
                     && nativeModels.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in nativeModels.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        models.Add(name.GetString()!.Replace("models/", "", StringComparison.Ordinal));
                    }
                }
            }

            return models.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException exception)
        {
            throw new BridgeException(ExitCode.RemoteApi, "远程 /v1/models 返回了无效 JSON。", exception);
        }
    }

    public async Task<JsonObject> GenerateAsync(
        string model,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var escapedModel = Uri.EscapeDataString(model).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        var path = $"v1beta/models/{escapedModel}:generateContent";
        var body = payload.ToJsonString(ConfigStore.JsonOptions);
        using var request = CreateRequest(HttpMethod.Post, path, body);
        using var response = await SendWithRetryAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response.StatusCode, responseBody);

        try
        {
            return JsonNode.Parse(responseBody) as JsonObject
                ?? throw new JsonException("根节点不是对象。");
        }
        catch (JsonException exception)
        {
            throw new BridgeException(ExitCode.RemoteApi, "远程生成接口返回了无效 JSON。", exception);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, string? json = null)
    {
        var baseUrl = _config.Server.BaseUrl.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Server.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage initialRequest,
        CancellationToken cancellationToken)
    {
        var method = initialRequest.Method;
        var uri = initialRequest.RequestUri;
        var body = initialRequest.Content is null
            ? null
            : await initialRequest.Content.ReadAsStringAsync(cancellationToken);

        Exception? lastException = null;
        for (var attempt = 0; attempt <= _config.Agent.RequestRetries; attempt++)
        {
            using var request = CreateRequest(method, GetRelativePath(uri!), body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.Agent.TimeoutSeconds));
            try
            {
                var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                if (!IsRetryable(response.StatusCode) || attempt == _config.Agent.RequestRetries)
                {
                    return response;
                }

                var retryStatusCode = response.StatusCode;
                response.Dispose();
                _logger.Warning("api.retry", $"远程接口返回 {(int)retryStatusCode}，准备第 {attempt + 1} 次重试。");
            }
            catch (Exception exception) when (
                (exception is HttpRequestException or TaskCanceledException)
                && !cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                if (attempt == _config.Agent.RequestRetries)
                {
                    break;
                }

                _logger.Warning("api.retry", $"远程请求失败，准备第 {attempt + 1} 次重试：{exception.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
        }

        throw new BridgeException(
            ExitCode.RemoteApi,
            $"无法连接远程 CLIProxyAPI：{lastException?.Message ?? "请求失败"}",
            lastException);
    }

    private string GetRelativePath(Uri uri)
    {
        var baseUri = new Uri(_config.Server.BaseUrl.TrimEnd('/') + "/");
        return baseUri.MakeRelativeUri(uri).ToString();
    }

    private void EnsureSuccess(HttpStatusCode statusCode, string responseBody)
    {
        if ((int)statusCode is >= 200 and < 300)
        {
            return;
        }

        var safeBody = Redact(responseBody);
        if (safeBody.Length > 2000)
        {
            safeBody = safeBody[..2000];
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new BridgeException(
                ExitCode.Authentication,
                $"远程 API 拒绝访问（{(int)statusCode}）。请重新运行 --login。{FormatBody(safeBody)}");
        }

        throw new BridgeException(
            ExitCode.RemoteApi,
            $"远程 API 返回错误 {(int)statusCode}。{FormatBody(safeBody)}");
    }

    private string Redact(string value) =>
        string.IsNullOrEmpty(_config.Server.ApiKey)
            ? value
            : value.Replace(_config.Server.ApiKey, "***REDACTED***", StringComparison.Ordinal);

    private static string FormatBody(string body) =>
        string.IsNullOrWhiteSpace(body) ? "" : $" 响应：{body.Trim()}";

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
}
