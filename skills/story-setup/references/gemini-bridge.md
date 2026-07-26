# gemini-bridge：远程 CLIProxyAPI 执笔桥

`gemini-bridge` 只连接用户配置的远程 CLIProxyAPI 普通接口。它不调用 Management API，不启动 CLIProxyAPI 程序，不处理 Google/Antigravity OAuth，也不读取远程账号凭据。

## 配置

默认配置路径：

```text
%LOCALAPPDATA%\TwinScribe\gemini-bridge\config.json
```

用 `--config <path>` 指定其他位置，或用 `GEMINI_BRIDGE_CONFIG` 设置默认路径。配置优先级为：`--config` > `GEMINI_BRIDGE_CONFIG` > 默认路径。

```json
{
  "version": 1,
  "server": {
    "baseUrl": "https://proxy.example.com",
    "apiKey": "明文 API Key"
  },
  "model": {
    "id": "gemini-pro-agent",
    "protocol": "gemini",
    "thinkingLevel": "high"
  },
  "agent": {
    "timeoutSeconds": 600,
    "requestRetries": 3,
    "maxToolTurns": 20,
    "requiredReadRetries": 2,
    "emptyResponseRetries": 2,
    "maxOutputTokens": 32768
  },
  "security": {
    "allowInsecureHttp": false,
    "allowProjectSymlinks": false,
    "maximumFileBytes": 1048576
  },
  "logging": {
    "format": "text",
    "level": "info"
  }
}
```

`server.apiKey` 按用户要求明文保存。不要把真实配置复制进小说项目或提交 Git。日志、诊断和默认的 `--show-config` 会隐藏密钥。

## 命令

```text
gemini-bridge.exe --login
gemini-bridge.exe --doctor
gemini-bridge.exe --models
gemini-bridge.exe --show-config
gemini-bridge.exe --logout
```

`--login` 的含义是配置并验证远程 CLIProxyAPI 地址、API Key 和默认模型；输入地址与 API Key 后，它先输出普通 `/v1/models` 返回的全部模型，再询问默认模型。直接输入列表中的完整 ID，或回车接受方括号内的建议值。它不是 Google OAuth 登录。远程服务器的上游账号由服务器管理员维护。

非交互环境可将 API Key 从标准输入交给登录命令：

```text
gemini-bridge.exe --login --base-url <https-url> --model <model-id> --api-key-stdin
```

写作命令：

```text
gemini-bridge.exe --write --project "<书目录>" --brief "<简报>" ^
  --require "<必读文件或通配>" --require "<另一个必读文件>" ^
  [--output "<项目内草稿路径>"] [--model "<远程模型 ID>"]
```

- 重复传 `--require`，不要把清单拼成逗号字符串。
- 不传 `--output` 时正文写到 stdout；传入时原子写入项目目录内的指定文件。
- stderr 输出工具轨迹、`[读取]`、`[✓ 必读覆盖]` 或 `[⚠ 漏读必读]`。
- `--project` 沙箱内只开放 `read_file` 与 `list_directory`；默认拒绝符号链接和目录越界。
- 远程地址默认必须是 HTTPS；只有显式设置 `allowInsecureHttp: true` 才允许 HTTP。

## 退出码

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | API Key 无效或缺失 |
| 3 | 参数或配置错误 |
| 4 | 远程 API 或网络错误 |
| 5 | Agent、空响应或必读覆盖失败 |
| 6 | 文件沙箱或安全策略拒绝 |

## 依赖

预编译 Windows x64 版本需要 .NET 10 Runtime。源码位于仓库 `tools/gemini-bridge/`。
