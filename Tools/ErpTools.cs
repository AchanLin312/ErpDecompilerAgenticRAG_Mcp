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
    private readonly DecompilerService _decompilerService;
    public ErpTools(DecompilerService decompilerService)
    {
        _decompilerService = decompilerService;
    }

    [McpServerTool(Name = "erp_decompile")]
    [Description(@"反编译指定的 Epicor DLL 文件并将结果存储到 SQLite 索引数据库。
     警告：反编译一个DLL可能花费数分钟时间，请谨慎使用！
     必须参数：dllName文件名（比如Erp.Service.BO.Part.dll）
     可选参数：pathAlias: 程序集文件夹的路径别名（默认default，可用erp_list_paths查看所有可用路径）
     ")]
    public async Task<string> DecompileDll([Description("DLL 文件名，例如 Erp.Contracts.BO.APLOC.dll")] string dllName, [Description("路径别名（默认default，可通过 erp_list_paths 查看所有可用别名）")] string pathAlias = "default")
    {
        var result = await _decompilerService.DecompileDllAsync(dllName, pathAlias);
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "erp_list_dlls")]
    [Description(@"列出指定路径中所有可用的 DLL 文件，用于了解有哪些DLL可以反编译或查询
可选参数：
- pathAlias: 路径别名（默认default）
示例调用：
{ ""pathAlias"": ""default"" }
")]
    public string ListAvailableDlls([Description("路径别名，默认为 'default'。可通过 erp_list_paths 查看所有可用别名")] string pathAlias = "default")
    {
        var dllsAndMessage = _decompilerService.ListAvailableDlls(pathAlias);
        var dlls = dllsAndMessage.Dlls;
        var message = dllsAndMessage.Message;
        Dictionary<string, string> allPaths = _decompilerService.GetAllPaths();
        var path = allPaths.GetValueOrDefault(pathAlias, $"path {pathAlias} is not configured"); //看看是否能通过PathAlias获取到路径，所以才用GetValueOrDefault
        return JsonSerializer.Serialize(new
        {
            Success = true,
            Message = $"Found {dlls.Count} available DLLs in {pathAlias}",
            pathAlias = pathAlias,
            Path = path,
            TotalCountOfDlls = dlls.Count,
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "erp_list_paths")]
    [Description("列出所有配置的搜索路径（一条Default路径，两条备用路径）及其别名。用于了解有哪些可用路径可以选择")]
    public string ListPaths()
    {
        var paths = _decompilerService.GetAllPaths();
        return JsonSerializer.Serialize(new
        {
            Success = true,
            Message = $"Found {paths.Count} configured paths",
            Paths = paths,
            DefaultPath = paths.GetValueOrDefault("default", "default path is not configured"),
            AvailableAliases = paths.Keys.ToList()
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }




    [McpServerTool(Name = "erp_list_decompiled")]
    [Description("列出所有已反编译的 DLL 及其详细信息（类型数量、路径别名，有没有被完全反编译）。用于了解哪些 DLL 已经可以搜索")]
    public async Task<string> ListDecompiledDllsInfo()
    {
        var assembliesmMetadata = await _decompilerService.ListDecompiledAssembliesAsync();
        var fullCompiledCount = 0;
        var partlyCompiledCount = 0;
        //添加统计信息
        foreach (var metadata in assembliesmMetadata)
        {
            if (metadata.IsFullyDecompiled == "YES")
            {
                fullCompiledCount++;
            }
            else
            {
                partlyCompiledCount++;
            }
        }
        var message = assembliesmMetadata.Count > 0
            ? $"Found {assembliesmMetadata.Count} decompiled assemblies"
            : "No assemblies have been decompiled yet. Use erp_decompile to decompile DLLs first.";

        return JsonSerializer.Serialize(new
        {
            Success = true,
            Message = message,
            FullCompiledDllCount = fullCompiledCount,
            PartlyCompiledDllCount = partlyCompiledCount,
            AssembliesMetaData = assembliesmMetadata
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }


    [McpServerTool(Name = "erp_list_dll_infomation")]
    [Description(@"列出指定 DLL的详细信息（类型数量、路径别名，有没有被完全反编译），如果这个dll没有被反编译，会返回这个dll没有被反编译的信息, 可选参数：
- pathAlias: 路径别名（默认default）
示例调用：
{ ""pathAlias"": ""default"" }")]
    public async Task<string> ListDllInfo(string dllName)
    {
        var metadata = await _decompilerService.GetAssemblyMetadata(dllName);
        if (metadata == null)
        {
            return JsonSerializer.Serialize(new
            {
                Success = false,
                Message = $"DLL {dllName} is not decompiled yet. Use erp_decompile to decompile DLLs first."
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }
        else
        {
            return JsonSerializer.Serialize(new
            {
                Success = true,
                Message = metadata.IsFullyDecompiled == "YES" ? $"DLL {dllName} is fully decompiled, available for search." : $"DLL {dllName} is partly decompiled.",
                AssemblyMetadata = metadata
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }
    }

    [McpServerTool(Name = "erp_search_types")]
    [Description(@"搜索指定的Epicor程序集中的某个类型（支持关键字模糊搜索，不强制使用类型全名），返回该类型（或关键字匹配的类型）的信息（非反编译源码），建议与erp_get_type_members，erp_get_type_code等工具结合使用，实现按需反编译。
必需参数：
- keyword: 搜索类型关键字，支持模糊搜索（如 PartPlant, SalesOrder）
- dllNameHint: 限定搜索的DLL文件名（如 Erp.Services.BO.Part.dll）
- pathAlias: 限定搜索的路径别名（可选，默认为 default）

示例调用：
{
  ""dllNameHint"": ""Erp.Contracts.BO.APLOC.dll"",
  ""keyword"": ""APLOC""
}

推荐用于探索DLL中的类型，配合erp_get_type_code实现按需反编译
    ")]
    public async Task<string> SearchTypes(
        [Description("搜索关键字（如 SalesOrder, Part, ConfirmDialog）")] string keyword,
        [Description("限定搜索的DLL文件名（可选，如 Erp.Services.BO.SalesOrder.dll）")] string dllName,
        [Description("限定搜索的路径别名（可选，如 default）")] string pathAlias = "default"
        )
    {
        //这个tool的原理是先去数据库中找，如果数据库中没有，就用System.Reflection.Metadata找，如果metadata中也没有，就返回一个空列表
        var (results, message) = await _decompilerService.SearchTypesAsync(keyword, dllName, pathAlias);
        return JsonSerializer.Serialize(new
        {
            Success = true,
            Message = message,
            TotalCount = results.Count,
            Results = results
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "erp_get_type_code")]
    [Description(@"获取指定类型的反编译源代码，建议与erp_get_type_members，erp_search_types等工具结合使用，实现按需反编译。
必需参数：
- typeName: 类型完整名称（如 Erp.BO.PartSvc） 
- dllName: DLL名称（如 Erp.Services.BO.Part.dll）
- pathAlias: 限定搜索的路径别名（可选，默认为 default）

示例调用：
{
  ""dllName"": ""Erp.Contracts.BO.Part.dll"",
  ""pathAlias"": ""default"",
  ""typeName"": ""Erp.Tablesets.PartTableset""
}
    
    ")]
    public async Task<string> GetTypeCode(
    [Description("类型完整名称（如 Erp.Services.BO.SalesOrderSvc）")] string typeName,
    [Description("DLL名称（如 Erp.Services.BO.SalesOrder.dll）")] string dllName,
    [Description("限定搜索的路径别名（可选，默认为 default）")] string pathAlias = "default"
    )
    {
        var result = await _decompilerService.GetOrDecompileTypeAsync(typeName, dllName, pathAlias);
        return JsonSerializer.Serialize(new
        {
            result
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "erp_get_type_members")]
    [Description(@"获取指定dll中某个指定类或接口的结构目录（方法签名、属性、字段）

    必要参数：
    - typeFullName: 类或接口的全限定名
    - dllName: DLL名称
    可选参数：
    - pathAlias: 路径别名（默认default）

    示例调用：
{
  ""dllName"": ""Erp.Contracts.BO.Part.dll"",
  ""pathAlias"": ""default"",
  ""typeFullName"": ""Erp.Tablesets.PartTableset""
}

    此工具不返回方法体代码，仅用于了解类的职责和可用API，建议与erp_get_type_code，erp_search_types等工具结合使用，实现按需反编译。
       ")]
    public async Task<string> GetTypeMembers(
            [Description("类或接口的全限定名（如 Ice.Services.BO.KineticErpSvc）")] string typeFullName,
            [Description("DLL名称（如 Erp.Services.BO.Part.dll）")] string dllName,
            [Description("路径别名（默认default）")] string pathAlias = "default"
    )
    {
        var result = await _decompilerService.GetTypeMembersAsync(typeFullName, dllName, pathAlias);
        return JsonSerializer.Serialize(new
        {
            result
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    [McpServerTool(Name = "erp_decompile_method")]
    [Description(@"读取指定的Dll中的单个Epicor方法的反编译源码，获取C# 代码逻辑但不包括被调用方法的实现（只能获取已缓存的反编译的类型中的方法的源码，如果类型之前未反编译过，也就没有缓存，会报错！），支持自动递归追踪其调用的私有/静态方法（但是只能递归追踪同一个类型文件内的方法，不能跨类型文件调用）。

必需参数：
- methodFullName: 方法的全限定名（如 Erp.BO.PartSvc.GetPart）
- dllName: DLL名称（如 Erp.Services.BO.Part.dll）
- pathAlias: 限定搜索的路径别名（可选，默认为 default）

提示：
如果需要了解方法内部调用的其他方法，可以单独调用此工具获取。

    ")]
    //该tool会优先去数据库和缓存中找这个method的类型的源代码，但即使类型已缓存，也用 CSharpDecompiler 反编译单个方法
    public async Task<string> DecompileMethod(
    [Description("方法的全限定名（如 Ice.Services.BO.KineticErpSvc.GetConfirmDialogUserOptions，或Erp.BO.PartSvc.GetPart）")] string methodFullName,
    [Description("DLL名称（如 Erp.Services.BO.Part.dll）")] string dllName,
    [Description("路径别名（默认default）")] string pathAlias = "default"
    )
    {
        var result = await _decompilerService.DecompileMethodAsync(methodFullName, dllName, pathAlias);
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    // [McpServerTool(Name = "erp_extract_db_references")]
    // [Description(@"静态分析指定方法的反编译代码，提取其中引用的 Epicor 数据库表名。用于与数据库MCP进行关联分析，先从数据库中查，没有的话再反编译查一次")]
    // public async Task<string> ExtractDbReferences(string typeName, string methodName)
    // {
    //     return "success";
    // }

    [McpServerTool(Name = "erp_search_members")]
    [Description(@"在指定的的dll程序集中按关键字搜索成员。
    
    必需参数：
- keyword: 搜索关键字（如 GetPart, Update, MinOrderQty）
- assemblyName: DLL名称（如 Erp.Services.BO.Part.dll）
- pathAlias: 限定搜索的路径别名（可选，默认为 default）
    
    ")]
    //这个tool先去这个dll的缓存中找，找不到的话就用DLL 元数据（Reflection.Metadata）找
    public async Task<string> SearchMembers([Description("搜索关键字（如 Authenticate, GetConfirmDialog）")]string keyword, [Description("DLL名称（如 Ice.Services.BO.KineticErp.dll）")]string assemblyName, [Description("路径别名（默认default）")] string pathAlias = "default")
    {
        var result = await _decompilerService.SearchMemberAsync(keyword, assemblyName, pathAlias);
        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
    }

    //    [McpServerTool(Name = "erp_remove_decompiled")]
    //     [Description(@"删除 SQLite 中指定 DLL 的反编译数据。注意：主要用于 DLL 更新后重新反编译。
    // ")]

}
