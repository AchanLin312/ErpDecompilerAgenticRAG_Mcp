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

    public IndexDatabaseService(IDbContextFactory<ErpDbContext> contextFactory, ILogger<IndexDatabaseService> logger) //直接依赖ErpConfig和ILogger，因为这两个服务已经注册为单例服务了可以直接用
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    //查看dll是否已经反编译过
    public async Task<(bool IsDecompiled, string Message)> IsAssemblyDecompiledAsync(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return (false, "assemblyName is null or empty");

        using var context = await _contextFactory.CreateDbContextAsync();
        var count = await context.Types
            .Where(t => t.AssemblyName == assemblyName)
            .CountAsync();

        return (count > 0, "");
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

    //删除指定Dll的所有类型的TypeRecords
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
        await context.SaveChangesAsync();
        return true;
    }

    //获取已反编译的Dll列表(仅包含已反编译过的Dll)
    public async Task<List<string>> GetDecompiledAssembliesAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.AssembliesMetadata
            .Where(a => a.IsFullyDecompiled == "YES")
            .Select(a => a.AssemblyName)
            .Distinct()
            .ToListAsync();
    }

    //获取指定dll反编译后的类型数量  
    public async Task<(int TypeCount, string Message)> GetDecompiledTypeCountAsync(string assemblyName)
    {
        // TODO：添加判断传入的assemblyName是否存在
        string Message = "";
        int TypeCount = 0;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            _logger.LogError("assemblyName is null or empty");
            Message = "assemblyName is null or empty";
        }
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetAssembly = await context.AssembliesMetadata
            .Where(a => a.AssemblyName == assemblyName)
            .FirstOrDefaultAsync();
        TypeCount = targetAssembly.TypeCount;
        
        if (targetAssembly == null)
        {
            Message = $"assembly {assemblyName} is not decompiled yet, no type count";
        }
        var count = await context.Types
            .Where(t => t.AssemblyName == assemblyName)
            .CountAsync();
        if(targetAssembly.IsFullyDecompiled != "NO")
        {
            Message = $"assembly {assemblyName} is not fully decompiled, only partly decompiled, decompiled type count: {count}";
        }
        if(targetAssembly.IsFullyDecompiled == "YES")
        {
            Message = $"assembly {assemblyName} is fully decompiled, decompiled type count: {count}";
        }
        return (TypeCount, Message);
    }

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


