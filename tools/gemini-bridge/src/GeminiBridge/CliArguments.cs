namespace GeminiBridge;

public enum BridgeCommand
{
    None,
    Help,
    Version,
    InitConfig,
    Login,
    Logout,
    Doctor,
    Models,
    ShowConfig,
    Write
}

public sealed class CliArguments
{
    private readonly Dictionary<string, List<string?>> _options = new(StringComparer.OrdinalIgnoreCase);

    public BridgeCommand Command { get; private set; }

    public string? ConfigPath => Single("--config");

    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new BridgeException(ExitCode.Usage, $"无法识别的位置参数：{argument}");
            }

            if (TryMapCommand(argument, out var command))
            {
                if (parsed.Command != BridgeCommand.None)
                {
                    throw new BridgeException(ExitCode.Usage, "一次只能执行一个一级命令。");
                }

                parsed.Command = command;
                continue;
            }

            if (IsBooleanOption(argument))
            {
                parsed.Add(argument, null);
                continue;
            }

            if (!IsValueOption(argument))
            {
                throw new BridgeException(ExitCode.Usage, $"未知参数：{argument}");
            }

            if (++index >= args.Length)
            {
                throw new BridgeException(ExitCode.Usage, $"参数缺少值：{argument}");
            }

            parsed.Add(argument, args[index]);
        }

        if (parsed.Command == BridgeCommand.None)
        {
            parsed.Command = BridgeCommand.Help;
        }

        return parsed;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Single(string name)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            return null;
        }

        if (values.Count > 1)
        {
            throw new BridgeException(ExitCode.Usage, $"参数只能出现一次：{name}");
        }

        return values[0];
    }

    public IReadOnlyList<string> Multiple(string name) =>
        _options.TryGetValue(name, out var values)
            ? values.Where(value => value is not null).Select(value => value!).ToArray()
            : Array.Empty<string>();

    private static bool TryMapCommand(string argument, out BridgeCommand command)
    {
        command = argument.ToLowerInvariant() switch
        {
            "--help" => BridgeCommand.Help,
            "--version" => BridgeCommand.Version,
            "--init-config" => BridgeCommand.InitConfig,
            "--login" => BridgeCommand.Login,
            "--logout" => BridgeCommand.Logout,
            "--doctor" => BridgeCommand.Doctor,
            "--models" => BridgeCommand.Models,
            "--show-config" => BridgeCommand.ShowConfig,
            "--write" => BridgeCommand.Write,
            _ => BridgeCommand.None
        };
        return command != BridgeCommand.None;
    }

    private static bool IsBooleanOption(string argument) =>
        argument.Equals("--api-key-stdin", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--show-secrets", StringComparison.OrdinalIgnoreCase);

    private static bool IsValueOption(string argument) =>
        argument.Equals("--config", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--base-url", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--model", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--project", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--brief", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--require", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--output", StringComparison.OrdinalIgnoreCase)
        || argument.Equals("--log-format", StringComparison.OrdinalIgnoreCase);

    private void Add(string name, string? value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            values = new List<string?>();
            _options[name] = values;
        }

        values.Add(value);
    }
}
