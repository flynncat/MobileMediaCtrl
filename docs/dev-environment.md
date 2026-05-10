# 开发与联调环境

## 平台要求

- **目标**：Windows 10 / 11（x64）。
- **SDK**：.NET 8 SDK（含 Windows 桌面开发工作负载，以支持 WPF）。
- **IDE**：Visual Studio 2022 或 VS Code + C# 扩展（构建 WPF 建议在 Windows 上执行）。

## 为何必须在 Windows 上构建与联调

- **WPF** 仅在 Windows 上受支持。
- **MTP** 通过 **WPD（Windows Portable Devices）** COM API 访问，需在 Windows 真机/虚拟机上验证。
- **设备插拔通知**（`WM_DEVICECHANGE` / `RegisterDeviceNotification`）与向 **资源管理器拖放** 需在 Windows 上验证。

## 若主开发机为 macOS / Linux

- 使用 **Windows 虚拟机**（Parallels、VMware、UTM 等）或 **物理 Windows 机** 进行：
  - `dotnet build` / `dotnet test`
  - MTP 与 U 盘插拔测试
- 本仓库中的项目文件可在任意系统上编辑；**构建与运行**请在 Windows 上执行。

## 建议测试矩阵

- **U 盘**：至少 1 个可移动 FAT32/exFAT 设备。
- **Android 手机**：至少 2 台不同品牌，USB 连接模式为 **文件传输 (MTP)**。
- **路径场景**：含中文路径、深层目录、大文件、无 EXIF 的照片。

## 代码签名（发布阶段）

- 对外分发安装包时建议 **Authenticode 签名**，降低 SmartScreen 警告，便于老年人安装。
