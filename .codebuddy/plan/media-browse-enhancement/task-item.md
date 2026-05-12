# 实施计划

- [ ] 1. 修复 `BuildImageThumbnail` 中 GDI+ 保存异常
   - 在 `ThumbnailLoader.cs` 的 `BuildImageThumbnail` 方法中，为 `bmp.Save(destPath, ImageFormat.Jpeg)` 添加 `ExternalException` 捕获
   - 捕获后先确保目标目录存在（`Directory.CreateDirectory`），若目录创建成功则重试保存一次
   - 若重试仍失败，回退调用 `BuildVideoThumbnailViaShell`（Shell API 方式）生成缩略图
   - 若 Shell API 也失败则静默返回（不抛异常），让调用方显示默认占位图标
   - 将 `OutOfMemoryException` 和 `ExternalException` 合并为统一的 `catch` 块处理
   - _需求：1.1、1.2、1.3、1.4_

- [ ] 2. 在文件系统扫描中排除隐藏文件
   - 修改 `FileSystemMediaCatalog.cs` 的 `EnumerateCore` 方法
   - 在 `MediaExtensionLists.IsMediaFile(file)` 检查之前，添加文件名过滤：跳过以 `.` 开头的文件（覆盖 `._xxx`、`.DS_Store` 等）
   - 使用 `Path.GetFileName(file).StartsWith('.')` 作为判断条件
   - _需求：2.1、2.3、2.4_

- [ ] 3. 在 MTP 扫描中排除隐藏文件
   - 修改 `MtpMediaCatalog.cs` 的 `EnumerateDirectoryRecursive` 方法
   - 在 `MediaExtensionLists.IsMediaFile(path)` 检查之前，添加文件名过滤：跳过以 `.` 开头的文件
   - 使用已有的 `fileName` 变量（`Path.GetFileName(path.TrimEnd('\\'))`）进行判断
   - _需求：2.2、2.3、2.4_

- [ ] 4. 重构 ViewModel 数据结构为两级分组（年→月）
   - 修改 `MediaWindowViewModel.cs`，将当前的单级 `GroupKey`（月份字符串）改为两级结构
   - 新增 `YearGroupViewModel` 类，包含 `Year`、`IsExpanded`（默认 true）、`ObservableCollection<MonthGroupViewModel>`
   - 新增 `MonthGroupViewModel` 类，包含 `Year`、`Month`、`DateLabel`、`IsExpanded`（默认 true）、`ObservableCollection<MediaTileViewModel>`
   - 将 `Tiles` 集合替换为 `ObservableCollection<YearGroupViewModel>` 作为顶层数据源
   - 移除 `CollectionViewSource` 分组逻辑，改为直接绑定层级集合
   - 重写 `AddItemsToFlatList` 为 `AddItemsToGroups`，按年月插入到正确的分组中
   - _需求：3.1、3.7_

- [ ] 5. 重构 XAML 为两级分组布局（年→月→媒体项）
   - 将 `MediaWindow.xaml` 中的 `ListBox` + `GroupStyle` 替换为嵌套的 `ItemsControl` 结构
   - 外层 `ItemsControl` 绑定 `YearGroups`，使用 `VirtualizingStackPanel` 作为面板
   - 年份模板：包含可点击的年份标题行（显示年份 + 媒体总数）+ 内层 `ItemsControl`（月份列表）
   - 月份模板：包含可点击的月份标题行 + 内层使用 `VirtualizingWrapPanel` 显示媒体项
   - 媒体项模板：复用现有的 160×200 卡片模板
   - _需求：3.1_

- [ ] 6. 实现年份和月份的折叠/展开功能
   - 在年份标题和月份标题上添加点击事件或使用 `ToggleButton` 绑定 `IsExpanded` 属性
   - 年份折叠时隐藏其下所有月份组（通过 `BooleanToVisibilityConverter` 绑定 `IsExpanded`）
   - 月份折叠时隐藏其下所有媒体项
   - 年份标题行显示该年份下的媒体总数统计（如 "2024年 (128)"）
   - 月份标题行显示该月份下的媒体数量（如 "3月 (42)"）
   - _需求：3.2、3.3、3.8_

- [ ] 7. 实现滚动时 Sticky Header（固定头）效果
   - 在 `MediaWindow.xaml` 中，在列表上方添加一个覆盖层 `Border`（包含年份和月份 TextBlock），默认隐藏
   - 在 `MediaWindow.xaml.cs` 中监听 `ScrollViewer.ScrollChanged` 事件
   - 滚动时遍历可视区域内的分组头，确定当前最顶部可见的年份和月份
   - 当分组头滚出可视区域时，将其文本复制到覆盖层并显示
   - 当新的分组头即将到达顶部时，实现推挤效果（通过 TranslateTransform 偏移旧的固定头）
   - _需求：3.4、3.5、3.6_

- [ ] 8. 适配缩略图懒加载逻辑到新的分组结构
   - 修改 `MediaWindow.xaml.cs` 中的懒加载逻辑，适配新的两级嵌套 `ItemsControl` 结构
   - 确保滚动时仅加载可视区域内的媒体项缩略图
   - 确保折叠的分组内的媒体项不触发缩略图加载
   - 验证拖拽功能在新结构下仍然正常工作（调整 `Tile_PreviewMouseMove` 等事件的绑定）
   - _需求：3.1、3.7_

- [ ] 9. 更新多语言资源文件
   - 在 `zh-CN.xaml` 和 `en-US.xaml` 中添加年份分组标签格式字符串（如 `Group_YearFormat`："{0}年"）
   - 添加折叠/展开相关的辅助文本（如 `Group_ItemCount`："{0} 项"）
   - 确保现有的 `Group_MonthFormat` 资源仍然兼容新的月份标题显示
   - _需求：3.1、3.8_

- [ ] 10. 编译验证与集成测试
   - 确保项目编译通过（0 错误 0 警告）
   - 运行现有单元测试确保全部通过
   - 连接实际设备（U盘/手机）验证：隐藏文件不再显示、图片预览不崩溃、分组折叠和 Sticky Header 正常工作
   - 提交代码到 GitHub
   - _需求：1.1-1.4、2.1-2.4、3.1-3.8_
xia