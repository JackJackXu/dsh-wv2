# dsh-wv2 — DeepSeek Harness Desktop Client

> **长名**：DeepSeek Harness Desktop Client — Powered by C#/WPF on WebView2
> **定位**：用系统原生 **WebView2**（不打包 Chromium）做的更轻、更原生的 DeepSeek Harness 桌面壳，Windows 单平台。
> 旧 Electron 版 **dsh-lite** 保留作参考。

## 状态

**v1.0（首个正式候选）**：核心功能 + 安全/稳定加固 + 任务完成通知已可用；待最终验收后定版。

## 功能

- **WebView2 原生壳**：复用系统 WebView2，不自带 Chromium（单文件 ~2.5MB，需已装 .NET 8 运行时）。
- **自动起 dsh 服务**：复用系统 node + 全局 dsh（与开发版一致，共享 `~/.dsh`）；`--port 0 --no-open`，解析带 token 的 URL；WebView2 作为真浏览器自动完成 token→cookie 鉴权。
- **托盘驻留**：关窗藏托盘；完整菜单：打开 / 重载 UI / 重启 dsh 服务 / 打开数据目录 / 打开日志目录 / 打开终端(会话目录) / 开机自启 / 关于 / 退出。
- **任务完成通知**：轮询 `~/.dsh/sessions`（zstd 逐帧解压 JSONL），检测 `turn/end` → 托盘气泡（前台/托盘都弹）。
- **审批通知**：监听会话日志的 `approval/asked` 事件（工具授权请求）→ 托盘气泡。仅在 dsh 真实发起工具审批时触发。
- **全局快捷键** Ctrl+Alt+D 显隐。
- **开机自启**（注册表 Run，托盘勾选）。
- **设置持久化**：窗口大小/位置/最大化（越界校验）、上次端口。
- **崩溃自愈**：WebView2 崩溃重载、页面加载失败退避重试、系统唤醒重载。

## 安全性

- 关 DevTools/状态栏；导航锁只允许 dsh loopback，外链/新窗走系统浏览器；权限最小化（仅剪贴板读）。
- **零原生桥**：不向页面暴露本地能力（无 preload/IPC 等价物）。

## 构建 / 发布

```powershell
cd C:\MyMy\my_work\DSH_DEV\dsh-wv2\src\DeepSeekHarness.Desktop
dotnet build -c Release
# 发布单文件（框架依赖，需已装 .NET 8 运行时）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../../release
```

## 环境要求

- Windows 10/11
- **.NET 8 运行时**（构建需 .NET 8 SDK）
- WebView2 Runtime（Win10/11 通常已装）
- 系统 Node.js ≥ 22 + 全局 dsh（`npm install -g @deepseek-ai/dsh`）

## 已知说明

- **提问通知未实现**：dsh 的提问走宿主↔UI 内部 RPC，不落盘、无外部干净通道，硬做只能依赖页面 DOM（脆弱），故暂不做。
- 避免与开发版 web / dsh-lite 同时开（共享 `~/.dsh`）。
- 会话与数据统一存 `~/.dsh`；壳自身状态在 `%LOCALAPPDATA%\DSH WV2\`。

## 目录

```
dsh-wv2/
├── src/DeepSeekHarness.Desktop/   # WPF 单项目
│   ├── App.xaml(.cs)              # 单实例 + 错误捕获
│   ├── MainWindow.xaml(.cs)       # 窗口/托盘/服务/安全
│   ├── MainWindow.Shell.cs        # 快捷键/唤醒/自愈/通知接线
│   └── Services/                  # Settings/DshProcess/SessionWatcher
├── release/                       # 发布输出（git 忽略）
└── README.md
```
