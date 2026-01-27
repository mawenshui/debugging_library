## 目标与边界
- 场景：个人在外部现场记录问题；回公司后把个人数据导入公司“总问题库”；也能从总库导出到个人库；两端完全离线；支持模糊查询。
- 假设：两端以“文件包”方式交换数据（U盘/共享盘/邮件等），无任何在线服务。

## 技术栈（推荐）
- 客户端：.NET 8（C#）+ WPF（Windows 桌面，成熟、部署简单）
- UI架构：MVVM（CommunityToolkit.Mvvm）
- 本地存储：SQLite（单文件数据库，易备份、离线友好）
- 搜索：SQLite FTS5（全文索引 + BM25排序 + 前缀匹配）；可选 Lucene.NET（需要更强“模糊/拼写纠错/相似度”时启用）
- 导入导出包：ZIP + 清单 manifest（JSON/CBOR/Protobuf 任选其一；优先 JSON 便于排查）
- 安全（可选但强烈建议）：
  - 导出包加密：AES-GCM（口令派生密钥/公司密钥）
  - 完整性：SHA-256 校验；可选 Ed25519 签名
- 附件：文件系统对象存储（按内容哈希去重）+ SQLite 记录元数据
- 安装与更新：MSIX 或 WiX（优先 MSIX，企业环境也易管控）

## 核心方案（离线“同步/合并”模型）
- 两端都各自维护一份 SQLite 数据库：
  - 个人库：个人现场记录为主
  - 总库：汇总全员数据，作为“权威库”
- 数据交换不传整个库文件（避免锁、体积、权限问题），而是传“变更包（Change Package）”。
- 每条记录使用全局唯一 ID（GUID/ULID），并使用软删除（deleted=1）避免导入时找不到历史。

## 数据模型（建议最小可用集）
- Problem（问题主表）：title、symptom、rootCause、solution、environment（文本/结构化）、severity、status、createdAt/updatedAt、createdBy、source（个人/总库）
- Tag（标签）+ ProblemTag（多对多）
- Attachment（附件）：contentHash、fileName、mime、size、createdAt
- Device/Instance（实例标识）：instanceId、owner、type（个人/总库）
- SyncState（同步水位）：lastImportedSeq / lastImportedAt / lastImportedPackageId（按对端维度）
- ChangeLog（可选）：
  - 方案A（更简单）：以 updatedAt + deleted + instanceId 做增量导出
  - 方案B（更可靠）：追加式操作日志（insert/update/delete 事件），导入更可审计

## 导入导出流程（两端离线）
- 个人 → 总库（导入到公司）：
  - 个人端导出：选择“增量导出”（基于上次导入到总库的水位）或“全量导出”
  - 总库端导入：校验包、去重、应用变更、处理冲突、生成导入报告
- 总库 → 个人（下发数据）：
  - 总库端导出：可按条件导出（全量/按标签/按产品线/按时间窗）
  - 个人端导入：合并到本地，必要时标记“来自总库的只读字段/覆盖策略”

## 冲突与合并策略（避免复杂度爆炸）
- 默认策略：字段级“最后写入优先（LWW）” + 冲突记录可视化（保留两版内容供人工选择）。
- 规则建议：
  - 总库字段可配置为“更权威”（例如 rootCause/solution 最终以总库为准）
  - 个人私有字段（个人备注/本地状态）永不被总库覆盖

## 模糊查询方案
- 基础：FTS5 建索引（title/symptom/rootCause/solution/环境字段），支持：
  - 前缀匹配（现场记忆不完整时好用）
  - 相关性排序（BM25）
  - 高亮摘要
- 增强（可选）：
  - Lucene.NET：支持 FuzzyQuery（编辑距离）、同义词、拼写纠错；适合“错别字/拼音/近似词”更强需求

## 可维护性与扩展
- 插件式字段：environment 用 JSON 存 + 常用字段冗余列（便于筛选与统计）
- 数据版本迁移：SQLite migration（按 schemaVersion 管理）
- 审计：总库记录导入来源、包ID、操作者、时间戳

## 风险与对策
- 包导入失败/中断：导入事务化（单包原子导入），失败可回滚；记录导入日志。
- 数据泄露：导出包默认加密 + 最小化导出范围 + 权限控制（本地账户/口令）。
- 搜索效果不佳：先用 FTS5 达到 80% 体验，再按反馈引入 Lucene 增强。

## 落地实施步骤（下一步将按此执行）
1. 明确最小可用功能（录入/列表/详情/标签/附件/导入/导出/搜索）。
2. 定稿数据模型与同步包格式（manifest、记录结构、附件打包规则、版本号）。
3. 搭建 .NET 8 WPF + MVVM 项目骨架（分层：UI/Application/Domain/Infrastructure）。
4. 实现 SQLite 存储与迁移；实现 FTS 索引与搜索 API。
5. 实现导入导出：全量/增量、校验、冲突处理、导入报告。
6. 实现核心 UI：问题录入、搜索页、详情页、导入导出向导、冲突解决页。
7. 补齐测试（导入导出/冲突/搜索）与打包安装（MSIX/WiX）。

如果你确认该方案，我将开始按上述步骤在仓库中落地一个可运行的离线 Windows 桌面原型。