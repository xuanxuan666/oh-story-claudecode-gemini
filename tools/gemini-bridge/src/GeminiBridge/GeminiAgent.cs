using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeminiBridge;

public sealed class GeminiAgent
{
    private readonly BridgeConfig _config;
    private readonly CliProxyClient _client;
    private readonly ProjectSandbox _sandbox;
    private readonly IBridgeLogger _logger;

    public GeminiAgent(
        BridgeConfig config,
        CliProxyClient client,
        ProjectSandbox sandbox,
        IBridgeLogger logger)
    {
        _config = config;
        _client = client;
        _sandbox = sandbox;
        _logger = logger;
    }

    public async Task<string> WriteAsync(
        string brief,
        IReadOnlyList<string> requiredPaths,
        CancellationToken cancellationToken)
    {
        var contents = new JsonArray
        {
            CreateContent("user", new JsonArray
            {
                new JsonObject
                {
                    ["text"] = BuildInitialPrompt(brief, requiredPaths)
                }
            })
        };
        var readPaths = new HashSet<string>(PathComparer);
        var requiredReadRepairs = 0;
        var emptyResponseRepairs = 0;

        for (var turn = 1; turn <= _config.Agent.MaxToolTurns; turn++)
        {
            _logger.Info("agent.turn", $"Gemini Agent 第 {turn} 轮。");
            var response = await _client.GenerateAsync(
                _config.Model.Id,
                BuildPayload(contents),
                cancellationToken);
            var content = ExtractCandidateContent(response);
            var parts = content["parts"] as JsonArray
                ?? throw new BridgeException(ExitCode.Agent, "模型响应缺少 candidates[0].content.parts。");
            contents.Add(content.DeepClone());

            var calls = ExtractFunctionCalls(parts);
            if (calls.Count > 0)
            {
                var responseParts = new JsonArray();
                foreach (var call in calls)
                {
                    var result = _sandbox.Execute(call.Name, call.Arguments);
                    if (result.Success && result.ReadPath is not null)
                    {
                        readPaths.Add(result.ReadPath);
                        _logger.Info("tool.read", $"[读取] {result.ReadPath}");
                    }
                    else
                    {
                        _logger.Info("tool.call", $"[工具] {call.Name}: {(result.Success ? "成功" : "失败")}");
                    }

                    var functionResponse = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["response"] = new JsonObject
                        {
                            ["ok"] = result.Success,
                            ["output"] = result.Output
                        }
                    };
                    if (!string.IsNullOrWhiteSpace(call.Id))
                    {
                        functionResponse["id"] = call.Id;
                    }

                    responseParts.Add(new JsonObject
                    {
                        ["functionResponse"] = functionResponse
                    });
                }

                contents.Add(CreateContent("user", responseParts));
                continue;
            }

            var text = ExtractText(parts);
            if (string.IsNullOrWhiteSpace(text))
            {
                if (emptyResponseRepairs++ >= _config.Agent.EmptyResponseRetries)
                {
                    throw new BridgeException(ExitCode.Agent, "模型连续返回空响应。");
                }

                contents.Add(CreateTextContent(
                    "user",
                    "你的上一条响应为空。继续完成任务；需要材料就调用工具，材料齐全后只输出正文。"));
                continue;
            }

            var missing = requiredPaths.Where(path => !readPaths.Contains(path)).ToArray();
            if (missing.Length > 0)
            {
                if (requiredReadRepairs++ >= _config.Agent.RequiredReadRetries)
                {
                    throw new BridgeException(
                        ExitCode.Agent,
                        $"[⚠ 漏读必读] {string.Join(", ", missing)}");
                }

                _logger.Warning("required.missing", $"[⚠ 漏读必读] {string.Join(", ", missing)}");
                contents.Add(CreateTextContent(
                    "user",
                    "上一稿作废。你尚未使用 read_file 读取以下必读文件：\n"
                    + string.Join("\n", missing.Select(path => $"- {path}"))
                    + "\n现在必须逐一读取，随后重新输出完整正文。"));
                continue;
            }

            _logger.Info("required.complete", $"[✓ 必读覆盖] {requiredPaths.Count} 个文件");
            return text.Trim();
        }

