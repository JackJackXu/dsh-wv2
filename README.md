# dsh-wv2 — DeepSeek Harness Desktop Client (C#/WPF + WebView2)

> 长名：**DeepSeek Harness Desktop Client — Powered by C#/WPF on WebView2**
> 定位：用系统原生 WebView2（不打包 Chromium）做的更轻桌面壳；Windows 单平台。
> 旧 Electron 版 `dsh-lite` 保留作参考。

## 状态
- Phase 1 MVP（进行中）：主窗口 WebView2 打开 dsh WebUI + 托盘 + 设置持久化 + 自动启动 dsh 服务。
- 待验收后进入 Phase 2（快捷键/开机自启/通知等）。

## 构建
```powershell
cd C:\MyMy\my_work\dsh-wv2\src\DeepSeekHarness.Desktop
dotnet build -c Release
```
需要 .NET 8 SDK 与 WebView2 Runtime（Win10/11 通常已装）。

## 说明
- 复用系统 node + 全局 dsh（与 Electron 版一致，共享 `~/.dsh`）。
- 用 `--no-open` 起服务并解析带 token 的 URL；WebView2 作为真浏览器自动完成 token→cookie 鉴权。
