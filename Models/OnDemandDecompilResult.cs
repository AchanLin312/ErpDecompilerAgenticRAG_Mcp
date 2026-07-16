namespace ErpDecompilerAgenticRAG_Mcp.Models;

/// 按需反编译单个类型的结果
public class OnDemandDecompileResult
{
    /// 是否成功
    public bool Success { get; set; }

    /// 结果消息
    public string Message { get; set; } = string.Empty;

    /// 类型全名
    public string TypeName { get; set; } = string.Empty;

    /// 反编译后的C#代码
    public string Code { get; set; } = string.Empty;

    /// 代码来源（cache/decompile）
    public string Source { get; set; } = string.Empty;

    /// DLL名称
    public string DllName { get; set; } = string.Empty;
}