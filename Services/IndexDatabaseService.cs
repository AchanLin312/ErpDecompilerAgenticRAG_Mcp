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
    private readonly ILogger<IndexDatabaseService> _logger;
    private readonly ErpConfig _config;
    //按需反编译和完全反编译都会覆盖和更新相应的type和assemblymetadata，也会覆盖和更新对应的反编译源码
    public IndexDatabaseService(IDbContextFactory<ErpDbContext> contextFactory, ILogger<IndexDatabaseService> logger, ErpConfig config)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _config = config;
    }

    //查看dll是否已经反编译过，返回是否反编译过和已反编译类型数量
    // 参数：dllName（不含路径别名，如 "Erp.Contracts.BO.APLOC.dll")
    public async Task<(bool IsDecompiled, string Message)> IsAssemblyDecompiledAsync(string dllName)
    {
        if (string.IsNullOrWhiteSpace(dllName))
            return (false, "assemblyName is null or empty");
        using var context = await _contextFactory.CreateDbContextAsync();

        // 使用 AssemblyMetadata.AssemblyName 查询（因为 AssemblyMetadata.AssemblyName 存储的是 dllName）
        var assemblyMetadata = await context.AssembliesMetadata
            .FirstOrDefaultAsync(a => a.AssemblyName == dllName);
        if (assemblyMetadata == null)
            return (false, $"not decompiled");

        // 使用 AssemblyMetadata.AssemblyKey 查询 TypeRecord 数量
        var count = await context.Types
            .Where(t => t.AssemblyKey == assemblyMetadata.AssemblyKey)
            .CountAsync();

        if (assemblyMetadata.IsFullyDecompiled == "YES")
            return (true, $"full decompiled");

        return (count > 0, $"partially decompiled");
    }
    //将单个typeRecord记录储存在数据库中
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
        var targetTypes = await context.Types.Where(t => t.AssemblyKey.EndsWith(":" + assemblyName)).ToListAsync();
        context.Types.RemoveRange(targetTypes);

        var targetAssembly = await context.AssembliesMetadata.FirstOrDefaultAsync(a => a.AssemblyName == assemblyName);
        if (targetAssembly != null)
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
        foreach (var assembly in query)
        {
            if (assembly.IsFullyDecompiled == "YES")
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
        if (targetAssembly == null)
        {
            Message = $"assembly {assemblyName} is not decompiled yet, no type count";
            return (0, Message);
        }
        TypeCount = targetAssembly.TotalTypeCount;
        CachedTypeCount = targetAssembly.CachedTypeCount;
        if (targetAssembly.IsFullyDecompiled == "YES")
        {
            Message = $"assembly {assemblyName} is fully decompiled, decompiled type count: {CachedTypeCount}";
        }
        else
        {
            Message = $"assembly {assemblyName} is not fully decompiled, only partly decompiled, decompiled type count: {CachedTypeCount}";
        }
        return (TypeCount, Message);
    }

    //从数据库中获取dll的元数据
    public async Task<AssemblyMetadata?> GetAssemblyMetadataAsync(string dllName)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetAssembly = await context.AssembliesMetadata.FirstOrDefaultAsync(a => a.AssemblyName == dllName);
        return targetAssembly;
    }

    //检查dll文件是否需要更新或可以更新(如果该dll在数据库中没有metadata，说明没被反编译过，可以更新；如果metadata的last write time与文件的last write time不同，说明该dll被更新过)
    public async Task<bool> CheckAssemblyNeedsUpdateAsync(string AssemblyKey)
    {
        // AssemblyKey 格式: "alias:dllName.dll"
        var parts = AssemblyKey.Split(':');
        if (parts.Length != 2)
        {
            _logger.LogError("Invalid AssemblyKey format: {AssemblyKey}", AssemblyKey);
            return false;
        }
        var pathAlias = parts[0];
        var dllName = parts[1];

        // 使用 ErpConfig 获取正确的路径
        var basePath = _config.GetPathByAlias(pathAlias);
        if (string.IsNullOrEmpty(basePath))
        {
            _logger.LogError("Path alias '{PathAlias}' not found in configuration", pathAlias);
            return false;
        }

        var fullPath = Path.Combine(basePath, dllName);
        var fileInfo = new FileInfo(fullPath);

        var metadata = await GetAssemblyMetadataAsync(dllName);

        // 如果 metadata == null，说明没被反编译过，需要更新
        if (metadata == null)
        {
            return true;
        }

        // 如果文件已修改（FileLastWriteTime 不同），需要更新
        if (metadata.FileLastWriteTime != fileInfo.LastWriteTime)
        {
            return true;
        }

        // 如果未完全反编译，也需要更新（继续完成反编译）
        if (metadata.IsFullyDecompiled != "YES")
        {
            return true;
        }

        // 文件未修改且已完全反编译，不需要更新
        return false;
    }

    //获取指定dll反编译后的类型types数量
    // 参数：assemblyKey（格式："alias:dllName.dll")
    public async Task<int> GetTypeCountAsync(string assemblyKey)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        // TypeRecord.AssemblyName 存储的是 assemblyKey
        var count = await context.Types.Where(t => t.AssemblyKey == assemblyKey).CountAsync();
        return count;
    }

    //删除指定 DLL 的所有数据（类型和元数据）
    // 参数：assemblyKey（格式："alias:dllName.dll")
    public async Task<bool> RemoveAssemblyAsync(string assemblyKey)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        // TypeRecord.AssemblyName 存储的是 assemblyKey
        var targetTypes = await context.Types.Where(t => t.AssemblyKey == assemblyKey).ToListAsync();
        // AssemblyMetadata.AssemblyKey 存储的是 assemblyKey
        var targetAssembly = await context.AssembliesMetadata.Where(a => a.AssemblyKey == assemblyKey).ToListAsync();
        context.Types.RemoveRange(targetTypes);
        context.AssembliesMetadata.RemoveRange(targetAssembly);
        await context.SaveChangesAsync();
        return true;
    }

    //保存dll元数据
    public async Task<bool> SaveAssemblyMetadataAsync(string assemblyKey, string assemblyName, string assemblyPath, DateTime fileLastWriteTime, long fileSize, int typeCount, int cachedTypeCount, string IsFullyDecompiled, DateTime decompiledTime)
    {
        var sucess = false;
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetAssembly = new AssemblyMetadata
        {
            AssemblyKey = assemblyKey,
            AssemblyName = assemblyName,
            AssemblyPath = assemblyPath,
            FileLastWriteTime = fileLastWriteTime,
            FileSize = fileSize,
            TotalTypeCount = typeCount,
            CachedTypeCount = cachedTypeCount,
            IsFullyDecompiled = IsFullyDecompiled,
            DecompileTime = decompiledTime,
        };
        context.AssembliesMetadata.Add(targetAssembly);
        await context.SaveChangesAsync();
        sucess = true;
        return sucess;
    }

    //增加指定dll的AssemblyMetadata记录的CachedTypeCount，且若更新了CachedTypeCount字段等于totalTypeCount，就更新IsFullyDecompiled为"YES"
    public async Task<bool> AssemblyMetadataCachedTypeCountPlus1Async(string assemblyName)
    {
        var sucess = false;
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetAssembly = await context.AssembliesMetadata.Where(a => a.AssemblyName == assemblyName).FirstOrDefaultAsync();
        if (targetAssembly == null)
        {
            _logger.LogError("AssemblyMetadata not found for assemblyName: {assemblyName}", assemblyName);
            return false;
        }
        targetAssembly.CachedTypeCount++;
        if (targetAssembly.CachedTypeCount == targetAssembly.TotalTypeCount)
        {
            targetAssembly.IsFullyDecompiled = "YES";
        }
        await context.SaveChangesAsync();
        sucess = true;
        return sucess;
    }


    //根据传入的类型关键词、Dll名称，以及pathAlias到对应的目录下对模糊搜索Type的相关信息,先去数据库中搜索
    public async Task<(List<TypeSearchResult> Results, string Message)> SearchTypesAsync(string keyword, string dllNameHint, string pathAlias = "default")
    {
        var assemblyKey = $"{pathAlias}:{dllNameHint}";
        String Message = "";
        List<TypeSearchResult> results = new List<TypeSearchResult>();
        if (string.IsNullOrWhiteSpace(keyword) || string.IsNullOrWhiteSpace(dllNameHint))
        {
            _logger.LogError("keyword or dllNameHint is null or empty");
            Message = "keyword or dllNameHint is null or empty";
            return (results, Message);
        }
        //从数据库中搜索
        using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Types
            .Where(t => t.TypeName.Contains(keyword) && t.AssemblyKey == assemblyKey)
            .Select(t => new TypeSearchResult
            {
                TypeName = t.TypeName,
                AssemblyKey = t.AssemblyKey,
                TypeKind = t.TypeKind,
                Source = "cache",
                PathAlias = pathAlias,
            });
        results = await query.ToListAsync();
        Message = $"Found {results.Count} types in {dllNameHint}";
        return (results, Message);
    }

    // 获取已反编译的 DLL 详细信息列表
    public async Task<List<AssemblyMetadata>> GetDecompiledAssembliesDetailAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        // var assemblies = new List<DecompiledAssemblyInfo>();
        List<AssemblyMetadata> assemblies = await context.AssembliesMetadata.ToListAsync();
        return assemblies;
    }

    //获取指定type的typeCodeFilePath
    public async Task<string?> GetTypeCodeFilePathAsync(string typeName)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var targetType = await context.Types.Where(t => t.TypeName == typeName).FirstOrDefaultAsync();
        if (targetType == null)
        {
            _logger.LogError("TypeRecord not found for typeName: {typeName}", typeName);
            return null;
        }
        return targetType.CodeFilePath;
    }


    public async Task<List<string>> GetCachedTypeNamesAsync(string assemblyKey)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var typesRecords = await context.Types.Where(t => t.AssemblyKey == assemblyKey).Select(t => t.TypeName).ToListAsync();
        return typesRecords;
    }












}


