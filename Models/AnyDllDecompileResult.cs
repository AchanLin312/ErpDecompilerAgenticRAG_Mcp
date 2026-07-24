namespace ErpDecompilerAgenticRAG_Mcp.Models;

/// 无状态反编译操作的基础结果
public class AnyDllDecompileResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// 列出任意 DLL 中所有类型的结果
public class DecompileAnyListTypesResult : AnyDllDecompileResult
{
    public string DllPath { get; set; } = string.Empty;
    public int TotalTypes { get; set; }
    public List<AnyDllTypeInfo> Types { get; set; } = new();
}

public class AnyDllTypeInfo
{
    public string TypeName { get; set; } = string.Empty;
    public string TypeKind { get; set; } = string.Empty;
}
