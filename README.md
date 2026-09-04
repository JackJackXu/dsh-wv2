# dsh-wv2 — DeepSeek Harness Desktop Client

> **长名**：DeepSeek Harness Desktop Client — Powered by C#/WPF on WebView2
> **定位**：用系统原生 **WebView2**（不打包 Chromium）做的更轻、更原生的 DeepSeek Harness 桌面壳，Windows 单平台。
> 旧 Electron 版 **dsh-lite** 保留作参考。

## 状态

**v1.2**：v1.0/1.1 之上补齐 UX/稳定/安全增强 + **提问通知**（无需等 dsh 上游，从会话日志识别 `ask_user_question` 的 `tool/call`）。

## 功能

- **WebView2 原生壳**：复用系统 WebView2，不自带 Chromium（主 exe ~2.5MB + 少量必要旁车文件如 WebView2Loader.dll，需已装 .NET 8 运行时）。
- **自动起 dsh 服务**：复用系统 node + 全局 dsh（与开发版一致，共享 `~/.dsh`）；`--port 0 --no-open`，解析带 token 的 URL；WebView2 作为真浏览器自动完成 token→cookie 鉴权。冷启动与 WebView2 初始化并行。
- **托盘驻留**：关窗藏托盘；完整菜单：打开 / 重载 UI / 重启 dsh 服务 / 打开数据目录 / 打开日志目录 / **日志查看器** / 打开终端(会话目录) / 通知开关 / 开机自启 / 关于 / **检查 dsh 更新** / 退出。
- **任务完成通知**：监听 `~/.dsh/sessions`（zstd 逐帧解压 JSONL，文件系统增量监听 + 低频兜底），检测 `turn/end` → 托盘气泡。
- **审批通知**：监听会话日志 `approval/asked`（工具授权请求）→ 托盘气泡。
- **提问通知**：dsh 通过 `ask_user_question` 向你提问时（会话日志里 `tool/call` 的 arguments 含问题全文），弹气泡提醒"dsh 在问你一个问题"。
- **通知即有动静**：任何任务完成/审批/提问/异常，托盘图标经典闪烁；点气泡自动显示主窗口。
- **检查 dsh 更新**：读本地 dsh 版本 → 联网查 npmmirror（latest/next）→ 有新版先提示、你确认后才 `npm i -g` 一键升级，并提示重启服务生效。
- **开机自启**（注册表 Run，托盘勾选）。
- **设置持久化**：窗口大小/位置/最大化（越界校验）、上次端口。
- **崩溃自愈 + 健康检查**：WebView2 崩溃重载、加载失败退避、唤醒重载；dsh 端口失联×3 或服务丢失自动重启（防重入）。

## 安全性

- 关 DevTools/状态栏/**页面右键菜单**；导航锁只允许 dsh loopback，外链/新窗走系统浏览器；权限最小化（仅剪贴板读）。
- **零原生桥**：不向页面暴露本地能力（无 preload/IPC 等价物）。
- **退出清 cookie**：退出时清空 WebView2 会话 cookie（含 dsh-auth），下次启动自动用新 token 重新鉴权。

## 构建 / 发布

```powershell
cd C:\MyMy\my_work\DSH_DEV\dsh-wv2\src\DeepSeekHarness.Desktop
dotnet build -c Release
# 发布单文件（框架依赖，需已装 .NET 8 运行时）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../../release
```

## 验收与已知限制

已实测项与因缺少条件未验证/受限项见 **[VERIFICATION.md](VERIFICATION.md)**。

## 环境要求

- Windows 10/11
- **.NET 8 运行时**（构建需 .NET 8 SDK）
- WebView2 Runtime（Win10/11 通常已装）
- 系统 Node.js ≥ 22.19（或 ≥24）+ 全局 dsh（`npm install -g @deepseek-ai/dsh`）

## 已知说明

- 公开分发未做数字签名，Windows SmartScreen 可能提示"未知发布者"。

- 提问通知走会话日志里的 `ask_user_question` `tool/call`（不依赖页面 DOM，无需 dsh 上游改动）。
- 避免与开发版 web / dsh-lite 同时开（共享 `~/.dsh`）。
- 会话与数据统一存 `~/.dsh`；壳自身状态在 `%LOCALAPPDATA%\DSH WV2\`。

## 目录

```
dsh-wv2/
├── src/DeepSeekHarness.Desktop/   # WPF 单项目
│   ├── App.xaml(.cs)              # 单实例 + 错误捕获 + PID 锁
│   ├── MainWindow.xaml(.cs)       # 窗口/托盘/服务/安全/迁移/更新
│   ├── MainWindow.Shell.cs        # 唤醒/自愈/通知/健康检查接线
│   ├── LogViewerWindow.xaml(.cs)  # 应用内日志查看器
│   └── Services/                  # Settings/DshProcess/SessionWatcher/DshUpdater
├── release/                       # 发布输出（git 忽略）
└── README.md
```
