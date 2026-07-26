# Gemini Bridge 2

面向远程 CLIProxyAPI 的最小 Gemini 执笔客户端。它只使用普通模型接口：

- `GET /v1/models`
- `POST /v1beta/models/{model}:generateContent`

项目不包含、不启动也不管理 CLIProxyAPI 服务端，不调用 Management API，不处理 Google / Antigravity OAuth。

## 构建与测试

```powershell
dotnet build src\GeminiBridge\GeminiBridge.csproj -c Release
dotnet run --project tests\GeminiBridge.Tests\GeminiBridge.Tests.csproj -c Release
```

测试使用进程内临时 HTTP 服务模拟远程 CLIProxyAPI，不需要本地代理程序或真实 API Key。

## 发布 Windows 单文件

从仓库根目录运行：

```powershell
dotnet publish tools\gemini-bridge\src\GeminiBridge\GeminiBridge.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -p:CetCompat=false -o skills\story-setup\bin
```

发布物依赖 .NET 10 Runtime。用户命令、明文配置格式和安全边界见
`skills/story-setup/references/gemini-bridge.md`。
