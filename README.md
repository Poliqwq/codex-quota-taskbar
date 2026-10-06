# Codex 周额度任务栏控件

在 Windows 11 任务栏左侧显示当前 Codex / ChatGPT 账户的剩余周额度：紫色渐变、流动粒子和白色百分比圆钮。

![控件预览](assets/preview.png)

*预览为合成的 100% 示例，不是实际账户数据。*

- 每 60 秒刷新；点击查看额度、重置时间和更新时间。
- 右键或托盘菜单可立即刷新、设置开机启动、退出；开机启动默认关闭。
- 默认宽度为 456 DIP，在 125% 显示缩放下约为 570 像素；粒子流动与闪烁速度为初版的 4 倍。
- 查询失败时保留上次读数并淡化显示；首次读取成功前显示 `—`。

## 运行

需要 Windows 11 x64、.NET Framework 4.8，以及已登录 ChatGPT 账户的 Codex 桌面端或 Codex CLI。

下载 [Windows ZIP](downloads/CodexQuotaTaskbar-v1.0.0-win-x64.zip?raw=1)，解压到可写目录，双击 `CodexQuotaTaskbar.exe`。保留旁边的 `CodexQuotaTaskbar.exe.config`。程序会自动寻找 Codex 桌面端附带的 CLI 或 PATH 中的 `codex.exe`；也可在程序旁创建 `codex.path`，写入本机 `codex.exe` 的完整路径。

额度通过官方 `codex app-server` 的 `account/rateLimits/read` 读取，按 10080 分钟识别周窗口，优先读取 `rateLimitsByLimitId.codex`。使用已有登录，不需要 API Key，也不发起模型聊天。

## 从源码构建

在仓库根目录运行：

```powershell
.\build.ps1
```

构建使用 Windows 自带的 .NET Framework C# 编译器，无需安装 .NET SDK。编译前退出正在运行的控件。生成的 EXE 与已提供的配置文件放在一起即可运行。

```powershell
.\CodexQuotaTaskbar.exe --refresh   # 刷新正在运行的控件
.\CodexQuotaTaskbar.exe --stop      # 退出正在运行的控件
.\CodexQuotaTaskbar.exe --verify    # 生成合成值的静态预览
```

## 使用范围

控件是贴合任务栏的独立透明 WPF 窗口，支持主屏底部任务栏、DPI 缩放和左侧小组件避让。任务栏自动隐藏、应用全屏或左侧空间不足时，控件会隐藏，托盘入口仍可使用。它不会修改 Explorer 或任务栏配置。

额度取决于官方接口及当前账户，接口不可用或账户没有可识别的周窗口时会显示读取错误。运行目录中的 `runtime.json` 包含最近额度与窗口位置，`widget.log` 记录错误，均为本机运行数据，不应上传；程序不保存登录凭据。

[Codex App Server 官方接口文档](https://developers.openai.com/codex/app-server/)
