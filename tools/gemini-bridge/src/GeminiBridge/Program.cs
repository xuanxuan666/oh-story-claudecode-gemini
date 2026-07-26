using System.Reflection;
using System.Text;

namespace GeminiBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var arguments = CliArguments.Parse(args);
            return (int)await RunAsync(arguments, CancellationToken.None);
        }
        catch (BridgeException exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return (int)exception.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("错误：操作已取消。");
            return (int)ExitCode.RemoteApi;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误：未处理异常：{exception.Message}");
            return (int)ExitCode.Agent;
        }
    }

    public static async Task<ExitCode> RunAsync(CliArguments arguments, CancellationToken cancellationToken)
    {
        var configPath = ConfigStore.ResolvePath(arguments.ConfigPath);
        switch (arguments.Command)
        {
            case BridgeCommand.Help:
                PrintHelp();
                return ExitCode.Success;
            case BridgeCommand.Version:
                Console.WriteLine(GetVersion());
                return ExitCode.Success;
            case BridgeCommand.InitConfig:
                {
                    if (File.Exists(configPath))
                    {
                        throw new BridgeException(ExitCode.Usage, $"配置文件已存在：{configPath}");
                    }

                    var initial = BridgeConfig.CreateDefault();
                    await ConfigStore.SaveAsync(configPath, initial, cancellationToken);
                    Console.WriteLine($"已创建明文配置模板：{configPath}");
                    return ExitCode.Success;
                }
            case BridgeCommand.Login:
                return await LoginAsync(arguments, configPath, cancellationToken);
            case BridgeCommand.Logout:
                return await LogoutAsync(configPath, cancellationToken);
            case BridgeCommand.ShowConfig:
                {
                    var config = await ConfigStore.LoadAsync(configPath, allowMissing: false, cancellationToken);
                    Console.WriteLine(ConfigStore.SerializeForDisplay(config, arguments.Has("--show-secrets")));
                    return ExitCode.Success;
                }
            case BridgeCommand.Doctor:
                return await DoctorAsync(arguments, configPath, cancellationToken);
            case BridgeCommand.Models:
                return await ModelsAsync(arguments, configPath, cancellationToken);
            case BridgeCommand.Write:
                return await WriteAsync(arguments, configPath, cancellationToken);
            default:
                throw new BridgeException(ExitCode.Usage, "未指定命令。");
        }
    }

    private static async Task<ExitCode> LoginAsync(
        CliArguments arguments,
        string configPath,
        CancellationToken cancellationToken)
    {
        var config = await ConfigStore.LoadAsync(configPath, allowMissing: true, cancellationToken);
        var baseUrl = arguments.Single("--base-url");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = Prompt($"远程 CLIProxyAPI 地址 [{config.Server.BaseUrl}]: ");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = config.Server.BaseUrl;
            }
        }

        var apiKey = arguments.Has("--api-key-stdin")
            ? (await Console.In.ReadLineAsync(cancellationToken))?.Trim()
            : ReadSecret("API Key: ");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new BridgeException(ExitCode.Authentication, "API Key 不能为空。");
        }

        config.Server.BaseUrl = baseUrl.Trim();
        config.Server.ApiKey = apiKey;
        config.Validate(requireCredentials: true);

        var logger = new BridgeLogger(Console.Error, config.Logging.Format);
        using var client = new CliProxyClient(config, logger);
        var models = await client.GetModelsAsync(cancellationToken);
        if (models.Count == 0)
        {
            throw new BridgeException(ExitCode.RemoteApi, "远程 API 可访问，但 /v1/models 没有返回任何模型。");
        }

        Console.WriteLine($"可用模型（{models.Count}）：");
        foreach (var availableModel in models)
        {
            Console.WriteLine($"  {availableModel}");
        }

        var model = arguments.Single("--model");
        if (string.IsNullOrWhiteSpace(model))
        {
            var suggestedModel = models.Contains(config.Model.Id, StringComparer.Ordinal)
                ? config.Model.Id
                : models[0];
            if (!string.Equals(suggestedModel, config.Model.Id, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"当前配置模型 {config.Model.Id} 不在远程列表中，改用 {suggestedModel} 作为默认选项。");
            }

            model = Prompt($"默认模型 [{suggestedModel}]: ");
            if (string.IsNullOrWhiteSpace(model))
            {
                model = suggestedModel;
            }
        }

        config.Model.Id = model.Trim();
        config.Validate(requireCredentials: true);
        if (!models.Contains(config.Model.Id, StringComparer.Ordinal))
        {
            throw new BridgeException(
                ExitCode.RemoteApi,
                $"远程 API 可访问，但模型列表中没有 {config.Model.Id}。可用模型数：{models.Count}");
        }

        await ConfigStore.SaveAsync(configPath, config, cancellationToken);
        Console.WriteLine($"登录验证成功，明文配置已写入：{configPath}");
        return ExitCode.Success;
    }

    private static async Task<ExitCode> LogoutAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = await ConfigStore.LoadAsync(configPath, allowMissing: false, cancellationToken);
        config.Server.ApiKey = "";
        await ConfigStore.SaveAsync(configPath, config, cancellationToken);
        Console.WriteLine($"已清除配置文件中的 API Key：{configPath}");
        return ExitCode.Success;
    }

    private static async Task<ExitCode> DoctorAsync(
        CliArguments arguments,
        string configPath,
        CancellationToken cancellationToken)
    {
        var config = await LoadRuntimeConfigAsync(arguments, configPath, cancellationToken);
        Console.WriteLine($"[✓] 配置有效：{configPath}");
        Console.WriteLine($"[✓] 远程地址：{config.Server.BaseUrl}");
        using var client = new CliProxyClient(config, new BridgeLogger(Console.Error, config.Logging.Format));
        var models = await client.GetModelsAsync(cancellationToken);
        Console.WriteLine($"[✓] API Key 有效，可见模型：{models.Count}");
        if (!models.Contains(config.Model.Id, StringComparer.Ordinal))
        {
            throw new BridgeException(ExitCode.RemoteApi, $"默认模型不可用：{config.Model.Id}");
        }

        Console.WriteLine($"[✓] 默认模型可用：{config.Model.Id}");
        return ExitCode.Success;
    }

    private static async Task<ExitCode> ModelsAsync(
        CliArguments arguments,
        string configPath,
        CancellationToken cancellationToken)
    {
        var config = await LoadRuntimeConfigAsync(arguments, configPath, cancellationToken);
        using var client = new CliProxyClient(config, new BridgeLogger(Console.Error, config.Logging.Format));
        var models = await client.GetModelsAsync(cancellationToken);
        foreach (var model in models)
        {
            Console.WriteLine(model == config.Model.Id ? $"* {model}" : $"  {model}");
        }

        return ExitCode.Success;
    }

    private static async Task<ExitCode> WriteAsync(
        CliArguments arguments,
        string configPath,
        CancellationToken cancellationToken)
    {
        var project = Required(arguments, "--project");
        var briefPath = Required(arguments, "--brief");
        var requiredPatterns = arguments.Multiple("--require");
        if (requiredPatterns.Count == 0)
        {
            throw new BridgeException(ExitCode.Usage, "--write 至少需要一个 --require。");
        }

        var config = await LoadRuntimeConfigAsync(arguments, configPath, cancellationToken);
        var logFormat = arguments.Single("--log-format");
        if (!string.IsNullOrWhiteSpace(logFormat))
        {
            config.Logging.Format = logFormat;
            config.Validate(requireCredentials: true);
        }

        var logger = new BridgeLogger(Console.Error, config.Logging.Format);
        var sandbox = new ProjectSandbox(project, config.Security);
        var brief = sandbox.ReadBrief(briefPath);
        var requiredPaths = sandbox.ExpandRequiredPatterns(requiredPatterns);
        using var client = new CliProxyClient(config, logger);
        var agent = new GeminiAgent(config, client, sandbox, logger);
        var result = await agent.WriteAsync(brief, requiredPaths, cancellationToken);

        var outputPath = arguments.Single("--output");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Write(result);
        }
        else
        {
            await WriteOutputAtomicallyAsync(sandbox, outputPath, result, cancellationToken);
            Console.WriteLine(Path.GetFullPath(
                Path.IsPathRooted(outputPath)
                    ? outputPath
                    : Path.Combine(sandbox.Root, outputPath)));
        }

        return ExitCode.Success;
    }

    private static async Task<BridgeConfig> LoadRuntimeConfigAsync(
        CliArguments arguments,
        string configPath,
        CancellationToken cancellationToken)
    {
        var config = await ConfigStore.LoadAsync(configPath, allowMissing: false, cancellationToken);
        var modelOverride = arguments.Single("--model");
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            config.Model.Id = modelOverride;
        }

        config.Validate(requireCredentials: true);
        return config;
    }

    private static async Task WriteOutputAtomicallyAsync(
        ProjectSandbox sandbox,
        string outputPath,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = sandbox.ResolveOutputPath(outputPath);

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new BridgeException(ExitCode.Security, "--output 路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Required(CliArguments arguments, string name) =>
        arguments.Single(name)
        ?? throw new BridgeException(ExitCode.Usage, $"缺少参数：{name}");

    private static string Prompt(string prompt)
    {
        Console.Error.Write(prompt);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine()?.Trim() ?? "";
        }

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0";

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            gemini-bridge 2 — 远程 CLIProxyAPI 小说执笔桥

            配置：
              gemini-bridge --init-config [--config <path>]
              gemini-bridge --login [--config <path>] [--base-url <url>] [--model <id>] [--api-key-stdin]
              gemini-bridge --logout [--config <path>]
              gemini-bridge --show-config [--show-secrets] [--config <path>]

            诊断：
              gemini-bridge --doctor [--config <path>]
              gemini-bridge --models [--config <path>]

            写作：
              gemini-bridge --write --project <dir> --brief <file>
                --require <path-or-glob> [--require <path-or-glob> ...]
                [--output <project-relative-path>] [--model <id>]
                [--log-format text|json] [--config <path>]

            说明：
              --login 只验证远程普通 API，并把 API Key 明文写入 JSON 配置。
              本程序不调用 CLIProxyAPI Management API，不启动任何 CLIProxyAPI 可执行文件。
            """);
    }
}
