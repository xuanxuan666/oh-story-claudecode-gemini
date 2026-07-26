using System.Text;
using System.Text.RegularExpressions;

namespace GeminiBridge;

public sealed record ToolExecutionResult(bool Success, string Output, string? ReadPath = null);

public sealed class ProjectSandbox
{
    private readonly string _root;
    private readonly SecurityConfig _security;
    private readonly StringComparison _pathComparison;

    public ProjectSandbox(string projectRoot, SecurityConfig security)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new BridgeException(ExitCode.Usage, "--project 不能为空。");
        }

        _root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(_root))
        {
            throw new BridgeException(ExitCode.Security, $"项目目录不存在：{_root}");
        }

        _security = security;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        EnsureNoReparsePoint(_root);
    }

    public string Root => _root;

    public ToolExecutionResult Execute(string name, IReadOnlyDictionary<string, object?> arguments)
    {
        try
        {
            return name switch
            {
                "read_file" => ReadFile(RequiredString(arguments, "path")),
                "list_directory" => ListDirectory(OptionalString(arguments, "path") ?? "."),
                _ => new ToolExecutionResult(false, $"未知工具：{name}")
            };
        }
        catch (BridgeException exception)
        {
            return new ToolExecutionResult(false, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ToolExecutionResult(false, $"文件操作失败：{exception.Message}");
        }
    }

    public IReadOnlyList<string> ExpandRequiredPatterns(IEnumerable<string> patterns)
    {
        var files = EnumerateFilesSafely()
            .Select(path => NormalizeRelative(Path.GetRelativePath(_root, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var targets = new HashSet<string>(PathComparer);

        foreach (var rawPattern in patterns)
        {
            var pattern = NormalizeRequiredPattern(rawPattern);
            if (HasWildcard(pattern))
            {
                var regex = GlobToRegex(pattern);
                var matches = files.Where(path => regex.IsMatch(path)).ToArray();
                if (matches.Length == 0)
                {
                    throw new BridgeException(ExitCode.Security, $"必读模式未匹配到文件：{rawPattern}");
                }

                foreach (var match in matches)
                {
                    targets.Add(match);
                }
            }
            else
            {
                var fullPath = ResolvePath(pattern, requireExisting: true, expectDirectory: false);
                targets.Add(NormalizeRelative(Path.GetRelativePath(_root, fullPath)));
            }
        }

        return targets.Order(StringComparer.Ordinal).ToArray();
    }

    public string ReadBrief(string path)
    {
        var resolved = ResolveUserSuppliedPath(path, expectDirectory: false);
        var info = new FileInfo(resolved);
        if (info.Length > _security.MaximumFileBytes)
        {
            throw new BridgeException(
                ExitCode.Security,
                $"写作简报超过文件大小限制：{info.Length} > {_security.MaximumFileBytes}");
        }

        return ReadUtf8(resolved);
    }

    public string NormalizeReadPath(string path)
    {
        var resolved = ResolvePath(path, requireExisting: true, expectDirectory: false);
        return NormalizeRelative(Path.GetRelativePath(_root, resolved));
    }

    public string ResolveOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            throw new BridgeException(ExitCode.Security, "--output 路径无效。");
        }

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));
        EnsureInsideRoot(fullPath);
        EnsureNoReparsePoint(fullPath);
        return fullPath;
    }

    private ToolExecutionResult ReadFile(string path)
    {
        var fullPath = ResolvePath(path, requireExisting: true, expectDirectory: false);
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _security.MaximumFileBytes)
        {
            throw new BridgeException(
                ExitCode.Security,
                $"文件超过读取上限：{NormalizeRelative(Path.GetRelativePath(_root, fullPath))}");
        }

        var relativePath = NormalizeRelative(Path.GetRelativePath(_root, fullPath));
        return new ToolExecutionResult(true, ReadUtf8(fullPath), relativePath);
    }

    private ToolExecutionResult ListDirectory(string path)
    {
        var fullPath = ResolvePath(path, requireExisting: true, expectDirectory: true);
        var entries = Directory.EnumerateFileSystemEntries(fullPath)
            .Order(StringComparer.Ordinal)
            .Take(501)
            .ToArray();
        if (entries.Length > 500)
        {
            throw new BridgeException(ExitCode.Security, "目录条目超过 500 个，请指定更具体的子目录。");
        }

        var lines = entries.Select(entry =>
        {
            var attributes = File.GetAttributes(entry);
            var marker = attributes.HasFlag(FileAttributes.Directory) ? "[D]" : "[F]";
            return $"{marker} {Path.GetFileName(entry)}";
        });
        return new ToolExecutionResult(true, string.Join(Environment.NewLine, lines));
    }

    private string ResolveUserSuppliedPath(string path, bool expectDirectory)
    {
        if (Path.IsPathRooted(path))
        {
            var fullPath = Path.GetFullPath(path);
            EnsureInsideRoot(fullPath);
            EnsureNoReparsePoint(fullPath);
            EnsureType(fullPath, expectDirectory);
            return fullPath;
        }

        return ResolvePath(path, requireExisting: true, expectDirectory);
    }

    private string ResolvePath(string path, bool requireExisting, bool expectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || Path.IsPathRooted(path))
        {
            throw new BridgeException(ExitCode.Security, $"非法项目相对路径：{path}");
        }

        var systemPath = path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_root, systemPath));
        EnsureInsideRoot(fullPath);
        if (requireExisting && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new BridgeException(ExitCode.Security, $"项目文件不存在：{NormalizeRelative(path)}");
        }

        EnsureNoReparsePoint(fullPath);
        if (requireExisting)
        {
            EnsureType(fullPath, expectDirectory);
        }

        return fullPath;
    }

    private void EnsureInsideRoot(string fullPath)
    {
        var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(_root, _pathComparison)
            && !fullPath.StartsWith(rootWithSeparator, _pathComparison))
        {
            throw new BridgeException(ExitCode.Security, $"路径越过项目目录边界：{fullPath}");
        }
    }

    private void EnsureNoReparsePoint(string fullPath)
    {
        if (_security.AllowProjectSymlinks)
        {
            return;
        }

        EnsureInsideRoot(fullPath);
        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new BridgeException(ExitCode.Security, $"项目根目录不能是符号链接或重解析点：{_root}");
        }

        if (relative == ".")
        {
            return;
        }

        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BridgeException(ExitCode.Security, $"拒绝访问符号链接或重解析点：{current}");
            }
        }
    }

    private IEnumerable<string> EnumerateFilesSafely()
    {
        var pending = new Stack<string>();
        pending.Push(_root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) && !_security.AllowProjectSymlinks)
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private string NormalizeRequiredPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || Path.IsPathRooted(pattern) || pattern.IndexOf('\0') >= 0)
        {
            throw new BridgeException(ExitCode.Security, $"非法必读模式：{pattern}");
        }

        var normalized = NormalizeRelative(pattern.Trim());
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new BridgeException(ExitCode.Security, $"必读模式不能越过项目目录：{pattern}");
        }

        return normalized.TrimStart('/');
    }

    private Regex GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        builder.Append(".*");
                        index++;
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return new Regex(
            builder.ToString(),
            RegexOptions.CultureInvariant
            | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    private static string ReadUtf8(string path)
    {
        try
        {
            return File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException exception)
        {
            throw new BridgeException(ExitCode.Security, $"文件不是有效 UTF-8：{path}", exception);
        }
    }

    private static void EnsureType(string path, bool expectDirectory)
    {
        if (expectDirectory && !Directory.Exists(path))
        {
            throw new BridgeException(ExitCode.Security, $"目标不是目录：{path}");
        }

        if (!expectDirectory && !File.Exists(path))
        {
            throw new BridgeException(ExitCode.Security, $"目标不是文件：{path}");
        }
    }

    private static string RequiredString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        OptionalString(arguments, name)
        ?? throw new BridgeException(ExitCode.Security, $"工具参数缺少字符串字段：{name}");

    private static string? OptionalString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static bool HasWildcard(string value) =>
        value.IndexOfAny(new[] { '*', '?' }) >= 0;

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
