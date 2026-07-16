using ErpDecompilerAgenticRAG_Mcp.Models;
using ErpDecompilerAgenticRAG_Mcp.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

public class CacheManagerService
{
    //原版本使用哈希来作为目录名的一部分，现是为了区分不同版本的dll，方便回滚，但是如果只使用dll文件的修改时间进行新旧版本对比的话应该也是可以的,，所以现在去掉哈希
    private readonly string _cacheDirectory;
    private readonly ILogger<CacheManagerService> _logger;
    // SemaphoreSlim确保线程安全（相当于是一个互斥锁，确保同一时刻只有一个线程可以访问文件）
    private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

    public CacheManagerService(string cacheDirectory, ILogger<CacheManagerService> logger)
    {
        _cacheDirectory = cacheDirectory;
        _logger = logger;

        // 初始化缓存目录
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
                _logger.LogInformation("Created cache directory: {CacheDirectory}", _cacheDirectory);
            }
            else
            {
                _logger.LogInformation("Cache directory already exists: {CacheDirectory}", _cacheDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create cache directory: {Error}", ex.Message);
            throw;
        }
    }

    //接受dll文件路径、assemblyKey、反编译得到的类型列表，保存到缓存目录
    public async Task SaveToCacheAsync(string dllPath, string assemblyKey, List<DecompiledType> types)
    {
        var cacheFoler = Path.Combine(_cacheDirectory, $"{ErpHelper.SanitizeFileName(assemblyKey)}"); //获取该dll缓存目录
        _logger.LogInformation("Saving to cache: {AssemblyKey}", assemblyKey);
        try
        {
            if (!Directory.Exists(cacheFoler))
            {
                Directory.CreateDirectory(cacheFoler);
                _logger.LogInformation("Created cache directory: {CacheDirectory}", cacheFoler);
            }
            //将每个type的反编译源码保存到.cs文件中
            var saveCount = 0;
            foreach (var type in types)
            {
                try
                {
                    //创建反编译源码文件目录
                    var codeFile = ErpHelper.GetCodeFilePath(cacheFoler, type.TypeName);
                    var directoryPath = Path.GetDirectoryName(codeFile);
                    if (!Directory.Exists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }
                    await File.WriteAllTextAsync(codeFile, type.Code, Encoding.UTF8);
                    saveCount++;
                }
                catch (IOException ex)
                {
                    _logger.LogError($"Error occurred while saving {type.TypeName} to cache, Error: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError($"Permission denied while saving {type.TypeName} to cache, Error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error occurred while saving {type.TypeName} to cache, Error: {ex.Message}");
                }
            }

        }
        catch (Exception ex)
        {
            _logger.LogError($"Error occurred while saving {assemblyKey} to cache, Error: {ex.Message}");
            throw;
        }
    }

    //从缓存文件夹中加载这个dll的所有类型，返回一个列表，如果缓存目录不存在，返回null
    public List<DecompiledType>? LoadAllTypes(string assemblyKey)
    {
        var types = new List<DecompiledType>();
        var cacheFoler = Path.Combine(_cacheDirectory, $"{ErpHelper.SanitizeFileName(assemblyKey)}"); //获取该dll缓存目录
        if (!Directory.Exists(cacheFoler))
        {
            return null;
        }
        //递归扫描所有.cs文件
        var csFiles = Directory.GetFiles(cacheFoler, "*.cs", SearchOption.AllDirectories);

        foreach (var csFile in csFiles)
        {
            //根据.cs文件路径获取类型名
            var relativePath = Path.GetRelativePath(cacheFoler, csFile);
            var typeName = relativePath.Replace(".cs", "").Replace(Path.DirectorySeparatorChar, '.');
            var code = File.ReadAllText(csFile, Encoding.UTF8);

            types.Add(new DecompiledType
            {
                TypeName = typeName,
                Code = code
            });
        }
        return types;
    }

    //从缓存中加载type的反编译源码
    public async Task<string?> LoadTypeCodeAsync(string dllPath, string assemblyKey, string typeName)
    {
        await _fileLock.WaitAsync();
        try
        {
            var cacheFolder = Path.Combine(_cacheDirectory, $"{ErpHelper.SanitizeFileName(assemblyKey)}");
            if (!Directory.Exists(cacheFolder))
            {
                return null;
            }
            var codeFile = ErpHelper.GetCodeFilePath(cacheFolder, typeName);
            if (!File.Exists(codeFile))
            {
                return null;
            }

            var code = await File.ReadAllTextAsync(codeFile, Encoding.UTF8);
            return code;
        }
        finally
        {
            _fileLock.Release();
        }
    }


}