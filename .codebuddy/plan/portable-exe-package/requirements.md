# 需求文档

## 引言

本功能旨在为 MediaBrowser 应用提供"绿色版"单文件可执行程序（Single-File EXE）打包能力，并扩展应用的设置功能。最终用户得到的是一个**自包含、零依赖**的 `.exe` 文件，可以直接复制到任意 Windows 电脑或任意目录中运行，无需安装 .NET 运行时、无需配置任何环境。

同时，应用将提供两项核心设置：
1. **界面语言切换**（已有功能，需统一到新的配置存储模型）
2. **是否随 Windows 开机自启动**（新增功能）

所有用户配置统一存放在 `%LocalAppData%\MediaBrowser\settings.json`，便于集中管理和维护。


并且在用户**首次启动**应用时，会主动弹出一次性提示，询问是否启用开机自启动，避免用户错过此设置。

整个功能交付时还应包含一个**一键打包脚本**，开发者执行一条命令即可在 `General/` 文件夹中产出最终可分发的 `.exe` 文件。

## 需求

### 需求 1：单文件可移植 EXE 打包

**用户故事：** 作为一名最终用户，我希望拿到一个独立的 `.exe` 文件就能直接运行 MediaBrowser，以便我可以把它放到 U 盘、桌面或任何路径下使用，而无需安装 .NET 或其它依赖。

#### 验收标准

1. WHEN 开发者执行一键打包脚本 THEN 系统 SHALL 在 `General/` 文件夹中产出**单个** `MediaBrowser.exe` 文件（包含所有依赖、.NET 运行时、WPF 资源）
2. WHEN 用户将该 `.exe` 复制到任意目录或任意 Windows 10/11 (x64) 电脑 THEN 该程序 SHALL 可在没有预装 .NET 8 的环境下正常启动并运行
3. WHEN 打包脚本执行 THEN 系统 SHALL 启用 `PublishSingleFile=true`、`SelfContained=true`、`RuntimeIdentifier=win-x64`、`IncludeNativeLibrariesForSelfExtract=true` 等发布参数，并启用资源压缩 `EnableCompressionInSingleFile=true`
4. WHEN 打包完成 THEN `General/` 目录中 SHALL 仅包含分发所需文件（exe 主文件 + 必要的 README/说明），不应包含中间产物 (`*.pdb`、`obj/`、`bin/` 等)
5. IF 打包脚本运行前 `General/` 中已存在旧产物 THEN 脚本 SHALL 自动清理旧产物后再生成新文件
6. WHEN 用户双击该 `.exe` THEN 程序 SHALL 正常显示主窗口，且语言/设置等功能可用

### 需求 2：统一的配置存储

**用户故事：** 作为一名用户，我希望应用的所有偏好设置（语言、开机自启动、首次运行标记等）统一存放在一个固定位置，便于管理和排查问题。

#### 验收标准

1. WHEN 应用读写任何配置 THEN 系统 SHALL 始终使用 `%LocalAppData%\MediaBrowser\settings.json` 作为唯一配置文件路径
2. WHEN `%LocalAppData%\MediaBrowser` 目录不存在 THEN 系统 SHALL 在首次写入时自动创建该目录
3. WHEN 配置文件被读取或写入 THEN 系统 SHALL 在同一个 `settings.json` 中同时持久化语言、开机自启动和首次运行标记三个字段
4. WHEN 配置文件不存在或解析失败 THEN 系统 SHALL 使用默认值（语言=zh-CN，开机自启=false，IsFirstRun=true），不应抛出异常导致应用崩溃
5. WHEN 配置写入失败（如磁盘异常）THEN 系统 SHALL 静默忽略错误，不影响应用正常使用


### 需求 3：扩展设置窗口（开机自启动选项）

**用户故事：** 作为一名用户，我希望在"设置"窗口中除了切换语言之外，还能勾选"开机自启动"，以便我开机后无需手动启动该应用。

#### 验收标准

1. WHEN 用户打开设置窗口 THEN 系统 SHALL 在窗口中显示"语言"下拉框 和"开机自启动"复选框两项设置
2. WHEN 设置窗口加载 THEN "开机自启动"复选框 SHALL 反映当前注册表实际状态（即从 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 读取 `MediaBrowser` 项是否存在且指向当前 exe）
3. WHEN 用户勾选"开机自启动"并点击"保存" THEN 系统 SHALL 在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入名为 `MediaBrowser` 的字符串值，值为当前 `.exe` 的完整路径
4. WHEN 用户取消勾选"开机自启动"并点击"保存" THEN 系统 SHALL 删除上述注册表项
5. WHEN 用户点击"保存" THEN 系统 SHALL 同时持久化语言设置和开机自启动设置到 `settings.json`
6. IF 注册表写入失败（如权限问题）THEN 系统 SHALL 显示友好错误提示，不影响其他设置生效
7. WHEN 用户已通过自启动方式启动应用并选择"开机自启动"路径 THEN 系统 SHALL 始终使用当前 `.exe` 实际路径，避免路径漂移

