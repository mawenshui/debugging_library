# 贡献指南

## 目标

本项目以“离线可交付”为第一优先级，因此变更需要同时考虑：

- 可重复构建与回归测试
- 数据兼容与迁移
- 包格式可演进且可校验

## 提交前检查（建议）

- `dotnet build FieldKb.sln -c Release`
- `dotnet test FieldKb.sln -c Release`
- 修改文档：同步更新 `docs/` 与 README 导航

## 变更约束

- 改数据库 schema：必须新增迁移并更新 [数据库设计.md](file:///d:/_TraeProject/debugging_library/docs/数据库设计.md)
- 改包格式：必须更新 schemaVersion 与 [数据模型与离线同步包规范.md](file:///d:/_TraeProject/debugging_library/docs/数据模型与离线同步包规范.md)
- 改合并规则：必须新增测试覆盖冲突/覆盖/跳过三类路径

