---
name: "epicor-mcp-tools"
description: "Guides usage of the ErpDecompilerAgenticRAG MCP tools for Epicor ERP 2nd-development. Invoke when user needs to analyze Epicor BO source code, search types/members, decompile methods, or understand Epicor API implementations."
---

# Epicor MCP Tools — 二次开发反编译工具使用指南

## 一、工具体系

工具分为两大类，**参数体系完全不同，绝对不可混用**：

### Epicor 系列（配置路径下的 Epicor DLL）

这些工具使用 `dllName` + `pathAlias` 定位文件，结果会写入缓存和数据库。

| 工具 | 用途 |
|------|------|
| `erp_list_paths` | 列出所有可用的路径别名 |
| `erp_list_dlls` | 列出指定路径下的所有 DLL |
| `erp_list_decompiled` | 列出已反编译的 DLL 及完整度状态 |
| `erp_list_dll_information` | 查看某个 DLL 的详细元信息 |
| `erp_decompile` | 全量反编译整个 DLL（耗时，大 DLL 可能超时） |
| `erp_search_types` | 搜索类型（cache + metadata 两级） |
| `erp_get_type_members` | 获取类型的成员列表 |
| `erp_get_type_code` | 获取单个类型的完整反编译源码 |
| `erp_decompile_method` | 提取单个方法的 C# 代码 |
| `erp_search_members` | 跨 DLL 搜索方法/属性/字段/事件 |

### Any 系列（任意本地 .NET DLL）

这些工具使用 `dllPath`（完整磁盘路径），**纯只读，不写数据库和缓存**。

| 工具 | 用途 |
|------|------|
| `decompile_any_list_types` | 列出 DLL 中所有类型（元数据读取，极快） |
| `decompile_any_type` | 反编译单个类型的完整源码 |
| `decompile_any_method` | 反编译单个方法的源码 |

## 二、Epicor 二开分析工作流

### 关键背景知识

Epicor 将代码分为四大类 DLL，按 `Erp.[类型].[模块]` 模式命名：

| 类型 | 示例 | 包含内容 | 分析价值 |
|------|------|---------|---------|
| **Services** | `Erp.Services.BO.Part.dll` | BO 方法的**实际业务逻辑**实现 | **高** — 了解方法怎么执行 |
| **Contracts** | `Erp.Contracts.BO.Part.dll` | 接口定义、数据契约（TableSet/Row） | **高** — 了解 API 有哪些方法和参数 |
| **Triggers** | `Erp.Triggers.APInvDtl.dll` | **C# 数据库级触发器**，一张表一个 DLL | **中** — 了解数据变更时的自动业务逻辑 |
| **Internal** | `Erp.Internal.JC.JobGenerator.dll` | **共享业务逻辑库**，被 BO 和 Trigger 调用 | **低** — 了解复杂计算和跨模块逻辑 |

### Trigger DLL 详解

Trigger DLL 是 Epicor 用 C# 实现的**数据库级触发器**，命名模式为 `Erp.Triggers.<表名>.dll`。其实现机制是**拦截器模式**（不是 SQL Trigger）：

```
BO 方法修改数据
  → ErpContext（数据上下文）拦截 Write/Create/Delete
    → 查找注册的 Trigger 类，传入新旧值
      → Trigger.Write(NewRow, OldRow) 执行业务逻辑
        → ErpContext 继续实际数据库操作
```

每个 Trigger DLL 包含以下类，由框架自动调用，无需开发者在 BO 中显式调用：

| 类 | 触发时机 | 基类 |
|---|---------|------|
| `CreateTrigger` | 插入记录时 | `CreateTrigger<ErpContext, TTable>` |
| `WriteTrigger` | 更新记录时 | `WriteTrigger<ErpContext, TTable>` |
| `DeleteTrigger` | 删除记录时 | `DeleteTrigger<ErpContext, TTable>` |

Trigger 的主要职责：
- **数据校验**：字段值合法性检查、UOM 转换验证
- **跨表联动**：修改当前表后自动更新关联表
- **数值处理**：四舍五入、精度控制
- **System Integration**：将数据变更入队，同步到外部系统
- **关联数据查询**：通过预编译 SQL 查询关联记录进行联动处理

简单型 Trigger（如 ABCCode）仅做四舍五入和 SI 入队，复杂型（如 APInvDtl）包含大量业务逻辑和关联表操作。

**分析建议**：当需要了解"修改某张表的字段后会发生什么连带影响"时，查找对应的 Trigger DLL 并反编译其 `WriteTrigger` 类。Trigger DLL 独立部署，可单独反编译分析。

### Internal DLL 详解

Internal DLL 是 Epicor 的**共享业务逻辑库**，命名模式为 `Erp.Internal.<模块>.<功能>.dll`。它们不直接暴露为 API，而是作为工具箱被 BO 方法和 Trigger 复用的基础组件。

特征：
- 继承 `ContextLibraryBase<ErpContext>`（通过构造注入数据库上下文）
- 包含大量**预编译 SQL 查询**（`Func<ErpContext, ...>`），用于高性能数据检索
- 定义嵌套的 **PartialRow 类**（继承 `TempRowBase`），用于多表联合查询的结果投影
- 跨模块引用其他 Internal DLL（如 `Erp.Internal.Methods`、`Erp.Internal.MR`）
- 一个 DLL 通常只含一个主类（与 Trigger 的"一张表一个 DLL"相似但粒度更粗）

典型大小对比：
- 简单 Internal：`AllocateFunctions`（3 个类型，仓库分配逻辑）
- 复杂 Internal：`JobGenerator`（9 个类型，192KB 源码，依赖 7+ 个 Internal DLL）

