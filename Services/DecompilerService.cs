using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.Extensions.Logging;
using ErpDecompilerAgenticRAG_Mcp.Models;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Collections.Immutable;
using Microsoft.IdentityModel.Tokens;
using ErpDecompilerAgenticRAG_Mcp.Services;
using ErpDecompilerAgenticRAG_Mcp.Utilities;
using System.Reflection.PortableExecutable;
using ModelContextProtocol.Protocol;
using ErpDecompilerAgenticRAG_Mcp.Models;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

public class DecompilerService
{
    private readonly ErpConfig _config;
    private readonly ILogger<DecompilerService> _logger;
    private readonly IndexDatabaseService _db;
    // CSharpDecompiler缓存，避免重复加载
    private readonly ConcurrentDictionary<string, CSharpDecompiler> _decompilerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _decompilerCacheOrder = new();
    private const int MaxDecompilerCacheSize = 10;
    private readonly CacheManagerService _cache;
    public DecompilerService(ErpConfig config, ILogger<DecompilerService> logger, IndexDatabaseService db, CacheManagerService cache)
    {
        _config = config;
        _logger = logger;
        _db = db;
        _cache = cache;
    }
    //获取CSharpDecompiler实例，优先从缓存中获取，若缓存中不存在则创建并缓存
    private CSharpDecompiler GetOrCreateDecompiler(string dllPath)
    {
        //优先从缓存中获取Decompiler
        if (_decompilerCache.TryGetValue(dllPath, out var CachedCSharpDecompiler))
        {
            return CachedCSharpDecompiler;
        }
        //如果缓存满了，出队老条目
        while (_decompilerCacheOrder.Count >= MaxDecompilerCacheSize)
        {
            if (_decompilerCacheOrder.TryDequeue(out var oldestPath))
            {
                _decompilerCache.TryRemove(oldestPath, out var _oldDecompiler);
                _logger.LogInformation("LRU evicting CSharpDecompiler for: {DllPath}", oldestPath);
            }
        }
        //如果缓存中不存在且缓存未满，创建新的CSharpDecompiler实例并入队缓存,使用GetOrAdd是为线程安全
        var decompiler = _decompilerCache.GetOrAdd(dllPath, path =>
        {
            _logger.LogInformation("Creating CSharpDecompiler for: {DllPath} (first time, may take a while for large DLLs)", path);
            var dc = new CSharpDecompiler(path, new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false
            });
            _logger.LogInformation("CSharpDecompiler created and cached for: {DllPath}", path);
            return dc;
        });

