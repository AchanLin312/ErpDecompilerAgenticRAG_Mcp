using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ErpDecompilerAgenticRAG_Mcp.Models;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks.Dataflow;
using Microsoft.EntityFrameworkCore;
using ErpDecompilerAgenticRAG_Mcp.Data;
using ErpDecompilerAgenticRAG_Mcp.Models;
using Microsoft.VisualBasic;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

public class IndexDatabaseService
{
    private readonly IDbContextFactory<ErpDbContext> _contextFactory;
    private readonly SqlConnection _connection;
    private readonly ILogger<IndexDatabaseService> _logger;
    private readonly string _dbPath;
    //按需反编译和完全反编译都会覆盖和更新相应的type和assemblymetadata，也会覆盖和更新对应的反编译源码
    public IndexDatabaseService(IDbContextFactory<ErpDbContext> contextFactory, ILogger<IndexDatabaseService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    //查看dll是否已经反编译过，返回是否反编译过和已反编译类型数量
    public async Task<(bool IsDecompiled, string Message)> IsAssemblyDecompiledAsync(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return (false, "assemblyName is null or empty");
        using var context = await _contextFactory.CreateDbContextAsync();
        var count = await context.Types
            .Where(t => t.AssemblyName == assemblyName)
            .CountAsync();
        var assemblyMetadata = await context.AssembliesMetadata
            .FirstOrDefaultAsync(a => a.AssemblyName == assemblyName);
        if(assemblyMetadata.IsFullyDecompiled == "YES")
            return (true, $"assembly {assemblyName} is fully decompiled, decompiled type count: {assemblyMetadata.CachedTypeCount}");

        return (count > 0, $"assembly {assemblyName} is not fully decompiled, only partly decompiled, decompiled type count: {assemblyMetadata.CachedTypeCount}");
    }
    //将dll的元数据储存在数据库中（仅元数据，方法体储存在文件系统中）
    public async Task<bool> SaveTypeAsync(TypeRecord record)
    {
        if (record == null || string.IsNullOrWhiteSpace(record.TypeName))
        {
            _logger.LogError("record is null or TypeName is null or empty");
            return false;
        }
        using var context = await _contextFactory.CreateDbContextAsync();
        context.Types.Add(record);
        await context.SaveChangesAsync();
        return true;
    }

    //批量保存元数据，反编译一个dll可能会一次性获得几百个TypeRecords
    public async Task<bool> SaveTypesAsync(IEnumerable<TypeRecord> records)
    {
        if (records == null || !records.Any())
        {
            _logger.LogError("records is null or empty");
            return false;
        }
        using var context = await _contextFactory.CreateDbContextAsync();
        context.Types.AddRangeAsync(records);
        await context.SaveChangesAsync();
        return true;
    }

    //删除指定Dll的所有类型的TypeRecords，同时更新assemblymetadata
    public async Task<bool> DeleteTypesAsync(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            _logger.LogError("assemblyName is null or empty");
            return false;
        }
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetTypes = await context.Types.Where(t => t.AssemblyName == assemblyName).ToListAsync();
        context.Types.RemoveRange(targetTypes);

        var targetAssembly = await context.AssembliesMetadata.FirstOrDefaultAsync(a => a.AssemblyName == assemblyName);
        if(targetAssembly != null)
        {
            targetAssembly.CachedTypeCount = 0;
            targetAssembly.IsFullyDecompiled = "NO";
        }
        await context.SaveChangesAsync();
        return true;
    }

    //获取已反编译的Dll列表(仅包含已反编译过的Dll)，包括告知这些dll是否已完全反编译过
    public async Task<List<Dictionary<string, string>>> GetDecompiledAssembliesAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var query = await context.AssembliesMetadata.ToListAsync();
        var result = new List<Dictionary<string, string>>();
        foreach(var assembly in query)
        {
            if(assembly.IsFullyDecompiled == "YES")
            {
                result.Add(new Dictionary<string, string>
                {
                    { "AssemblyName", $"{assembly.AssemblyName} (fully decompiled)" },
                });
            }
            else
            {
                result.Add(new Dictionary<string, string>
                {
                    { "AssemblyName", $"{assembly.AssemblyName} (partly decompiled)" }
                });
            }
        }
        return result;
    }

    //获取指定dll反编译后的类型数量  
    public async Task<(int TypeCount, string Message)> GetDecompiledTypeCountAsync(string assemblyName)
    {
        // TODO：添加判断传入的assemblyName是否存在
        string Message = "";
        int TypeCount = 0;
        int CachedTypeCount = 0;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            _logger.LogError("assemblyName is null or empty");
            Message = "assemblyName is null or empty";
        }
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetAssembly = await context.AssembliesMetadata
            .Where(a => a.AssemblyName == assemblyName)
            .FirstOrDefaultAsync();
        TypeCount = targetAssembly.TotalTypeCount;
        CachedTypeCount = targetAssembly.CachedTypeCount;
        if (targetAssembly == null)
        {
            Message = $"assembly {assemblyName} is not decompiled yet, no type count";
        }
        // var count = await context.AssembliesMetadata
        //     .Where(t => t.AssemblyName == assemblyName)
        //     .CountAsync();
        if(targetAssembly.IsFullyDecompiled != "NO")
        {
            Message = $"assembly {assemblyName} is not fully decompiled, only partly decompiled, decompiled type count: {CachedTypeCount}";
        }
        if(targetAssembly.IsFullyDecompiled == "YES")
        {
            Message = $"assembly {assemblyName} is fully decompiled, decompiled type count: {CachedTypeCount}";
        }
        return (TypeCount, Message);
    }

    //获取 DLL 元数据
    

    //根据传入的关键词、Dll名称、类型种类、限制数量搜索类型的模糊搜索,assemblyName和typeKind为可选参数
    //TODO：先搞清楚具体业务再写代码
    public async Task<(List<TypeSearchResult>,string Message)> SearchTypesAsync(
        string keyword,
        string? assemblyName = null,
        TypeKind typeKind = TypeKind.Unknown,
        int limit = 100)
    {
        String Message = "";
        List<TypeSearchResult> results = new List<TypeSearchResult>();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            _logger.LogError("keyword is null or empty");
            Message = "keyword is null or empty";
        }
        using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Types.Where(t => t.TypeName.Contains(keyword)).Select(t => new TypeSearchResult
        {
            TypeName = t.TypeName,
            AssemblyName = t.AssemblyName,
            TypeKind = t.TypeKind,
        });

        return (results, Message);

    }














}


