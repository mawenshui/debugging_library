# 调试资料汇总平台（Windows 离线版）

完全离线的 Windows 桌面程序：用于沉淀调试问题资料，支持本机检索复用，并可在不同实例之间通过离线包或局域网交换数据。

## 功能概览

- 问题记录：新建/编辑/删除（软删除），列表与详情展示
- 标签与筛选：标签管理、多标签筛选
- 附件：上传、预览（图片/文本类）、外部打开
- 搜索：Hybrid（FTS5 优先 + 包含匹配回退），支持分页
- 离线导入/导出：全量/增量导出与导入合并，生成导入报告
- 冲突中心：可视化差异并人工决议
- 职业/角色：模板与“职业固定字段”可配置
- 局域网交换：两端无需拷贝文件，支持拉取导入/推送导入（可配置共享密钥鉴权）

更完整的功能清单与细节说明见：[当前已实现功能与细节.md](file:///d:/_TraeProject/debugging_library/docs/当前已实现功能与细节.md)

## 目录结构（重要）

本项目按“程序目录内落盘”组织数据，避免存储位置分散：

- `<程序目录>\config\`
  - `appsettings.json`：默认配置（随发布）
  - `appsettings.user.json`：用户配置（运行期写入/升级保留）
  - `instance.json`：本机实例标识（首次运行生成）
- `<程序目录>\data\`
  - `kb.sqlite`：SQLite 数据库
  - `attachments\`：附件对象存储（按内容哈希）
  - `logs\`：日志文件（每次启动一个 log）

说明与故障排查：

- [开发指南.md](file:///d:/_TraeProject/debugging_library/docs/开发指南.md)
- [故障排查.md](file:///d:/_TraeProject/debugging_library/docs/故障排查.md)

## 快速开始（开发）

环境要求：Windows 10/11 + .NET SDK 8.x

```bash
dotnet build FieldKb.sln -c Release
dotnet test FieldKb.sln -c Release
dotnet run --project src/FieldKb.Client.Wpf/FieldKb.Client.Wpf.csproj
```

## 发布与打包

发布与打包指南见：[发布与打包.md](file:///d:/_TraeProject/debugging_library/docs/发布与打包.md)

- 免安装版：publish 到 `artifacts/publish/win-x64/`，解压即用
- 安装包：
  - Inno Setup（推荐）：支持自定义安装目录与可选桌面快捷方式
  - WiX MSI：支持升级更新与可选桌面快捷方式（自定义安装时可取消）

## 文档索引

- 文档入口：[docs/README.md](file:///d:/_TraeProject/debugging_library/docs/README.md)
- 数据模型与离线同步包规范：[数据模型与离线同步包规范.md](file:///d:/_TraeProject/debugging_library/docs/数据模型与离线同步包规范.md)
- 架构设计：[架构设计.md](file:///d:/_TraeProject/debugging_library/docs/架构设计.md)
- UI 操作流：[UI原型与操作流.md](file:///d:/_TraeProject/debugging_library/docs/UI原型与操作流.md)
