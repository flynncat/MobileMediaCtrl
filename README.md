# MobileMediaCtrl（外接盘媒体浏览与复制）

面向 **Windows** 的小工具：在插入 **U 盘** 或连接 **手机（MTP）** 后自动打开窗口，按**日期时间线**浏览照片与视频，支持勾选、复制到指定文件夹，以及拖到桌面、资源管理器或其它本程序窗口（按对方窗口的「路径栏」作为目标目录）。

## 功能概要

- 自动检测可移动磁盘与 MTP 设备，**单进程多窗口**（多块盘/多台设备可同时各开一窗）。
- **时间线**：按本地自然日分组；U 盘文件优先用 EXIF 拍摄时间，否则用修改时间；MTP 使用设备端时间信息。
- **路径栏**：明确「复制到哪个文件夹」，避免误拷（与 PRD 一致）。
- **拖放**：拖到系统外壳（先按需将 MTP 文件暂存到临时目录再 `FileDrop`）；窗口间拖放使用自定义载荷，复制到目标窗口的路径栏目录。
- **缩略图**：本地缓存目录 `%LocalAppData%\MediaBrowser\thumbs`。

## 环境要求

- Windows 10 / 11（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022（工作负载：**使用 .NET 的桌面开发**，含 WPF）

详细联调说明见 [docs/dev-environment.md](docs/dev-environment.md)，需求摘要见 [docs/prd.md](docs/prd.md)。

## 在 Visual Studio 2022 中编译

1. 打开 `MediaBrowser.sln`
2. 右键解决方案 → **还原 NuGet 包**
3. **生成 → 生成解决方案**（Ctrl+Shift+B）
4. 将启动项目设为 **MediaBrowser.App**，**F5** 或 **Ctrl+F5** 运行

输出目录示例：`src/MediaBrowser.App/bin/Debug/net8.0-windows10.0.17763.0/MediaBrowser.App.exe`

## 命令行

```bash
dotnet restore MediaBrowser.sln
dotnet build MediaBrowser.sln -c Release
dotnet test tests/MediaBrowser.Tests/MediaBrowser.Tests.csproj -c Release
dotnet run --project src/MediaBrowser.App/MediaBrowser.App.csproj -c Release
```

> 说明：WPF 与 MTP 仅能在 **Windows** 上完整构建与运行；单元测试项目仅依赖 Core，可在装有 .NET 8 SDK 的环境执行 `dotnet test`。

## 仓库结构

| 路径 | 说明 |
|------|------|
| `src/MediaBrowser.Core` | 领域模型、本地文件枚举、时间线分组、复制选项 |
| `src/MediaBrowser.App` | WPF 界面、设备监听、MTP（[MediaDevices](https://www.nuget.org/packages/MediaDevices)）、拖放与缩略图 |
| `tests/MediaBrowser.Tests` | 单元测试 |
| `docs/` | PRD 与环境说明 |

## 许可证

若未另行声明，以仓库内 `LICENSE` 为准（如无 LICENSE 文件，使用前请自行补充）。

## 相关链接

- 远程仓库：<https://github.com/flynncat/MobileMediaCtrl>
