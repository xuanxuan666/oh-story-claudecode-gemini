# gemini-bridge.exe（内置执笔桥）

story 技能「Gemini 执笔」的**预编译**后端（Windows / 框架依赖单文件）。让 Gemini 3.1 Pro 用只读文件工具自读项目文件写正文——Claude 当大脑（选料 / 审校 / 质检），Gemini 当枪手。**桥源码不在本仓库。**

- **依赖**：**.NET 10 运行时**（https://dotnet.microsoft.com/download/dotnet/10.0 ，装 Runtime 即可，无需 SDK）。缺运行时时运行会提示 “You must install .NET”。
- **登录（浏览器授权 Antigravity，一次）**：`gemini-bridge.exe --login` — 自动打开浏览器，用 Google 账号授权；成功后 refresh_token 长期有效、自动刷新，凭证存 `%LOCALAPPDATA%\TwinScribe\antigravity-*.json`。
- **自检**：`gemini-bridge.exe --selftest`（出一句正文＝登录态+模型可用）；`--list-models` 看可用模型。
- **写一章**（story 技能自动调用，一般不用手动跑）：
  ```
  gemini-bridge.exe --write --project "<书目录>" --brief-file "<简报>" --require "<必读文件,逗号分隔>"
  ```
  - `stdout` = 正文；`stderr` = 工具轨迹 + `[读取]` / `[✓ 必读覆盖]` / `[⚠ 漏读必读]`
  - `--require` 漏读会被**监督闸自动打回补读**；缺登录退出码 2 → 重跑 `--login`
  - `--model <id>` 覆盖模型；默认 `gemini-pro-agent` = Gemini 3.1 Pro (High)
- 文件工具**只读**且**锁死在 `--project` 目录内**，不读写项目外文件。
- 认证走 Antigravity 的 Google OAuth（Code Assist 后端），**非官方付费 API key**，请在自己的订阅额度内个人使用、遵守相应服务条款。