        throw new BridgeException(ExitCode.Agent, $"超过最大工具轮数 {_config.Agent.MaxToolTurns}。");
    }

    private JsonObject BuildPayload(JsonArray contents)
    {
        var generationConfig = new JsonObject
        {
            ["maxOutputTokens"] = _config.Agent.MaxOutputTokens
        };
        if (!string.IsNullOrWhiteSpace(_config.Model.ThinkingLevel))
        {
            generationConfig["thinkingConfig"] = new JsonObject
            {
                ["thinkingLevel"] = _config.Model.ThinkingLevel.ToUpperInvariant(),
                ["includeThoughts"] = false
            };
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["text"] =
                            "你是专业中文网络小说正文执笔。"
                            + "你只能通过 read_file 和 list_directory 读取项目资料。"
                            + "必须读取用户列出的全部必读文件；不得声称读取了实际未调用工具的文件。"
                            + "最终只输出正文，不输出标题、说明、分析、Markdown 围栏或完成提示。"
                    }
                }
            },
            ["contents"] = contents.DeepClone(),
            ["tools"] = BuildTools(),
            ["generationConfig"] = generationConfig
        };
    }

    private static JsonArray BuildTools() =>
        new()
        {
            new JsonObject
            {
                ["functionDeclarations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "read_file",
                        ["description"] = "读取项目目录内的 UTF-8 文本文件。",
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "OBJECT",
                            ["properties"] = new JsonObject
                            {
                                ["path"] = new JsonObject
                                {
                                    ["type"] = "STRING",
                                    ["description"] = "相对于项目根目录的文件路径。"
                                }
                            },
                            ["required"] = new JsonArray("path")
                        }
                    },
                    new JsonObject
                    {
                        ["name"] = "list_directory",
                        ["description"] = "列出项目目录内某个目录的直接子项。",
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "OBJECT",
                            ["properties"] = new JsonObject
                            {
                                ["path"] = new JsonObject
                                {
                                    ["type"] = "STRING",
                                    ["description"] = "相对于项目根目录的目录路径，默认是项目根目录。"
                                }
                            }
                        }
                    }
                }
            }
        };

    private static JsonObject ExtractCandidateContent(JsonObject response)
    {
        if (response["candidates"] is not JsonArray candidates || candidates.Count == 0
            || candidates[0]?["content"] is not JsonObject content)
        {
            var blockReason = response["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new BridgeException(
                ExitCode.Agent,
                string.IsNullOrWhiteSpace(blockReason)
                    ? "模型响应没有候选内容。"
                    : $"模型请求被拦截：{blockReason}");
        }

        return content;
    }

    private static List<FunctionCall> ExtractFunctionCalls(JsonArray parts)
    {
        var calls = new List<FunctionCall>();
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (part["functionCall"] is not JsonObject functionCall)
            {
                continue;
            }

            var name = functionCall["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (functionCall["args"] is JsonObject args)
            {
                foreach (var pair in args)
                {
                    arguments[pair.Key] = ConvertNode(pair.Value);
                }
            }

            calls.Add(new FunctionCall(
                name,
                functionCall["id"]?.GetValue<string>(),
                arguments));
        }

        return calls;
    }

    private static object? ConvertNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }
        }

        return node.ToJsonString(ConfigStore.JsonOptions);
    }

    private static string ExtractText(JsonArray parts) =>
        string.Concat(
            parts.OfType<JsonObject>()
                .Select(part => part["text"]?.GetValue<string>())
                .Where(text => text is not null));

    private static JsonObject CreateContent(string role, JsonArray parts) =>
        new()
        {
            ["role"] = role,
            ["parts"] = parts
        };

    private static JsonObject CreateTextContent(string role, string text) =>
        CreateContent(role, new JsonArray { new JsonObject { ["text"] = text } });

    private static string BuildInitialPrompt(string brief, IReadOnlyList<string> requiredPaths) =>
        "请根据下面的写作简报完成正文。\n\n"
        + "动笔前必须调用 read_file 读取全部必读文件：\n"
        + string.Join("\n", requiredPaths.Select(path => $"- {path}"))
        + "\n\n# 写作简报\n"
        + brief;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record FunctionCall(
        string Name,
        string? Id,
        IReadOnlyDictionary<string, object?> Arguments);
}