        _decompilerCacheOrder.Enqueue(dllPath);
        return decompiler;
    }

    //反编译指定dll文件
    public async Task<DecompileResult> DecompileDllAsync(string dllName, string pathAlias = "default")
    {
        var result = new DecompileResult();

        // 参数验证
        if (string.IsNullOrWhiteSpace(dllName) || string.IsNullOrWhiteSpace(pathAlias))
        {
            result.Success = false;
            result.Message = "dllName or pathAlias is null or empty";
            _logger.LogError(result.Message);
            return result;
        }

        if (ErpHelper.ContainsInvalidPathCharacters(dllName) || ErpHelper.ContainsInvalidPathCharacters(pathAlias))
        {
            result.Success = false;
            result.Message = $"dllName {dllName} or pathAlias {pathAlias} contains invalid path characters";
            _logger.LogError(result.Message);
            return result;
        }

        if (!dllName.EndsWith(".dll"))
        {
            result.Success = false;
            result.Message = $"dllName {dllName} must end with .dll";
            _logger.LogError(result.Message);
            return result;
        }

        var basePath = _config.GetPathByAlias(pathAlias);
        if (string.IsNullOrEmpty(basePath))
        {
            result.Success = false;
            result.Message = $"Path alias '{pathAlias}' not found in configuration";
            _logger.LogError(result.Message);
            return result;
        }

        var fullPath = Path.Combine(basePath, dllName);

        if (!File.Exists(fullPath))
        {
            result.Success = false;
            result.Message = $"DLL file not found: {fullPath}";
            _logger.LogError(result.Message);
            return result;
        }

        try
        {
            var assemblyKey = $"{pathAlias}:{dllName}";
            var fileInfo = new FileInfo(fullPath);

            //检查目标dll是否已经被完全反编译，是否需要更新
            var (isDecompiled, message) = await _db.IsAssemblyDecompiledAsync(dllName);
            if ((isDecompiled == false && message == "not decompiled") || (isDecompiled == true && message == "partially decompiled"))
            {
                //检查dll文件是需要更新（若dll文件已修改或者不存在对应的dll元数据记录说明需要更新）
                var needUpdate = await _db.CheckAssemblyNeedsUpdateAsync(assemblyKey);
                if (needUpdate)
                {
                    //文件需要更新，删除一切旧数据
                    await _db.RemoveAssemblyAsync(assemblyKey);
                    //执行反编译
                    var decompiler = GetOrCreateDecompiler(fullPath);

                    //获取dll中所有类型
                    var types = decompiler.TypeSystem.MainModule.TypeDefinitions;
                    var decompiledTypes = new List<DecompiledType>();
                    var typeNames = new List<string>();

                    foreach (var type in types)
                    {
                        //跳过编译器生成的类型
                        if (type.IsCompilerGenerated() || type.IsAnonymousType())
                            continue;

                        if (type.Name.Contains("<") && type.Name.Contains(">"))
                            continue;

                        try
                        {
                            var typeCode = decompiler.DecompileTypeAsString(type.FullTypeName);
                            if (typeCode == null)
                            {
                                _logger.LogWarning("Decompiler returned null for type: {TypeName}. Skipping this type.", type.FullName);
                                continue;
                            }
                            var kind = ErpHelper.ConvertToTypeKind(type.Kind);

                            decompiledTypes.Add(new DecompiledType
                            {
                                TypeName = type.FullName,
                                Code = typeCode,
                                TypeKind = kind.ToString()
                            });
                            typeNames.Add(type.FullName);
                        }
                        catch (System.Exception ex)
                        {
                            result.Success = false;
                            result.Message = $"Failed to decompile type: {type.FullName}. Error: {ex.Message}";
                            _logger.LogWarning("Failed to decompile type: {TypeName}. Error: {Error}", type.FullName, ex.Message);
                            throw;
                        }
                    }

                    //保存反编译得到的代码到文件系统中
                    await _cache.SaveToCacheAsync(fullPath, assemblyKey, decompiledTypes);

                    //准备typerecor并储存到数据库中，注意typerecord中的CodeFilePath是用LoadAllTypes方法获取的，在这里使用LoadAllTypes既可以获取刚才所有类型的代码的文件路径，也可以顺便验证刚才所有类型的代码是否都保存到了缓存目录
                    List<DecompiledType>? cachedTypes;
                    try
                    {
                        cachedTypes = _cache.LoadAllTypes(assemblyKey);
                    }
                    catch (System.Exception)
                    {
                        result.Success = false;
                        result.Message = $"Failed to load all types from cache for {assemblyKey}";
                        _logger.LogError(result.Message);
                        throw;
                    }

                    var TypeRecords = new List<TypeRecord>();
                    var dllPath = assemblyKey.Replace(":", "/");
                    foreach (var type in cachedTypes)
                    {
                        TypeRecords.Add(new TypeRecord
                        {
                            TypeName = type.TypeName,
                            AssemblyKey = assemblyKey,
                            AssemblyPath = fullPath,
                            TypeKind = Enum.Parse<Models.TypeKind>(type.TypeKind),
                            CodeFilePath = type.CodeFilePath,
                            DecompileTime = DateTime.Now
                        });
                    }
                    await _db.SaveTypesAsync(TypeRecords);

                    //保存dll元数据并标记isFullyDecompiled为YES且CachedTypeCount为TotalTypeCount,(不能在这里用System.Reflection.Metadata读元数据，因为这个api获取到的元数据其实还是稍微不同于ICSharpCode.Decompiler.TypeSystem中的TypeDefinition)
                    await _db.SaveAssemblyMetadataAsync(assemblyKey, dllName, fullPath, fileInfo.LastWriteTime, fileInfo.Length, cachedTypes.Count, TypeRecords.Count, "YES", DateTime.Now);

                    result.Success = true;
                    result.Message = $"Assembly {dllName} (path: {pathAlias}) decompiled successfully";
                    result.AssemblyName = dllName;
                    result.TotalTypes = cachedTypes.Count;
                    _logger.LogInformation(result.Message);
                }
            }
            else
            {
                //目标dll已经被完全反编译，无需更新
                var existingCount = await _db.GetTypeCountAsync(assemblyKey);
                result.Success = true;
                result.Message = $"Assembly {dllName} (path: {pathAlias}) already fully decompiled)";
                result.AssemblyName = dllName;
                result.TotalTypes = existingCount;
            }


        }
        catch (System.Exception ex)
        {
            result.Success = false;
            result.Message = $"Decompile failed: {ex.Message}";
            _logger.LogError(ex, "Decompile failed for {DllName}: {Error}", dllName, ex.Message);
        }

        return result;
    }
    //获取所有配置的路径
    public Dictionary<string, string> GetAllPaths()
    {
        return _config.GetAllPaths();
    }
    //列出指定路径下的所有dll文件
    public (List<string> Dlls, string Message) ListAvailableDlls(string alias = "default")
    {
        List<string> resultLst;
        string message;
        var searchPath = _config.GetPathByAlias(alias);
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return (new List<string>(), $"Path alias '{alias}' not found in configuration");
        }
        if (!Directory.Exists(searchPath))
        {
            return (new List<string>(), $"searchPath {searchPath} does not exist");
        }
        resultLst = Directory.GetFiles(searchPath, "*.dll").ToList();
        message = $"searchPath {searchPath} has {resultLst.Count} dll files";
        if (resultLst.Count == 0)
        {
            message = $"searchPath {searchPath} has no dll files";
        }
        return (resultLst, message);
    }

    //根据传入的类型关键词、Dll名称，以及pathAlias到对应的目录下对模糊搜索,先去数据库中搜索，然后去metadata中找，在去重，如果metadata中也没有，就返回一个空列表，从数据库中搜索到的加一个Source="cache"的标记，metadata中搜索到的加一个Source="metadata"，这样调用这可以知道这个类型是从数据库中搜索到的还是用System.Reflection.Metadata中搜索到的，如果是从数据库中搜索到的就说明这个类型已经被反编译了
    public async Task<(List<TypeSearchResult> Results, string message)> SearchTypesAsync(string keyword, string dllNameHint, string pathAlias = "default")
    {
        if (string.IsNullOrWhiteSpace(keyword) || string.IsNullOrWhiteSpace(dllNameHint))
        {
            _logger.LogWarning("keyword or dllNameHint is null or empty");
            return (new List<TypeSearchResult>(), "keyword or dllNameHint is null or empty");
        }

        var basePath = _config.GetPathByAlias(pathAlias);
        if (string.IsNullOrEmpty(basePath))
        {
            return (new List<TypeSearchResult>(), $"Path alias '{pathAlias}' not found in configuration");
        }

        //从数据库中查询类型
        var dbTuple = await _db.SearchTypesAsync(keyword, dllNameHint, pathAlias);
        var dbResults = dbTuple.Results;
        //用反射直接查询dll的元数据
        var matadataResult = await SearchTypesFromDllAsync(keyword, dllNameHint, pathAlias);
        //过滤重复数据
        if (dbResults.Count != 0 && matadataResult.Count != 0)
        {
            foreach (var reslut in dbResults)
            {
                if (matadataResult.Any(x => x.TypeName == reslut.TypeName))
                {
                    matadataResult.Remove(matadataResult.Where(x => x.TypeName == reslut.TypeName).FirstOrDefault());
                }
            }
        }

        //合并数据
        var countOfCache = 0;
        var countOfMetadata = 0;
        List<TypeSearchResult> results = new List<TypeSearchResult>();
        results.AddRange(dbResults);
        results.AddRange(matadataResult);
        foreach (var result in results)
        {
            if (result.Source == "cache")
            {
                countOfCache++;
            }
            else
            {
                countOfMetadata++;
            }
        }
        //返回结果
        return (results, $"search {keyword} in {dllNameHint} in pathAlias {pathAlias} has {countOfCache} types from cache (Already decompiled) and {countOfMetadata} types from metadata (Not decompiled)");
    }

    //列出已反编译的dll的详细信息
    public async Task<List<AssemblyMetadata>> ListDecompiledAssembliesAsync()
    {
        return await _db.GetDecompiledAssembliesDetailAsync();
    }

    //从数据库中获取assemblyMetadata
    public async Task<AssemblyMetadata?> GetAssemblyMetadata(string dllName)
    {
        return await _db.GetAssemblyMetadataAsync(dllName);
    }

    //从未反编译的dll中获取类型的详细信息，用System.Reflection.Metadata
    private async Task<List<TypeSearchResult>> SearchTypesFromDllAsync(string keyword, string dllNameHint, string pathAlias = "default")
    {

        try
        {
            var results = new List<TypeSearchResult>();
            //定位dll文件
            var basePath = _config.GetPathByAlias(pathAlias);
            if (string.IsNullOrEmpty(basePath))
            {
                _logger.LogWarning($"Path alias '{pathAlias}' not found in configuration");
                return new List<TypeSearchResult>();
            }
            var dllPath = Path.Combine(basePath, dllNameHint);
            if (!File.Exists(dllPath))
            {
                _logger.LogWarning($"dllPath {dllPath} does not exist");
                return new List<TypeSearchResult>();
            }

            _logger.LogInformation($"Read type {keyword} metadata from dllPath {dllPath}");
            await using var fs = File.OpenRead(dllPath);
            using var peReader = new PEReader(fs);
            if (!peReader.HasMetadata)
            {
                _logger.LogWarning($"dllPath {dllPath} does not have metadata");
                return new List<TypeSearchResult>();
            }
            var metadataReader = peReader.GetMetadataReader();
            if (!metadataReader.IsAssembly)
            {
                _logger.LogWarning($"dllPath {dllPath} is not an assembly dll");
                return new List<TypeSearchResult>();
            }
            foreach (var type in metadataReader.TypeDefinitions)
            {
                var typeDef = metadataReader.GetTypeDefinition(type);
                var typeName = metadataReader.GetString(typeDef.Name);
                var namespaceName = metadataReader.GetString(typeDef.Namespace);

                // 跳过编译器生成的类型（与反编译逻辑保持一致）
                if (typeName.StartsWith("<") || typeName.Contains(">") ||
                    typeName.StartsWith("__") || typeName.Contains("AnonymousType") ||
                    typeName.Contains("DisplayClass"))
                {
                    continue;
                }

                // 获取完整的类型名称（包括命名空间）
                // 对于嵌套类型，需要递归获取父类型的命名空间
                var fullName = GetFullTypeName(metadataReader, typeDef, typeName, namespaceName);

                // 按关键字过滤
                if (!fullName.Contains(keyword))
                {
                    continue;
                }

                results.Add(new TypeSearchResult
                {
                    TypeName = fullName,
                    Namespace = namespaceName,
                    AssemblyKey = $"{pathAlias}:{dllNameHint}",
                    TypeKind = GetModelTypeKindFromMetadata(metadataReader, typeDef),
                    Source = "metadata",
                    PathAlias = pathAlias
                });
            }
            return results;
        }
        catch (System.Exception)
        {
            throw;
        }
    }

    //根据dllName和pathAlias定位dll文件的完整路径
    public string? ResolveDllPath(string? dllName, string pathAlias)
    {
        if (string.IsNullOrEmpty(dllName))
            return null;

        var basePath = _config.GetPathByAlias(pathAlias);
        if (basePath == null)
            return null;

        // 精确匹配
        var exactPath = Path.Combine(basePath, dllName);
        if (File.Exists(exactPath))
            return exactPath;

        // 模糊匹配（忽略大小写）
        var dllFiles = Directory.GetFiles(basePath, "*.dll");
        var matchedFile = dllFiles.FirstOrDefault(f =>
        Path.GetFileName(f).Equals(dllName, StringComparison.OrdinalIgnoreCase));

        return matchedFile;
    }

    //获取单个类型的反编译源码，先从数据库和缓存中找，如果没有，再反编译找。反编译后需要把结果缓存起来，还要更新对应的AssemblyMetadata（CachedTypeCount，并判断CachedTypeCount有没有等于TotalTypeCount，若等于就把IsFullyDecompiled改为YES）
    public async Task<OnDemandDecompileResult> GetOrDecompileTypeAsync(string typeName, string? dllName = null, string pathAlias = "default")
    {
        var result = new OnDemandDecompileResult();
        try
        {
            var dllPath = ResolveDllPath(dllName, pathAlias);
            var assemblyKey = $"{pathAlias}:{dllName}";
            if (string.IsNullOrEmpty(dllPath))
            {
                result.Success = false;
                result.Message = $"DLL {dllName} not found in path alias {pathAlias}";
                return result;
            }
            //从数据库和缓存中找这个type的源码
            var cacheCode = await _cache.LoadTypeCodeAsync(dllPath, assemblyKey, typeName);

            if (cacheCode != null)
            {
                result.Success = true;
                result.Code = cacheCode;
                result.Message = "Sucessufully Loaded from cache";
                result.Source = "cache";
                result.DllName = Path.GetFileName(dllName);
                return result;
            }

            //若缓存中没有，按需反编译单个类型，并存入缓存和数据库
            var decompiler = GetOrCreateDecompiler(dllPath);
            var typeSystem = decompiler.TypeSystem;
            var targetType = typeSystem.MainModule.TypeDefinitions.FirstOrDefault(t => t.FullName == typeName);
            if (targetType == null)
            {
                result.Success = false;
                result.Message = $"Type {typeName} not found in {dllName}";
                return result;
            }
            var code = decompiler.DecompileTypeAsString(targetType.FullTypeName);
            var types = new List<DecompiledType>
            {
                new DecompiledType
                {
                    TypeName = typeName,
                    Code = code,
                    TypeKind = targetType.Kind.ToString(),
                    CodeFilePath = _cache.GetTypeCodeFilePath(assemblyKey, typeName)
                }
            };
            //反编译成功，缓存起来
            await _cache.SaveToCacheAsync(dllPath, assemblyKey, types);
            //反编译成功，更新TypeRecord数据到数据库
            var saveSucess = await SaveTypeMetadataAsync(typeName, assemblyKey, dllPath, targetType.Kind.ToString());
            if (saveSucess)
            {
                result.Success = true;
                result.Code = code;
                result.Message = "Sucessufully Loaded from decompiler";
                result.Source = "decompiler";
                result.DllName = Path.GetFileName(dllName);
                return result;
            }
            if (!saveSucess)
            {
                result.Success = false;
                result.Message = $"This type has not been cached, Error saving type code {typeName} to database";
                return result;
            }
            return result;
        }
        catch (System.Exception ex)
        {
            result.Success = false;
            result.Message = $"Error compiling type {typeName}";
            _logger.LogError($"Error compiling type {typeName}: {ex.Message}", ex);
            return result;
        }



    }

    //从dll的metadata中获取到指定类型的成员（使用System.Reflection.Metadata，轻量高效）
    public async Task<TypeMembersResult> GetTypeMembersAsync(string typeFullName, string dllName, string pathAlias = "default")
    {
        var result = new TypeMembersResult();
        try
        {
            var dllPath = ResolveDllPath(dllName, pathAlias);
            if (string.IsNullOrEmpty(dllPath))
            {
                result.Success = false;
                result.Message = $"DLL {dllName} not found in path alias {pathAlias}";
                return result;
            }

            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();
            var provider = new MetadataSignatureTypeProvider(reader);
            var genericContext = new GenericContext();

            // 查找类型定义
            TypeDefinitionHandle? targetTypeHandle = null;
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeHandle);
                var typeName = reader.GetString(typeDef.Name);
                var namespaceName = reader.GetString(typeDef.Namespace);
                var fullName = GetFullTypeName(reader, typeDef, typeName, namespaceName);

                if (fullName == typeFullName)
                {
                    targetTypeHandle = typeHandle;
                    break;
                }
            }

            if (targetTypeHandle == null)
            {
                result.Success = false;
                result.Message = $"Type {typeFullName} not found in {dllName}";
                return result;
            }

            var targetType = reader.GetTypeDefinition(targetTypeHandle.Value);
            result.TypeFullName = typeFullName;
            result.Source = "metadata";

            // 获取 Methods（跳过属性/事件访问器和编译器生成的方法）
            foreach (var methodHandle in targetType.GetMethods())
            {
                var methodDef = reader.GetMethodDefinition(methodHandle);
                var methodName = reader.GetString(methodDef.Name);

                // 跳过编译器生成的和属性/事件访问器方法
                if (methodName.StartsWith("<")) continue;
                if (methodName.StartsWith("get_") || methodName.StartsWith("set_")) continue;
                if (methodName.StartsWith("add_") || methodName.StartsWith("remove_")) continue;

                var methodSig = methodDef.DecodeSignature(provider, genericContext);
                var returnType = methodSig.ReturnType;
                var isStatic = (methodDef.Attributes & MethodAttributes.Static) != 0;
                var isVirtual = (methodDef.Attributes & MethodAttributes.Virtual) != 0;
                var isAbstract = (methodDef.Attributes & MethodAttributes.Abstract) != 0;
                var visibility = GetMethodVisibility(methodDef.Attributes);

                // 获取参数名称
                var paramNames = new List<string>();
                foreach (var paramHandle in methodDef.GetParameters())
                {
                    var param = reader.GetParameter(paramHandle);
                    if (param.SequenceNumber > 0)
                    {
                        paramNames.Add(reader.GetString(param.Name));
                    }
                }

                // 构建参数列表
                var parameters = new List<string>();
                for (int i = 0; i < methodSig.ParameterTypes.Length; i++)
                {
                    var paramType = methodSig.ParameterTypes[i];
                    var paramName = i < paramNames.Count ? paramNames[i] : $"arg{i}";
                    parameters.Add($"{paramType} {paramName}");
                }

                var memberInfo = new Models.MemberInfo
                {
                    Name = methodName,
                    Kind = MemberKind.Method,
                    Visibility = visibility,
                    ReturnType = returnType,
                    IsStatic = isStatic,
                    IsVirtual = isVirtual,
                    IsAbstract = isAbstract,
                    Parameters = parameters
                };

                var mods = new List<string>();
                if (visibility != Visibility.Unknown) mods.Add(visibility.ToString().ToLower());
                if (isStatic) mods.Add("static");
                if (isAbstract) mods.Add("abstract");
                else if (isVirtual) mods.Add("virtual");
                var paramStr = string.Join(", ", parameters);
                memberInfo.Signature = $"{string.Join(" ", mods)} {returnType} {methodName}({paramStr})".Trim();

                result.Methods.Add(memberInfo);
                result.Members.Add(memberInfo);
            }

            // 获取 Properties
            foreach (var propHandle in targetType.GetProperties())
            {
                var propDef = reader.GetPropertyDefinition(propHandle);
                var propName = reader.GetString(propDef.Name);

                var propSig = propDef.DecodeSignature(provider, genericContext);
                var propType = propSig.ReturnType;

                // 从 getter/setter 获取可见性
                var visibility = Visibility.Unknown;
                var isStatic = false;
                var isVirtual = false;
                var isAbstract = false;

                var accessors = propDef.GetAccessors();
                if (!accessors.Getter.IsNil)
                {
                    var getterDef = reader.GetMethodDefinition(accessors.Getter);
                    visibility = GetMethodVisibility(getterDef.Attributes);
                    isStatic = (getterDef.Attributes & MethodAttributes.Static) != 0;
                    isVirtual = (getterDef.Attributes & MethodAttributes.Virtual) != 0;
                    isAbstract = (getterDef.Attributes & MethodAttributes.Abstract) != 0;
                }
                else if (!accessors.Setter.IsNil)
                {
                    var setterDef = reader.GetMethodDefinition(accessors.Setter);
                    visibility = GetMethodVisibility(setterDef.Attributes);
                    isStatic = (setterDef.Attributes & MethodAttributes.Static) != 0;
                    isVirtual = (setterDef.Attributes & MethodAttributes.Virtual) != 0;
                    isAbstract = (setterDef.Attributes & MethodAttributes.Abstract) != 0;
                }

                var accessorList = new List<string>();
                if (!accessors.Getter.IsNil) accessorList.Add("get");
                if (!accessors.Setter.IsNil) accessorList.Add("set");

                var memberInfo = new Models.MemberInfo
                {
                    Name = propName,
                    Kind = MemberKind.Property,
                    Visibility = visibility,
                    PropertyType = propType,
                    IsStatic = isStatic,
                    IsVirtual = isVirtual,
                    IsAbstract = isAbstract
                };

                var mods = new List<string>();
                if (visibility != Visibility.Unknown) mods.Add(visibility.ToString().ToLower());
                if (isStatic) mods.Add("static");
                if (isAbstract) mods.Add("abstract");
                else if (isVirtual) mods.Add("virtual");

                memberInfo.Signature = $"{string.Join(" ", mods)} {propType} {propName} {{ {string.Join("; ", accessorList)}; }}".Trim();

                result.Properties.Add(memberInfo);
                result.Members.Add(memberInfo);
            }

            // 获取 Fields（跳过编译器生成的字段）
            foreach (var fieldHandle in targetType.GetFields())
            {
                var fieldDef = reader.GetFieldDefinition(fieldHandle);
                var fieldName = reader.GetString(fieldDef.Name);

                if (fieldName.StartsWith("<")) continue;

                var fieldType = fieldDef.DecodeSignature(provider, genericContext);
                var isStatic = (fieldDef.Attributes & FieldAttributes.Static) != 0;
                var visibility = GetFieldVisibility(fieldDef.Attributes);

                var memberInfo = new Models.MemberInfo
                {
                    Name = fieldName,
                    Kind = MemberKind.Field,
                    Visibility = visibility,
                    FieldType = fieldType,
                    IsStatic = isStatic
                };

                var mods = new List<string>();
                if (visibility != Visibility.Unknown) mods.Add(visibility.ToString().ToLower());
                if (isStatic) mods.Add("static");

                memberInfo.Signature = $"{string.Join(" ", mods)} {fieldType} {fieldName}".Trim();

                result.Fields.Add(memberInfo);
                result.Members.Add(memberInfo);
            }

            // 获取 Events
            foreach (var eventHandle in targetType.GetEvents())
            {
                var eventDef = reader.GetEventDefinition(eventHandle);
                var eventName = reader.GetString(eventDef.Name);

                // 获取事件类型
                var eventType = "EventHandler";
                if (!eventDef.Type.IsNil)
                {
                    if (eventDef.Type.Kind == HandleKind.TypeReference)
                    {
                        var typeRef = reader.GetTypeReference((TypeReferenceHandle)eventDef.Type);
                        var ns = reader.GetString(typeRef.Namespace);
                        var name = reader.GetString(typeRef.Name);
                        eventType = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                    }
                    else if (eventDef.Type.Kind == HandleKind.TypeDefinition)
                    {
                        var td = reader.GetTypeDefinition((TypeDefinitionHandle)eventDef.Type);
                        var ns = reader.GetString(td.Namespace);
                        var name = reader.GetString(td.Name);
                        eventType = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                    }
                }

                // 从 add 方法获取可见性
                var visibility = Visibility.Unknown;
                var isStatic = false;
                var addMethod = eventDef.GetAccessors().Adder;
                if (!addMethod.IsNil)
                {
                    var adderDef = reader.GetMethodDefinition(addMethod);
                    visibility = GetMethodVisibility(adderDef.Attributes);
                    isStatic = (adderDef.Attributes & MethodAttributes.Static) != 0;
                }

                var memberInfo = new Models.MemberInfo
                {
                    Name = eventName,
                    Kind = MemberKind.Event,
                    Visibility = visibility,
                    IsStatic = isStatic,
                    AdditionalInfo = eventType
                };

                var mods = new List<string>();
                if (visibility != Visibility.Unknown) mods.Add(visibility.ToString().ToLower());
                if (isStatic) mods.Add("static");

                memberInfo.Signature = $"{string.Join(" ", mods)} event {eventType} {eventName}".Trim();

                result.Events.Add(memberInfo);
                result.Members.Add(memberInfo);
            }

            result.TotalCount = result.Members.Count;
            result.Success = true;
            result.Message = $"Found {result.TotalCount} members in {typeFullName} (Methods: {result.Methods.Count}, Properties: {result.Properties.Count}, Fields: {result.Fields.Count}, Events: {result.Events.Count})";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error getting type members: {ex.Message}";
            _logger.LogError("Error getting type members for {TypeName}: {Error}", typeFullName, ex.Message);
        }
        return result;
    }

    // 从 MethodAttributes 中提取可见性
    private Visibility GetMethodVisibility(MethodAttributes attributes)
    {
        var access = attributes & MethodAttributes.MemberAccessMask;
        return access switch
        {
            MethodAttributes.Public => Visibility.Public,
            MethodAttributes.Private => Visibility.Private,
            MethodAttributes.Family => Visibility.Protected,
            MethodAttributes.Assembly => Visibility.Internal,
            MethodAttributes.FamANDAssem => Visibility.Protected,
            MethodAttributes.FamORAssem => Visibility.Protected,
            _ => Visibility.Unknown
        };
    }


    // 从 FieldAttributes 中提取可见性
    private Visibility GetFieldVisibility(FieldAttributes attributes)
    {
        var access = attributes & FieldAttributes.FieldAccessMask;
        return access switch
        {
            FieldAttributes.Public => Visibility.Public,
            FieldAttributes.Private => Visibility.Private,
            FieldAttributes.Family => Visibility.Protected,
            FieldAttributes.Assembly => Visibility.Internal,
            FieldAttributes.FamANDAssem => Visibility.Protected,
            FieldAttributes.FamORAssem => Visibility.Protected,
            _ => Visibility.Unknown
        };
    }


    // 用于解码方法/属性/字段签名的类型提供器，将元数据类型句柄转换为字符串
    private readonly struct GenericContext { }

    private class MetadataSignatureTypeProvider : ISignatureTypeProvider<string, GenericContext>
    {
        private readonly MetadataReader _reader;

        public MetadataSignatureTypeProvider(MetadataReader reader)
        {
            _reader = reader;
        }

        public string GetArrayType(string elementType, ArrayShape shape)
            => $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => $"ref {elementType}";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

        public string GetGenericInstanceType(string genericType, ImmutableArray<string> typeArguments)
            => $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => GetGenericInstanceType(genericType, typeArguments);

        public string GetGenericMethodParameter(GenericContext genericContext, int index) => $"T{index}";

        public string GetGenericTypeParameter(GenericContext genericContext, int index) => $"T{index}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetGenericTypeDefinition(GenericContext genericContext, int parameterCount)
            => $"T{parameterCount}";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.Void => "void",
            _ => typeCode.ToString()
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            var ns = reader.GetString(typeDef.Namespace);
            var name = reader.GetString(typeDef.Name);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var typeRef = reader.GetTypeReference(handle);
            var ns = reader.GetString(typeRef.Namespace);
            var name = reader.GetString(typeRef.Name);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            var typeSpec = reader.GetTypeSpecification(handle);
            return typeSpec.DecodeSignature(this, genericContext);
        }
    }

    //反编译指定dll中的指定某个method，无论method所在的类型是否被缓存过，都会调用CSharpDecompiler反编译该方法（但是如果没有缓存，会先反编译类型再缓存）
    public async Task<MethodDecompileResult> DecompileMethodAsync(string methodFullName, string dllName, string pathAlias = "default")
    {
        var result = new MethodDecompileResult();
        try
        {
            //验证传入的方法全名
            var parts = methodFullName.Split('.');
            if (parts.Length < 2)
            {
                result.Success = false;
                result.Message = "方法全名格式错误，应为命名空间.类型名.方法名";
                result.MethodFullName = methodFullName;
                return result;
            }

            var methodName = parts[parts.Length - 1];
            var typeName = string.Join(".", parts.Take(parts.Length - 1));

            //从数据库中获取类型代码文件路径
            var codeFilePath = await _db.GetTypeCodeFilePathAsync(typeName);
            //获取dllPath和decompiler
            var dllPath = ResolveDllPath(dllName, pathAlias);
            if (string.IsNullOrEmpty(dllPath))
            {
                result.Success = false;
                result.Message = $"DLL {dllName} not found in path alias {pathAlias}";
                result.MethodFullName = methodFullName;
                return result;
            }
            var decompiler = GetOrCreateDecompiler(dllPath);
            var assemblyKey = $"{pathAlias}:{dllName}";
            //如果路径为空，说明该类型未缓存过，需先反编译类型再缓存
            if (codeFilePath == null)
            {
                var typeSystem = decompiler.TypeSystem;
                var targetType = typeSystem.MainModule.TypeDefinitions.FirstOrDefault(t => t.FullName == typeName);
                //如果method所处的类型不存在于这个dll中，就报错
                if (targetType == null)
                {
                    result.Success = false;
                    result.Message = $"The type {typeName} containing method {methodFullName} does not exist in the assembly {dllName}! Please ensure that the type exists in the assembly and is not a generic type!";
                    result.MethodFullName = methodFullName;
                    return result;
                }
                var typeCode = decompiler.DecompileTypeAsString(targetType.FullTypeName);
                var typeList = new List<DecompiledType>
                {
                    new DecompiledType
                    {
                        TypeName = typeName,
                        Code = typeCode,
                        TypeKind = targetType.Kind.ToString(),
                        CodeFilePath = _cache.GetTypeCodeFilePath(assemblyKey, typeName)
                    }
                };

                //生成对应的TypeRecord记录到数据库，同时更新对应的AssemblyMetadata
                var saveSuccess = await SaveTypeMetadataAsync(typeName, assemblyKey, dllPath, targetType.Kind.ToString());
                if (saveSuccess)
                {
                    result.Message += $"Cache type {typeName} sucessfully!";
                }
                else
                {
                    result.Success = false;
                    result.Message = $"Cache type {typeName} failed!";
                    result.MethodFullName = methodFullName;
                    return result;
                }
                //反编译成功，缓存起来
                await _cache.SaveToCacheAsync(dllPath, assemblyKey, typeList);
            }

            //代码路径存在，说明类型已缓存，但是无论有没有缓存到，都会调用CSharpDecompiler反编译该方法获得源码
            // var typeCode = await File.ReadAllTextAsync(codeFilePath);
            var fullTypeName = new FullTypeName(typeName);
            var type = decompiler.TypeSystem.FindType(fullTypeName).GetDefinition();
            if (type == null)
            {
                result.Success = false;
                result.Message += $"The Type of Method {typeName} not found!";
                result.MethodFullName = methodFullName;
                return result;
            }
            //从type中寻找方法
            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method == null)
            {
                result.Success = false;
                result.Message += $"Method {methodFullName} can not be found in type {typeName}!";
                result.MethodFullName = methodFullName;
                return result;
            }
            //反编译单个方法(不知道这样得到的单个方法反编译源码包不包含XML文档注释)
            var methodCode = decompiler.DecompileAsString(method.MetadataToken);

            result.Success = true;
            result.MethodCode = methodCode;
            result.MethodFullName = methodFullName;
            result.TotalMethods = 1; //单方法反编译永远是1
            result.IncludeXmlDoc = true;//反编译默认包含XML文档注释
            result.Message += $"Extract method {methodFullName} sucessfully!";
            return result;

        }
        catch (System.Exception ex)
        {
            result.Success = false;
            result.Message = $"Extract method {methodFullName} failed! Error: {ex.Message}";
            result.MethodFullName = methodFullName;
            return result;
        }
    }
    //搜索成员，因为数据库中只存在typerecord这种类型级别的记录，所以是无法通过类型定位到成员的，所以只能用Refelction.Metadata来搜索成员
    //    流程：
    // 1. 从数据库获取该 DLL 中已缓存的类型名称集合（快，用于标记 source）
    // 2. 用 Reflection.Metadata 遍历 DLL 中所有类型的所有成员，匹配 keyword
    // 3. 如果成员所属的类型已缓存 → source = "cache"
    // 4. 如果成员所属的类型未缓存 → source = "metadata"
    // 5. 去重合并返回
    public async Task<MemberSearchResult> SearchMemberAsync(string keyword, string dllName, string pathAlias)
    {
        var result = new MemberSearchResult
        {
            Keyword = keyword,
            AssemblyName = dllName
        };

        try
        {
            var dllPath = ResolveDllPath(dllName, pathAlias);
            if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
            {
                result.Success = false;
                result.Message = $"DLL {dllName} not found in path alias {pathAlias}";
                return result;
            }

            var assemblyKey = $"{pathAlias}:{dllName}";
            //获取这个dll的所有的在数据库中所有已存在的typeRecord记录,用于标记source
            var cachedTypeNames = await _db.GetCachedTypeNamesAsync(assemblyKey);

            //用System.Reflection.Metadata来搜索成员
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();

            //收集属性访问器方法名，用于过滤
            var accessorNames = new HashSet<string>();
            foreach (var typeDefHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeDefHandle);
                foreach (var propHandle in typeDef.GetProperties())
                {
                    var prop = reader.GetPropertyDefinition(propHandle);
                    var propName = reader.GetString(prop.Name);
                    accessorNames.Add($"get_{propName}");
                    accessorNames.Add($"set_{propName}");
                }
                foreach (var eventHandle in typeDef.GetEvents())
                {
                    var eventDef = reader.GetEventDefinition(eventHandle);
                    var eventName = reader.GetString(eventDef.Name);
                    accessorNames.Add($"add_{eventName}");
                    accessorNames.Add($"remove_{eventName}");
                    accessorNames.Add($"raise_{eventName}");
                }
            }

            foreach (var typeDefHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeDefHandle);
                var typeName = GetFullTypeName(reader, typeDef, reader.GetString(typeDef.Name), reader.GetString(typeDef.Namespace));

                //跳过编译器生成的类型
                if (typeName.StartsWith("<") || typeName.Contains(">") ||
                    typeName.StartsWith("__") || typeName.Contains("AnonymousType") ||
                    typeName.Contains("DisplayClass"))
                {
                    continue;
                }

                var isCached = cachedTypeNames.Contains(typeName);

                //搜索方法
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var methodName = reader.GetString(method.Name);

                    //跳过构造函数、属性访问器、事件访问器
                    if (methodName == ".ctor" || methodName == ".cctor" ||
                        accessorNames.Contains(methodName))
                    {
                        continue;
                    }

                    if (methodName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Matches.Add(new MemberSearchMatch
                        {
                            MemberName = methodName,
                            MemberFullName = $"{typeName}.{methodName}",
                            TypeName = typeName,
                            MemberType = "Method",
                            Source = isCached ? "cache" : "metadata"
                        });
                    }
                }

                //搜索属性
                foreach (var propHandle in typeDef.GetProperties())
                {
                    var prop = reader.GetPropertyDefinition(propHandle);
                    var propName = reader.GetString(prop.Name);

                    if (propName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Matches.Add(new MemberSearchMatch
                        {
                            MemberName = propName,
                            MemberFullName = $"{typeName}.{propName}",
                            TypeName = typeName,
                            MemberType = "Property",
                            Source = isCached ? "cache" : "metadata"
                        });
                    }
                }

                //搜索字段
                foreach (var fieldHandle in typeDef.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    var fieldName = reader.GetString(field.Name);

                    //跳过编译器生成的字段
                    if (fieldName.StartsWith("<") || fieldName.Contains(">") ||
                        fieldName.StartsWith("__"))
                    {
                        continue;
                    }

                    if (fieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Matches.Add(new MemberSearchMatch
                        {
                            MemberName = fieldName,
                            MemberFullName = $"{typeName}.{fieldName}",
                            TypeName = typeName,
                            MemberType = "Field",
                            Source = isCached ? "cache" : "metadata"
                        });
                    }
                }

                //搜索事件
                foreach (var eventHandle in typeDef.GetEvents())
                {
                    var eventDef = reader.GetEventDefinition(eventHandle);
                    var eventName = reader.GetString(eventDef.Name);

                    if (eventName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Matches.Add(new MemberSearchMatch
                        {
                            MemberName = eventName,
                            MemberFullName = $"{typeName}.{eventName}",
                            TypeName = typeName,
                            MemberType = "Event",
                            Source = isCached ? "cache" : "metadata"
                        });
                    }
                }
            }

            result.Success = true;
            result.TotalMatches = result.Matches.Count;
            var cacheCount = result.Matches.Count(m => m.Source == "cache");
            var metadataCount = result.Matches.Count(m => m.Source == "metadata");
            result.Message = $"Found {result.TotalMatches} members matching '{keyword}' in {dllName} (cache: {cacheCount}, metadata: {metadataCount})";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error searching members: {ex.Message}";
        }

        return result;
    }


    // 获取类型的完整名称（包括命名空间和嵌套类型路径）
    private string GetFullTypeName(MetadataReader reader, TypeDefinition typeDef, string typeName, string namespaceName)
    {
        // 检查是否是嵌套类型
        if (typeDef.IsNested)
        {
            // 获取声明类型（父类型）
            var declaringTypeHandle = typeDef.GetDeclaringType();
            if (!declaringTypeHandle.IsNil)
            {
                var declaringTypeDef = reader.GetTypeDefinition(declaringTypeHandle);
                var declaringTypeName = reader.GetString(declaringTypeDef.Name);
                var declaringNamespace = reader.GetString(declaringTypeDef.Namespace);

                // 递归获取父类型的完整名称
                var declaringTypeFullName = GetFullTypeName(reader, declaringTypeDef, declaringTypeName, declaringNamespace);
                return $"{declaringTypeFullName}.{typeName}";
            }
        }
        // 非嵌套类型，直接组合命名空间和类型名
        return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }
    //根据metadata中的typeDef，获取对应的Model.TypeKind
    private Models.TypeKind GetModelTypeKindFromMetadata(MetadataReader reader, TypeDefinition typeDef)
    {
        var attributes = typeDef.Attributes;

        //检查是否是接口
        if ((attributes & System.Reflection.TypeAttributes.Interface) != 0)
            return Models.TypeKind.Interface;

        //检查是否是委托
        if ((attributes & System.Reflection.TypeAttributes.Sealed) != 0 &&
            (attributes & System.Reflection.TypeAttributes.Abstract) != 0)
            return Models.TypeKind.Delegate;

        //检查是否是类，枚举，结构体，委托
        if ((attributes & System.Reflection.TypeAttributes.ClassSemanticsMask) == System.Reflection.TypeAttributes.Class)
        {
            var baseTypeHandle = typeDef.BaseType;
            if (baseTypeHandle.IsNil)
                return Models.TypeKind.Class;

            if (baseTypeHandle.Kind == HandleKind.TypeReference)
            {
                var baseTypeName = reader.GetString(reader.GetTypeReference((TypeReferenceHandle)baseTypeHandle).Name);
                if (baseTypeName == "Enum")
                    return Models.TypeKind.Enum;
            }
            return Models.TypeKind.Class;
        }
        return Models.TypeKind.Struct;
    }
    //将单个反编译得到的typeRecord储存到数据库中并且更新对应的dll的AssemblyMetadata
    private async Task<bool> SaveTypeMetadataAsync(string typeName, string assemblyKey, string dllPath, string typeKind)
    {
        var codeFilePath = _cache.GetTypeCodeFilePath(assemblyKey, typeName);
        var typeRecord = new TypeRecord
        {
            TypeName = typeName,
            AssemblyKey = assemblyKey,
            AssemblyPath = dllPath,
            TypeKind = Enum.Parse<Models.TypeKind>(typeKind),
            CodeFilePath = codeFilePath,
            DecompileTime = DateTime.UtcNow
        };
        var saveSucess = await _db.SaveTypeAsync(typeRecord);
        var updateSucess = await UpdateAssemblyCachedCountAsync(assemblyKey, dllPath);
        return saveSucess && updateSucess;
    }

    //在按需获取了一个tpye的源码后，更新对应的dll的AssemblyMetadata中的CachedCount字段，若这个dll此时还没有AssemblyMetadata记录，就创建一个记录，CachedCount设为1，若更新了CachedCount字段等于totalTypeCount，就更新IsFullyDecompiled为"YES"
    private async Task<bool> UpdateAssemblyCachedCountAsync(string assemblyKey, string dllPath)
    {
        // assemblyKey 格式: "alias:dllName.dll"，需要提取 dllName 部分
        var colonIndex = assemblyKey.IndexOf(':');
        var dllName = colonIndex >= 0 ? assemblyKey.Substring(colonIndex + 1) : assemblyKey;
        var metadata = await _db.GetAssemblyMetadataAsync(dllName);
        //若数据库中没有这个dll的记录，就创建一个记录，CachedCount设为1
        if (metadata == null)
        {
            var fileInfo = new FileInfo(dllPath);
            //用CSharpDecompiler获取dll元数据
            var decompiler = GetOrCreateDecompiler(dllPath);
            var types = decompiler.TypeSystem.MainModule.TypeDefinitions;
            var totalTypeCount = types.Count(t => (!t.Name.Contains("<") && !t.Name.Contains(">")) && !t.IsCompilerGenerated() && !t.IsAnonymousType());

            //保存到数据库
            var saveSucess = await _db.SaveAssemblyMetadataAsync(assemblyKey, dllName, dllPath, fileInfo.LastWriteTime, fileInfo.Length, totalTypeCount, 1, "NO", DateTime.UtcNow);
            return saveSucess;
        }
        //若有这个记录，就更新CachedCount字段加1，视情况更新Status为"YES"
        var updateSucess = await _db.AssemblyMetadataCachedTypeCountPlus1Async(dllName);
        return updateSucess;
    }

    // ========== 无状态反编译工具（任意本地 DLL，不写数据库和缓存文件）==========

    /// 验证 dllPath 是否合法（非空、含非法字符、后缀、文件存在）
    private string? ValidateDllPath(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            return "dllPath is null or empty";

        // 只检查目录遍历攻击，不检查反斜杠等合法 Windows 路径字符
        if (dllPath.Contains(".."))
            return $"dllPath '{dllPath}' contains directory traversal pattern";

        if (!dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return $"dllPath '{dllPath}' must end with .dll";

        if (!File.Exists(dllPath))
            return $"DLL file not found: {dllPath}";

        return null;
    }

    /// 列出任意 DLL 中的所有类型（通过 System.Reflection.Metadata 读取元数据，不写数据库和缓存）
    public DecompileAnyListTypesResult DecompileAnyListTypes(string dllPath)
    {
        var result = new DecompileAnyListTypesResult { DllPath = dllPath };

        var error = ValidateDllPath(dllPath);
        if (error != null)
        {
            result.Success = false;
            result.Message = error;
            return result;
        }

        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();

            foreach (var typeDefHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeDefHandle);
                var typeName = reader.GetString(typeDef.Name);
                var namespaceName = reader.GetString(typeDef.Namespace);
                var fullName = GetFullTypeName(reader, typeDef, typeName, namespaceName);

                // 跳过编译器生成的类型
                if (fullName.StartsWith("<") || fullName.Contains(">") ||
                    fullName.StartsWith("__") || fullName.Contains("AnonymousType") ||
                    fullName.Contains("DisplayClass"))
                {
                    continue;
                }

                result.Types.Add(new AnyDllTypeInfo
                {
                    TypeName = fullName,
                    TypeKind = GetModelTypeKindFromMetadata(reader, typeDef).ToString()
                });
            }

            result.Success = true;
            result.TotalTypes = result.Types.Count;
            result.Message = $"Found {result.TotalTypes} types in {dllPath}";
        }
        catch (System.Exception ex)
        {
            result.Success = false;
            result.Message = $"Error reading types: {ex.Message}";
            _logger.LogError(ex, "Error listing types from {DllPath}", dllPath);
        }

        return result;
    }

    /// 反编译任意 DLL 中的单个类型（不写数据库和缓存文件，仅使用内存 LRU 缓存避免重复加载 DLL）
    public OnDemandDecompileResult DecompileAnyType(string dllPath, string typeName)
    {
        var result = new OnDemandDecompileResult { TypeName = typeName, DllName = Path.GetFileName(dllPath) };

        var error = ValidateDllPath(dllPath);
        if (error != null)
        {
            result.Success = false;
            result.Message = error;
            return result;
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            result.Success = false;
            result.Message = "typeName is null or empty";
            return result;
        }

        try
        {
            var decompiler = GetOrCreateDecompiler(dllPath);
            var typeSystem = decompiler.TypeSystem;
            var targetType = typeSystem.MainModule.TypeDefinitions.FirstOrDefault(t => t.FullName == typeName);

            if (targetType == null)
            {
                result.Success = false;
                result.Message = $"Type '{typeName}' not found in {Path.GetFileName(dllPath)}";
                return result;
            }

            var code = decompiler.DecompileTypeAsString(targetType.FullTypeName);

            result.Success = true;
            result.Code = code;
            result.Message = "Decompiled successfully";
            result.Source = "decompiler";
        }
        catch (System.Exception ex)
        {
            result.Success = false;
            result.Message = $"Error decompiling type '{typeName}': {ex.Message}";
            _logger.LogError(ex, "Error decompiling type {TypeName} from {DllPath}", typeName, dllPath);
        }

        return result;
    }

    /// 反编译任意 DLL 中的单个方法（不写数据库和缓存文件，不追踪调用链）
    public MethodDecompileResult DecompileAnyMethod(string dllPath, string methodFullName)
    {
        var result = new MethodDecompileResult { MethodFullName = methodFullName };

        // 校验 dllPath
        var error = ValidateDllPath(dllPath);
        if (error != null)
        {
            result.Success = false;
            result.Message = error;
            return result;
        }

        // 校验方法全名格式
        var parts = methodFullName.Split('.');
        if (parts.Length < 2)
        {
            result.Success = false;
            result.Message = "方法全名格式错误，应为命名空间.类型名.方法名";
            return result;
        }

        var methodName = parts[parts.Length - 1];
        var typeName = string.Join(".", parts.Take(parts.Length - 1));

        try
        {
            var decompiler = GetOrCreateDecompiler(dllPath);
            var typeSystem = decompiler.TypeSystem;

            // 查找类型
            var type = typeSystem.FindType(new FullTypeName(typeName))?.GetDefinition();
            if (type == null)
            {
                result.Success = false;
                result.Message = $"Type '{typeName}' not found in {Path.GetFileName(dllPath)}";
                return result;
            }

            // 查找方法
            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method == null)
            {
                result.Success = false;
                result.Message = $"Method '{methodName}' not found in type '{typeName}'";
                return result;
            }

            // 反编译单个方法
            var methodCode = decompiler.DecompileAsString(method.MetadataToken);

            result.Success = true;
            result.MethodCode = methodCode;
            result.TotalMethods = 1;
            result.IncludeXmlDoc = true;
            result.Message = $"Method '{methodFullName}' extracted successfully";
        }
        catch (System.Exception ex)
        {
            result.Success = false;
            result.Message = $"Error extracting method '{methodFullName}': {ex.Message}";
            _logger.LogError(ex, "Error extracting method {MethodFullName} from {DllPath}", methodFullName, dllPath);
        }

        return result;
    }
}
