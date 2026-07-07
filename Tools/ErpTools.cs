using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ErpDecompilerAgenticRAG_Mcp.Models;
using ErpDecompilerAgenticRAG_Mcp.Services;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace ErpDecompilerAgenticRAG_Mcp.Tools;

[McpServerToolType]
//对于只获取类型名、成员名等元数据的操作， System.Reflection.Metadata 确实比 ICSharpCode.Decompiler.TypeSystem 更合适
public class ErpTools
{
    public ErpTools()
    {
    }

    [McpServerTool(Name = "erp_decompile")]
    [Description(@"反编译指定的 Epicor DLL 文件并将结果存储到 SQLite 索引数据库。

⚠️ 警告：反编译一个DLL可能花费数分钟时间，请谨慎使用！")]
    public async Task<string> DecompileDll()
    {
        return "success";
    }

    [McpServerTool(Name = "erp_list_dlls")]
    [Description(@"列出指定路径中所有可用的 DLL 文件")]
    public string ListAvailableDlls()
    {
        return "success";
    }

    [McpServerTool(Name = "erp_list_paths")]
    [Description("列出所有配置的搜索路径（一条Default路径，两条备用路径）及其别名。用于了解有哪些可用路径可以选择")]
    public string ListPaths()
    {
        return "success";
    }


    [McpServerTool(Name = "erp_search_types")]
    [Description(@"搜索Epicor程序集中的类型，如果能从数据库中找不到就用metadata找？或者反编译找？感觉还是直接用metadata是最方便的")]
    public async Task<string> SearchTypes()
    {
        return "success";
    }

    [McpServerTool(Name = "erp_list_decompiled")]
    [Description("列出所有已反编译的 DLL 及其详细信息（类型数量、路径别名，有没有被完全反编译）。用于了解哪些 DLL 已经可以搜索")]
    public async Task<string> ListDecompiledDllsInfo()
    {
        return "success";
    }

    [McpServerTool(Name = "erp_get_type_code")]
    [Description(@"获取指定类型的反编译源代码，先从数据库中查，没有的话再反编译查一次")]
    public async Task<string> GetTypeCode(string typeName)
    {
        return "success";
    }

    [McpServerTool(Name = "erp_get_type_members")]
    [Description(@"获取指定类或接口的结构目录（方法签名、属性、字段）,先从数据库中查，没有的话用Reflection.Metadata查，或者不从数据库中查直接用Reflection.Metadata。")]
    public async Task<string> GetTypeMembers(string typeName)
    {
        return "success";
    }

    [McpServerTool(Name = "erp_decompile_method")]
    [Description(@"反编译指定的 Epicor 方法，获取完整的 C# 代码逻辑。先从数据库中查，没有的话反编译查")]
    public async Task<string> DecompileMethod(string typeName, string methodName)
    {
        return "success";
    }

    [McpServerTool(Name = "erp_extract_db_references")]
    [Description(@"静态分析指定方法的反编译代码，提取其中引用的 Epicor 数据库表名。用于与数据库MCP进行关联分析，先从数据库中查，没有的话再反编译查一次")]
    public async Task<string> ExtractDbReferences(string typeName, string methodName)
    {
        return "success";
    }

    [McpServerTool(Name = "erp_search_members")]
    [Description(@"在程序集中按关键字搜索成员，先从数据库中查，没有的话再反编译查一次（也不一定，用Reflection.Metadata也可以）。")]
    public async Task<string> SearchMembers(string keyword)
    {
        return "success";
    }

   [McpServerTool(Name = "erp_remove_decompiled")]
    [Description(@"删除 SQLite 中指定 DLL 的反编译数据。注意：主要用于 DLL 更新后重新反编译。
")]

}
