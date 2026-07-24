---
name: "epicor-function-library"
description: "了解 Epicor Kinetic 的 Function Library（EFX）系统，包括 EfxLibraryDesignerSvc 服务、Ecf schema 下的数据库表结构、Function 增删改查/导入导出/发布管理的 API。当用户询问 Epicor Function、Function Library、EFX、epicor function 代码存储位置、如何通过代码操作 function library 时调用此 skill。"
---

# Epicor Function Library (EFX) 知识

## 核心服务

**EfxLibraryDesignerSvc** 是 Epicor Function Library 的管理服务（EFX = Epicor Function eXtensions）。

### DLL 位置

```
D:\Epicor\Server.Latest\Assemblies\
├── Ice.Contracts.Lib.EfxLibraryDesigner.dll    (100 类型, 服务契约)
├── Ice.Services.Lib.EfxLibraryDesigner.dll     (106 类型, 服务实现)
└── Ice.Lib.EfxLibraryDesigner.Shared.dll       (共享类型/DTO)
```

### 服务契约

`Ice.Contracts.EfxLibraryDesignerSvcContract` — 28 个方法

---

## 数据库表（Ecf Schema）

| 表 | 说明 |
|---|------|
| `Ecf.EfxLibrary` | Library 定义（151 条），含 LibraryID、Description、Mode、Owner、Published 等 |
| `Ecf.EfxFunction` | Function 定义（~1,490 条），**Body 字段存 C# 代码**（JSON 格式 `{"Code":"..."}` ），Kind 字段区分类型 |
| `Ecf.EfxFunctionSignature` | Function 参数签名，含 ArgumentName、DataType、Response/Input、Optional |
| `Ecf.EfxLibraryReference` | Library 引用的外部 DLL |
| `Ecf.EfxLibraryMapping` | Library 的公司映射（Company、Allowed） |
| `Ecf.EfxLibraryLock` | Library 编辑锁 |

### EfxFunction.Kind 含义

| Kind | 说明 |
|------|------|
| 0 | Widget Function |
| 1 | Library Widget |
| 2 | 标准 Function（最常见，用户日常编写的） |

### EfxFunction.Body 格式

```json
{"Code":"using(var scope = ...){ ... C# code ... }"}
```

C# 代码被包裹在 `{"Code":"..."}` 的 JSON 字符串中存储。

---

## SvcContract 28 个方法分类

### Library CRUD
- `GetLibrary(libraryId)` — 获取单个 Library（含所有 Function）
- `GetLibraries(libraryIds[])` — 批量获取 Library
- `GetDefaults()` — 获取默认模板
- `ApplyChanges(ref EfxLibraryTableset)` — **保存修改（增删改 Function）**
- `ApplyChangesWithDiagnostics(ref tableset, ref diagnostics)` — 保存 + 诊断

### Library 查询
- `GetLibraryList(LibrarySearchOptions)` — 搜索 Library 列表
- `GetLibraryList2(startsWith, kind, rollOutMode, status)` — 按条件过滤
- `GetLibraryInfo(LibraryInfoSearchOptions)` — 获取 Library 详细信息

### Function CRUD
- `GetFunctionList(FunctionSearchOptions)` — 搜索 Function 列表
- `GetFunctionList2(libraryID, kind, functionIDStartsWith)` — 按 Library 筛选
- `GetFunctionInfo(FunctionInfoSearchOptions)` — 获取 Function 详情
- `GetKineticFunction(libraryId, functionId)` — 获取 Function 完整 JSON（含 C# 代码）

### 发布管理
- `PromoteToProduction(libraryID)` — 发布到生产
- `DemoteFromProduction(libraryID)` — 从生产降级
- `RegenerateLibrary(libraryID)` — 重新编译 Library
- `RegenerateAllLibraries()` — 重新编译所有 Library

### 导入/导出
- `ExportLibrary(libraryID, ExportOptions)` — 导出为 byte[]
- `ImportLibrary(byte[], ImportOptions)` — 导入 Library
- `InstallLibrary(byte[], InstallOptions)` — 安装 Library
- `UninstallLibrary(libraryId)` — 卸载

### 锁定/权限
- `LockLibrary(libraryID)` — 锁定编辑
- `ReleaseLibrary(libraryID)` — 释放锁定
- `ChangeOwner(libraryId, userId)` — 变更所有者

### 验证/工具
- `IsValidLibraryId(proposedValue)` — 校验 Library ID
- `IsValidFunctionId(proposedValue)` — 校验 Function ID
- `IsValidFunctionParam(proposedValue)` — 校验参数名
- `DetectCircularReferences(libraryId, referencedLibraries[])` — 检测循环引用
- `DetermineInstallationOrder(libraryIds[])` — 确定安装顺序（拓扑排序）

---

## 重要区分

| | EfxLibraryDesignerSvc | BpMethodSvc |
|---|---|---|
| **管理对象** | Function Library 中的 Function（Epicor Function） | BPM Directive/Method |
| **数据库** | `Ecf.EfxFunction` / `Ecf.EfxLibrary` | `Ice.BpMethod` / `Ice.BpDirective` |
| **代码存储** | `Ecf.EfxFunction.Body`（JSON 包裹的 C# 代码） | `Ice.BpDirective.Body`（XML 格式的 Directive 定义） |
| **用户界面** | Function Library Designer | Function Maintenance |
| **典型使用** | 用户写 Function Library 供各处调用 | 用户写 BPM 触发器逻辑 |

**EfxLibraryDesignerSvc 才是用户日常开发 Epicor Function 的核心管理服务。**
