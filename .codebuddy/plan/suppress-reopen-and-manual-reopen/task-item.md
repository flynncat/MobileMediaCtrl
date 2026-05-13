# 实施计划

- [ ] 1. 在 `DeviceArrivalCoordinator` 中引入"已抑制会话键"集合及相关 API
   - 在 `DeviceArrivalCoordinator.cs` 中新增私有字段 `_suppressedSessionKeys`（`HashSet<string>`，使用 `_gate` 串行访问）
   - 新增公开方法 `SuppressSession(string sessionKey)` / `IsSuppressed(string sessionKey)` / `UnsuppressSession(string sessionKey)`
   - 新增公开方法 `GetReopenableDevices()` 返回当前已连接但处于抑制状态的设备列表（直接返回可复用的 `DeviceSessionDescriptor` 列表，含 `SessionKey` / `Kind` / `DisplayName` / 卷根路径或 MTP 设备名）
   - _需求：1.1, 1.2, 4.1_

- [ ] 2. 让窗口关闭事件写入"已抑制"集合
   - 修改 `MediaWindowFactory.OpenForDevice`：窗口 `Closed` 时除了 `MediaWindowRegistry.Unregister`，再调用 `ApplicationSession.Coordinator.SuppressSession(descriptor.SessionKey)`
   - 同时让协调器记住 `descriptor` 本身（用于 GetReopenableDevices 时复用），可在 `SuppressSession` 接口里改为 `SuppressSession(DeviceSessionDescriptor descriptor)`，内部维护 `Dictionary<string, DeviceSessionDescriptor>`
   - 检查 `MediaWindow.xaml.cs` 中现有 `Closed` 处理（包含 `NotifyMtpSessionClosed` 调用），确保两次 `Closed` 订阅之间不会出现冲突
   - _需求：1.1, 4.1_

- [ ] 3. 修改 `RefreshVolumes` 以支持抑制逻辑及插拔时的清理
   - 在循环中跳过 `IsSuppressed(key)` 为真的卷
   - 引入私有字段 `_lastVolumeSessionKeys` 用于记录上一轮快照
   - 在方法末尾比较"本轮枚举到的会话键集合"与 `_lastVolumeSessionKeys`，将本轮缺失的会话键调用 `UnsuppressSession`（U 盘真正被拔出 → 解除抑制）
   - _需求：1.2, 1.3, 4.3_

- [ ] 4. 修改 `RefreshMtpDevices` 以支持抑制逻辑及拔出时的清理
   - 在循环中跳过 `IsSuppressed(BuildMtpSessionKey(name))` 为真的设备
   - 调整 `NotifyMtpSessionClosed`：不再清除 `_mtpSnapshot`（避免抑制立即失效），改为仅作为兼容钩子保留或直接在 `MediaWindow` 关闭逻辑里移除该调用
   - 在 `RefreshMtpDevices` 中：对"上一轮 `_mtpSnapshot` 存在但本轮 `current` 缺失"的设备，调用 `UnsuppressSession`（真正拔出 → 解除抑制）
   - _需求：1.4, 4.1, 4.2_

- [ ] 5. 在中英文资源文件中新增按钮与提示文案
   - `Languages/zh-CN.xaml` 添加：`MainWindow_Reopen`（"重新打开窗口"）、`MainWindow_NoReopenable`（"当前没有可重新打开的设备，请插入 U 盘或连接手机。"）、`MainWindow_ReopenTitle`（"重新打开窗口"）
   - `Languages/en-US.xaml` 添加对应英文文案：`Reopen Window` / `No connected devices to reopen. Please insert a USB drive or connect your phone.` / `Reopen Window`
   - _需求：3.1, 3.2, 3.4_

- [ ] 6. 在 `MainWindow.xaml` 中新增"重新打开窗口"按钮
   - 将原有 `Settings` 单按钮改为水平 `StackPanel`（`Orientation="Horizontal"`），内置"设置"按钮和"重新打开窗口"按钮，按钮间距 8-12px，保持原有左对齐和视觉样式
   - 给新按钮命名（`x:Name="ReopenButton"`），文案使用 `{DynamicResource MainWindow_Reopen}`，绑定 `Click="Reopen_Click"`
   - _需求：2.1, 3.3_

- [ ] 7. 在 `MainWindow.xaml.cs` 中实现"重新打开窗口"按钮的点击逻辑
   - 新增 `Reopen_Click(object sender, RoutedEventArgs e)` 处理：调用 `ApplicationSession.Coordinator.GetReopenableDevices()` 获取 `DeviceSessionDescriptor` 列表
   - 列表为空：弹出 `MessageBox`，使用 `MainWindow_NoReopenable` 文案与 `MainWindow_ReopenTitle` 标题
   - 列表非空：动态构造 `ContextMenu` 挂在 `ReopenButton.ContextMenu`，每个 `MenuItem.Header = descriptor.DisplayName`，`Click` 时调用 `Coordinator.UnsuppressSession(descriptor.SessionKey)` 后 `MediaWindowFactory.OpenForDevice(descriptor)`，最后 `ReopenButton.ContextMenu.IsOpen = true`
   - 关闭再次出现时遵循需求 1 的循环逻辑（关闭→抑制→可再次重开）
   - _需求：2.2, 2.3, 2.4, 2.5, 4.4_

- [ ] 8. 端到端自测验证
   - U 盘场景：插入 → 自动弹窗；关闭后等待 5 秒确认未重弹；点击"重新打开窗口"→ 菜单中存在该 U 盘并可重开；再次关闭后仍可被抑制；拔出后再次插入 → 自动弹窗恢复
   - 无设备场景：在没有任何被抑制设备时点击按钮 → 弹出"无可重开设备"提示
   - 多语言场景：切换中英文语言并重启 → 按钮文案与提示均按所选语言显示
   - MTP 场景：连接手机 → 自动弹窗；关闭 → 不重弹；按钮重开成功；断开手机后重新连接 → 自动弹窗
   - 编译并运行所有现有单元测试，确保未破坏现有功能
   - _需求：1.1-1.5, 2.1-2.5, 3.1-3.4, 4.1-4.4_
