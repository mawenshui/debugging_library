# 调试资料汇总平台（Windows 离线版）

目标：提供一个完全离线的 Windows 桌面程序，用于汇总与沉淀调试问题资料；支持在不同实例之间通过离线包或局域网进行数据交换，并在本机完成检索、复盘与复用。

## 关键约束

- 全离线：不依赖服务器、云存储或在线账号体系
- 两端数据交换：通过“导入/导出包”文件完成（U 盘/共享盘/邮件等任意载体）
- Windows 优先：桌面端体验、可部署与可维护性优先

## 最小可用功能（MVP）

- 问题管理
  - 新建/编辑/删除（软删除）问题记录
  - 问题列表、详情页展示
  - 标签管理与多标签筛选
  - 附件上传与查看（截图、日志、文档）
- 搜索与检索
  - 关键字全文检索（标题/现象/原因/方案/环境）
  - 相关性排序与高亮摘要
  - 支持前缀匹配的“模糊”查询体验（后续可增强为编辑距离/拼写纠错）
- 离线导入/导出
  - 个人库 → 总库：增量导出、总库导入合并
  - 总库 → 个人库：条件导出（全量/时间窗/标签等）、个人库导入合并
  - 导入报告：记录成功/跳过/冲突条目及原因
- 冲突处理
  - 默认字段级“最后写入优先（LWW）”
  - 冲突可视化：保留两版内容，支持人工选择覆盖

## 非目标（当前阶段）

- 在线同步/多端实时协作
- 复杂权限系统（以离线文件加密与基本口令保护为主）
- 企业级工作流（审批/发布/订阅）与复杂统计报表

## 技术栈（推荐）

- .NET 8（C#）+ WPF（Windows 桌面）
- MVVM：CommunityToolkit.Mvvm
- 存储：SQLite（单文件数据库）
- 搜索：SQLite FTS5（必要时引入 Lucene.NET 增强模糊能力）
- 导入导出包：ZIP + manifest（JSON）
- 附件：文件系统对象存储（按内容哈希去重）+ 数据库元数据
- 安全（可选但建议）：导出包 AES-GCM 加密 + SHA-256 校验（可再加签名）

## 离线数据交换概述

- 两端各维护本地 SQLite 数据库（个人库/总库）
- 数据通过“变更包（Change Package）”流转：
  - 包内包含：manifest、记录变更集、附件文件、校验信息
  - 支持全量与增量两种导出模式

## 当前状态

- 已包含可运行的 Windows 离线原型：问题录入、全文检索（Hybrid：FTS5 + 包含匹配回退）、导入/导出（全量/增量）、冲突中心人工决议、职业/角色模板与“职业固定字段”可配置、设置页操作密码与批量物理删除（危险）、批量导出（Excel/CSV/JSONL）、表单导入（Excel）、附件预览（图片/文本类）、局域网交换、统一浅色玻璃渐变主题（含 DatePicker/DataGrid/PasswordBox/ListBox/ComboBox 等控件主题化）、自定义标题栏（支持拖拽/双击/系统菜单/最小化最大化关闭，最大化不遮挡任务栏）、主界面本机实例ID支持点击复制（2026-01）

## 文档

- 文档索引：[docs/README.md](file:///d:/_TraeProject/debugging_library/docs/README.md)
- 数据模型与包规范：[数据模型与离线同步包规范.md](file:///d:/_TraeProject/debugging_library/docs/数据模型与离线同步包规范.md)
- 后续优化计划：[开发计划.md](file:///d:/_TraeProject/debugging_library/docs/开发计划.md)

## 开发与发布

- 构建
  - `dotnet build FieldKb.sln -c Release`
- 测试
  - `dotnet test FieldKb.sln -c Release`
- 运行（WPF）
  - `dotnet run --project src/FieldKb.Client.Wpf/FieldKb.Client.Wpf.csproj`
- 发布（win-x64）
  - 可使用发布配置文件：[WinX64.pubxml](file:///d:/_TraeProject/debugging_library/src/FieldKb.Client.Wpf/Properties/PublishProfiles/WinX64.pubxml)
