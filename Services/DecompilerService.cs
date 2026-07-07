using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.Extensions.Logging;
using ErpDecompilerAgenticRAG_Mcp.Models;
using System.Collections.Concurrent;
using System.Reflection.Metadata;
using Microsoft.IdentityModel.Tokens;
using ErpDecompilerAgenticRAG_Mcp.Services;
using ErpDecompilerAgenticRAG_Mcp.Utilities;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

public class DecompilerService
{
    private readonly ErpConfig _config;
    private readonly ILogger<DecompilerService> _logger;
    private readonly IndexDatabaseService _db;
    public DecompilerService(ErpConfig config, ILogger<DecompilerService> logger, IndexDatabaseService db)
    {
        _config = config;
        _logger = logger;
        _db = db;
    }

    //反编译指定dll文件
    public async Task<DecompileResult> DecompileDllAsync(string dllName, string pathAlias = "default")
    {
        var result = new DecompileResult();
        if(string.IsNullOrWhiteSpace(dllName) || string.IsNullOrWhiteSpace(pathAlias))
        {
            result.Success = false;
            result.Message = "dllName or pathAlias is null or empty";
            _logger.LogError(result.Message);
        }
        if(ErpHelper.ContainsInvalidPathCharacters(dllName) || ErpHelper.ContainsInvalidPathCharacters(pathAlias))
        {
            result.Success = false;
            result.Message = $"dllName {dllName} or pathAlias {pathAlias} contains invalid path characters";
            _logger.LogError(result.Message);
        }
        var fullPath = Path.Combine(_config.GetPathByAlias(pathAlias), dllName);
        if(!Directory.Exists(fullPath))
        {
            result.Success = false;
            result.Message = $"dllName {dllName} does not exist";
            _logger.LogError(result.Message);
        }
        if(!dllName.EndsWith(".dll"))
        {
            result.Success = false;
            result.Message = $"dllName {dllName} must end with .dll";
            _logger.LogError(result.Message);
        }

        try
        {
            var assemblyKey = $"{pathAlias}:{dllName}";
            var fileInfo = new FileInfo(fullPath);

            //检查目标dll是否已经反编译且未更新
            var (isDecompiled, message) = await _db.IsAssemblyDecompiledAsync(dllName);


        }
        catch (System.Exception)
        {
            
            throw;
        }

        return result;
    }

    public (List<string> Dlls, string Message) ListAvailableDlls(string alias = "default")
    {
        List<string> resultLst;
        string message;
        var searchPath = _config.GetPathByAlias(alias);
        if(string.IsNullOrWhiteSpace(searchPath))
        {
            message = "searchPath is null or empty";
        }
        if(!Directory.Exists(searchPath))
        {
            message = $"searchPath {searchPath} does not exist";
        }
        resultLst = Directory.GetFiles(searchPath, "*.dll").ToList();
        message = $"searchPath {searchPath} has {resultLst.Count} dll files";
        if(resultLst.Count == 0)
        {
            message = $"searchPath {searchPath} has no dll files";
        }
        return (resultLst, message);
    }
}
