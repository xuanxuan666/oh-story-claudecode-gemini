using System.Text;
using System.Text.Json;

namespace GeminiBridge;

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ResolvePath(string? commandLinePath)
    {
        if (!string.IsNullOrWhiteSpace(commandLinePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(commandLinePath));
        }

        var environmentPath = Environment.GetEnvironmentVariable("GEMINI_BRIDGE_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(environmentPath));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(localAppData, "TwinScribe", "gemini-bridge", "config.json");
    }

    public static async Task<BridgeConfig> LoadAsync(string path, bool allowMissing, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            if (allowMissing)
            {
                return BridgeConfig.CreateDefault();
            }

            throw new BridgeException(ExitCode.Usage, $"配置文件不存在：{path}。请先运行 --login 或 --init-config。");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<BridgeConfig>(stream, JsonOptions, cancellationToken);
            return config ?? throw new BridgeException(ExitCode.Usage, $"配置文件为空：{path}");
        }
        catch (JsonException exception)
        {
            throw new BridgeException(ExitCode.Usage, $"配置文件 JSON 无效：{exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new BridgeException(ExitCode.Usage, $"无法读取配置文件：{exception.Message}", exception);
        }
    }

    public static async Task SaveAsync(string path, BridgeConfig config, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new BridgeException(ExitCode.Usage, $"配置文件路径无效：{path}");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, fullPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BridgeException(ExitCode.Usage, $"无法写入配置文件：{exception.Message}", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string SerializeForDisplay(BridgeConfig config, bool showSecrets)
    {
        var clone = JsonSerializer.Deserialize<BridgeConfig>(
            JsonSerializer.Serialize(config, JsonOptions),
            JsonOptions) ?? BridgeConfig.CreateDefault();
        if (!showSecrets && !string.IsNullOrEmpty(clone.Server.ApiKey))
        {
            clone.Server.ApiKey = "***REDACTED***";
        }

        return JsonSerializer.Serialize(clone, JsonOptions);
    }
}
