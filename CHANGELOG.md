# Changelog — dsh-wv2

## [1.0.0] — 2026-09-05

首个正式版。C#/WPF + WebView2 的 DeepSeek Harness 桌面壳（替代 Electron 版 dsh-lite，不打包 Chromium）。

### 核心
- WebView2 打开 dsh WebUI；复用系统 node + 全局 dsh（共享 `~/.dsh`）
- 托盘驻留：完整菜单（打开/重载/重启服务/数据目录/日志目录/终端/通知/开机自启/关于/退出）
- 单实例 + 错误捕获 + 崩溃/加载自愈 + Ctrl+Alt+D 全局快捷键 + 唤醒重载
- 窗口大小/位置记忆（越界校验）、开机自启（注册表 Run）
- 任务完成通知（会话日志 zstd 轮询）；审批通知（会话日志 `approval/asked`）
- 安全：导航锁、权限最小化、外链走系统浏览器、无原生桥

### 说明
- 提问（自由问答）通知未实现：dsh 走宿主↔UI 内部 RPC，不落盘、无外部干净通道。
- 曾尝试 mux WebSocket / DOM 观察两条审批通知路线，后因 dsh 0.1.2 无干净接口而废弃，改用会话日志 `approval/asked`。

### 工程
- .NET 8，框架依赖单文件发布（~2.5MB），CI build+publish。