### 需求 4：首次运行开机自启动提醒

**用户故事：** 作为一名首次启动应用的新用户，我希望被主动询问是否启用开机自启动，以便不会错过这个常用设置。

#### 验收标准

1. WHEN 应用启动且 `settings.json` 中标记为首次运行（IsFirstRun=true 或文件不存在）THEN 系统 SHALL 在主窗口显示后弹出一次性确认对话框，询问"是否在开机时自动启动 MediaBrowser？"
2. WHEN 用户在首次运行对话框中点击"是" THEN 系统 SHALL 启用开机自启动并保存到注册表与 `settings.json`
3. WHEN 用户在首次运行对话框中点击"否" THEN 系统 SHALL 不启用开机自启动，仅将 IsFirstRun 标记为 false
4. WHEN 首次运行对话框关闭（无论选择"是"或"否"）THEN 系统 SHALL 将 IsFirstRun 标记为 false 并写入 `settings.json`，确保后续启动不再弹出该对话框
5. WHEN 应用非首次运行 THEN 系统 SHALL 不弹出该对话框，直接进入主流程
6. WHEN 首次运行对话框显示 THEN 文案 SHALL 根据 `LanguageManager.CurrentLanguage` 显示对应语言（中文/英文）

### 需求 5：一键打包脚本

**用户故事：** 作为一名开发者，我希望执行一条命令就能完成构建并产出最终可分发的单文件 exe，以便我能快速发布版本。

#### 验收标准

1. WHEN 开发者在项目根目录执行打包脚本（如 `build.ps1` 或 `pack.ps1`）THEN 脚本 SHALL 自动完成 NuGet 还原、Release 编译、Publish 单文件发布、产物拷贝到 `General/` 等步骤
2. WHEN 脚本执行成功 THEN 控制台 SHALL 输出最终 `.exe` 的完整路径和文件大小，方便开发者确认结果
3. WHEN 脚本执行失败（任一步骤）THEN 脚本 SHALL 立即中止并以非零退出码返回，且打印清晰的错误信息
4. WHEN 脚本执行 THEN SHALL 支持参数化（至少支持 `-Clean` 清理参数和默认 `Release/win-x64` 配置）
5. WHEN 脚本执行 THEN SHALL 不在 `General/` 中保留 `.pdb` 调试符号文件（通过 `-p:DebugType=None -p:DebugSymbols=false` 或在打包后手动清理实现）

### 需求 6：Git 与忽略规则

**用户故事：** 作为一名维护者，我希望构建产物不会污染 git 仓库，但打包脚本本身需要被纳入版本控制。

#### 验收标准

1. WHEN 打包脚本生成 `General/*.exe` THEN `.gitignore` SHALL 包含规则使 `General/` 内的所有产物不被 git 跟踪
2. IF `General/` 内有需要纳入版本控制的固定资产（例如 `README.txt`、图标）THEN `.gitignore` SHALL 提供白名单豁免，确保这些文件可被提交
3. WHEN 打包脚本本身（如 `build.ps1`）被创建/修改 THEN 该脚本 SHALL 被纳入 git 版本控制
4. WHEN 本次功能开发完成 THEN 所有源代码、配置、脚本变更 SHALL 被一次性提交到 git，提交信息使用清晰的中文描述

### 需求 7：多语言文案补充

**用户故事：** 作为一名中英文双语用户，我希望新增的"开机自启动"和"首次运行提醒"文案在两种语言下都能正确显示。

#### 验收标准

1. WHEN 当前语言为中文 THEN 设置窗口中的"开机自启动"复选框文本 SHALL 显示为"开机时自动启动"或类似中文文案
2. WHEN 当前语言为英文 THEN 设置窗口中的"开机自启动"复选框文本 SHALL 显示为"Launch at Windows startup"或类似英文文案
3. WHEN 首次运行对话框显示 THEN 标题、提示文本、"是"/"否"按钮 SHALL 在 `zh-CN.xaml` 与 `en-US.xaml` 中均有对应资源键
4. WHEN 用户切换语言并重新打开设置窗口 THEN 所有新增文案 SHALL 正确反映当前语言
