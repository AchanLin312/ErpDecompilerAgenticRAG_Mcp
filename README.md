# ErpDecompilerAgenticRAG MCP

一个基于 MCP（Model Context Protocol）的 Epicor ERP 反编译与智能检索服务。它将 ICSharpCode.Decompiler 的反编译能力封装为 MCP 工具，让 AI 助手能够按需反编译 Epicor DLL、搜索类型与成员、读取源码，实现 Agentic RAG（检索增强生成）工作流。

## 技术栈

- **.NET 9.0** / ASP.NET Core
- **ICSharpCode.Decompiler** — C# 反编译引擎
- **System.Reflection.Metadata** — 轻量级 DLL 元数据读取
- **Entity Framework Core + SQLite** — 反编译索引数据库
- **Model ContextProtocol** — MCP 协议（支持 stdio 和 HTTP 两种传输模式）

## 快速开始

### 1. 配置

编辑 `appsettings.json`，将路径指向你的 Epicor 服务器程序集目录：

```json
{
  "ErpConfig": {
    "DefaultPath": "C:\\Epicor\\Server\\Assemblies",
    "AlternativePaths": {
      "GPE_Assemblies": "C:\\Epicor\\Server\\AssembliesGPE",
      "Bin_Assemblies": "C:\\Epicor\\Server\\Bin"
    },
    "MaxCallChainDepth": 6,
    "DatabasePath": "erp_index.db"
  }
}
```

- `DefaultPath` — Epicor 程序集所在目录（必需）
- `AlternativePaths` — 额外的程序集路径别名（可选）
- `MaxCallChainDepth` — `erp_decompile_method` 递归追踪调用链的最大深度
- `DatabasePath` — SQLite 索引数据库路径，留空则使用运行目录下的 `erp_index.db`

### 2. 构建与发布

```bash
dotnet publish -c Release -o publish
```

### 3. 启动

**stdio 模式**（用于 Claude Desktop / Trae 等 MCP 客户端）：

```bash
./ErpDecompilerAgenticRAG_Mcp.exe
```

**HTTP 模式**（用于远程调用或调试）：

```bash
./ErpDecompilerAgenticRAG_Mcp.exe --mode http --port 5000 --endpoint /erp_decompiler_mcp
```

### 4. 在 MCP 客户端中配置

以 Trae IDE 为例，在 MCP 配置中添加：

```json
{
  "mcpServers": {
    "erp-decompiler": {
      "command": "C:\\path\\to\\ErpDecompilerAgenticRAG_Mcp.exe",
      "args": []
    }
  }
}
```

## MCP 工具一览

| 工具 | 作用 |
|------|------|
| `erp_list_paths` | 列出所有已配置的程序集路径别名 |
| `erp_list_dlls` | 列出指定路径下所有可用的 DLL 文件 |
| `erp_decompile` | 反编译整个 DLL 并将结果存入索引数据库（耗时操作） |
| `erp_list_decompiled` | 列出所有已反编译的 DLL 及其反编译状态 |
| `erp_list_dll_infomation` | 查询指定 DLL 的反编译详细信息 |
| `erp_search_types` | 在指定 DLL 中按关键字模糊搜索类型（同时查缓存和元数据） |
| `erp_get_type_members` | 获取指定类型的方法、属性、字段等成员列表 |
| `erp_get_type_code` | 获取指定类型的反编译源码（支持按需反编译，自动缓存） |
| `erp_decompile_method` | 反编译单个方法并提取源码（支持递归追踪同文件内的调用链） |
| `erp_search_members` | 在指定 DLL 中按关键字搜索成员（方法/属性/字段/事件） |

### 典型工作流

1. `erp_search_types` — 搜索目标 DLL 中的类型
2. `erp_get_type_members` — 查看类型的成员结构
3. `erp_get_type_code` — 获取类型完整源码
4. `erp_decompile_method` — 深入查看单个方法的实现逻辑

> 上述步骤 2-4 均支持按需反编译：即使目标 DLL 未被完全反编译，工具会自动反编译所需类型并缓存，无需事先 `erp_decompile` 整个 DLL。

## 免责声明

本项目仅供学习和研究用途。反编译功能基于 ICSharpCode.Decompiler 开源库实现，使用者需确保：

- 遵守所在地区的法律法规
- 遵守 Epicor ERP 的软件许可协议
- 不将反编译结果用于商业用途或知识产权侵犯

项目作者不对任何因使用本工具而产生的法律后果承担责任。
