# dsh-wv2 — 构建 / 发布 / 安装

## 环境
- .NET 8 SDK（`dotnet --list-sdks` 应显示 8.0.x）
- WebView2 Runtime（Win10/11 通常已带）
- 系统 Node.js ≥ 22.19 + 全局 dsh（运行时要求，构建不需要）

## 常用命令
```powershell
# 编译检查（主 + 测试）
dotnet build src\DeepSeekHarness.Desktop\DeepSeekHarness.Desktop.csproj -c Release
dotnet test  tests\DeepSeekHarness.Desktop.Tests\DeepSeekHarness.Desktop.Tests.csproj -c Release

# 直接跑开发版（调试用）
cd src\DeepSeekHarness.Desktop && dotnet run

# 一次性安装（发布 + 装到 %LOCALAPPDATA%\Programs\DSH WV2 + 桌面/开始菜单快捷方式）
powershell -ExecutionPolicy Bypass -File .\install.ps1

# 卸载
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

## 手动发布（了解产物）
```powershell
cd src\DeepSeekHarness.Desktop
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\..\release
# 产物：release\DSH WV2.exe + WebView2Loader.dll（+ Assets\）—— 需要 .NET 8 运行时
```

## 说明
- 框架依赖单文件（主 exe ~2.5MB），需已装 .NET 8 运行时。
- WebView2 Profile 数据在 `%LOCALAPPDATA%\DSH WV2\WebView2`；壳设置/日志在 `%LOCALAPPDATA%\DSH WV2`。
- 会话/数据与 dsh 共享 `~/.dsh`。
- 验收与已知限制见 `VERIFICATION.md`。