```
典型调用链：
BO 方法  → Services DLL
           → Internal DLL（共享逻辑、预编译 SQL）
           → Trigger DLL（自动拦截数据变更）
           → Internal DLL（Trigger 也调用共享逻辑）
```

**分析建议**：当 BO 方法中调用了某个未定义的方法，或想理解某个复杂计算（如 Job 生成、成本核算）时，查找对应的 Internal DLL 并反编译主类。

### 标准工作流

#### 场景 A：研究某个 BO 方法的业务逻辑

```
1. erp_search_members("GetByID", "Erp.Services.BO.Part.dll")
   → 找 Services DLL 中的方法声明

2. erp_decompile_method("Erp.BO.PartSvc.GetByID", "Erp.Services.BO.Part.dll")
   → 提取方法源码，看实现逻辑

3. 如果方法内部调用了其他方法：
   erp_decompile_method("Erp.BO.PartSvc.SomePrivateHelper", "Erp.Services.BO.Part.dll")
   → 递归追踪调用链
```

#### 场景 B：了解某个字段的 UOM/Business Rule

```
1. erp_search_types("PartPlant", "Erp.Services.BO.Part.dll")
   → 找相关类

2. erp_get_type_code("Erp.Tablesets.PartPlantRow", "Erp.Services.BO.Part.dll")
   → 看 Row 的字段定义和业务规则

3. 搜索关键赋值：
   erp_search_members("MinOrderQty", "Erp.Services.BO.Part.dll")
   → 找到所有赋值位置
```

#### 场景 C：了解 BO 有哪些 API 可用

```
1. erp_search_members("GetByID", "Erp.Contracts.BO.Part.dll")
   → 找 Contracts 中的方法签名，快速了解可用 API

2. erp_get_type_members("Erp.Contracts.BO.Part.IPartSvc", "Erp.Contracts.BO.Part.dll")
   → 查看接口的所有方法
```

### 分析第三方/自定义 DLL

```
1. decompile_any_list_types("C:\path\to\MyLib.dll")
   → 先了解 DLL 有哪些类型

2. decompile_any_type("C:\path\to\MyLib.dll", "MyLib.Program")
   → 反编译具体类型

3. decompile_any_method("C:\path\to\MyLib.dll", "MyLib.Program.Main")
   → 提取单个方法
```

#### 场景 D：了解数据变更的连带影响（Trigger 分析）

```
1. decompile_any_list_types("D:\Epicor\Server.Latest\Assemblies\Erp.Triggers.APInvDtl.dll")
   → 列出 Trigger DLL 的类型（通常 2-5 个）

2. decompile_any_type("D:\Epicor\...\Erp.Triggers.APInvDtl.dll", "Erp.Triggers.ApInvDtl.WriteTrigger")
   → 看 WriteTrigger 逻辑，了解修改 APInvDtl 表后会触发什么

3. 如需了解新建/删除时的逻辑：
   decompile_any_type("...", "Erp.Triggers.ApInvDtl.CreateTrigger")
   decompile_any_type("...", "Erp.Triggers.ApInvDtl.DeleteTrigger")
```
> 注：Trigger DLL 位于默认路径但不在缓存体系中，使用 `decompile_any_*` 工具直接分析。

## 三、性能最佳实践

1. **避免全量反编译大 DLL**：`erp_decompile` 对大型 DLL（如 `Erp.Services.BO.Part.dll`，421 个类型）可能耗时数十秒并触发客户端超时。优先使用 `erp_get_type_code` 按需反编译
2. **先用 metadata 搜索**：`erp_search_types` 同时查 cache 和 metadata，未缓存的结果标记 `source: "metadata"`，可以快速定位目标类型
3. **Contracts DLL 通常不需要反编译**：接口定义和数据结构不需要看源码，前者无方法体，后者结构可通过 `erp_get_type_members` 查看
4. **已完全反编译的 DLL 重复调用无开销**：`erp_decompile` 对已缓存的 DLL 秒级返回 `"already fully decompiled"`
5. **`decompile_any_*` 不持久化**：每次调用重新执行反编译，同一 DLL 的多次请求复用内存中的 LRU 缓存（上限 10 个编译实例）

## 四、工具选择速查

| 你想做什么 | 用哪个工具 |
|-----------|-----------|
| 查看某个方法怎么实现的 | `erp_decompile_method` (Epicor) / `decompile_any_method` (任意) |
| 搜索 DLL 中有哪些类 | `erp_search_types` (Epicor) / `decompile_any_list_types` (任意) |
| 看一个类的完整源码 | `erp_get_type_code` (Epicor) / `decompile_any_type` (任意) |
| 搜索某个方法/字段在哪里出现 | `erp_search_members` |
| 了解 BO 有哪些方法可用 | `erp_search_members` + Contracts DLL |
| 一次性反编译整个 DLL | `erp_decompile` |
| 分析任意本地 DLL（非 Epicor） | `decompile_any_list_types` → `decompile_any_type` → `decompile_any_method` |

## 五、常见错误

1. **`fetch failed`**：不是工具出错，是大 DLL 反编译耗时超过客户端超时，但反编译结果已保存。查 `erp_list_decompiled` 确认
2. **对任意 DLL 用了 Epicor 工具**：看到 `dllPath`（完整路径）就应用 `decompile_any_*`，看到 `dllName` + `pathAlias` 才用 `erp_*`
3. **方法全名格式错误**：`methodFullName` 必须是 `命名空间.类名.方法名`（至少一个点号），不是裸方法名
