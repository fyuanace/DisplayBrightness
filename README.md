# DisplayBrightness · 显示器亮度

通过 DDC/CI 调节外接显示器的 Windows 桌面工具。支持多屏独立调节、同步修正、每日定时和托盘运行。

## 下载与运行

1. 从 [GitHub Releases](https://github.com/fyuanace/DisplayBrightness/releases/latest) 下载 Windows x64 的 `.exe`。
2. 首次运行时，如系统提示缺少运行时，请安装 [Microsoft .NET 8 桌面运行时（Windows x64）](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)。
3. 在显示器自身的菜单中开启 DDC/CI，然后运行下载的程序。

发布版是单文件程序，可直接运行。显示器和连接链路都需要支持 DDC/CI。遇到读取失败时，可先确认显示器已点亮，再点工具栏的刷新图标。

## 亮度调节

| 模式 | 操作 |
| --- | --- |
| 独立调节 | 关闭「同步编辑」，在各显示器卡片中设置亮度。 |
| 同步调节 | 开启「同步编辑」，设置基准亮度和各屏的亮度修正。基准为 10%，修正为 +5 时，该屏亮度为 15%。 |
| 定时亮度 | 在「定时亮度」中添加开始时间。同步模式设置基准亮度，独立模式可以为各屏分别设置。 |

- 数值可用键盘输入，也可用输入框的上下箭头调整。手动调节按回车或切换焦点后保存并应用。
- 定时计划每天重复。只有一条计划时全天使用该亮度。多条计划按开始时间切换，最后一条延续到次日第一条开始。
- 启用定时后，程序启动时会应用当前时段。同步模式下仍可修改各屏的亮度修正。

## 托盘与开机自启

- 关闭窗口后，程序继续在托盘运行。托盘菜单可查看各屏亮度、定时计划和退出程序。
- 打开工具栏的「开机自启」后，程序会在当前 Windows 用户登录时静默进入托盘。此设置使用当前用户的注册表项，可在普通用户权限下开启。
- 移动或重命名程序后，从新位置打开，并重新开启「开机自启」以更新启动路径。

## 本地数据

设置保存在 `%LOCALAPPDATA%\DisplayBrightness`。

| 文件 | 内容 |
| --- | --- |
| `sync.json` | 同步模式、基准亮度、每台屏幕的名称、位置、亮度修正和独立亮度。 |
| `schedule.json` | 定时开关、开始时间和各时段的亮度。 |

## 从源码构建

在 Windows 上安装 .NET 8 SDK，然后在仓库目录运行。

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o publish
```

发布文件为 `publish\DisplayBrightness.exe`。
