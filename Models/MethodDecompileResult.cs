namespace ErpDecompilerAgenticRAG_Mcp.Models;

// 方法反编译结果
public class MethodDecompileResult
{
    // 是否成功
    public bool Success { get; set; }

    // 消息
    public string Message { get; set; } = string.Empty;

    // 方法全名
    public string MethodFullName { get; set; } = string.Empty;

    // 主方法代码
    public string MethodCode { get; set; } = string.Empty;

    // 调用链深度
    // public int CallChainDepth { get; set; }

    // 被调用的方法列表（递归追踪）
    // public List<CalledMethod> CalledMethods { get; set; } = new();

    // 总方法数（包含主方法）
    public int TotalMethods { get; set; }

    // 是否包含 XML 文档注释
    public bool IncludeXmlDoc { get; set; }
}

// 被调用的方法信息
public class CalledMethod
{
    // 方法名称
    public string MethodName { get; set; } = string.Empty;

    // 方法全名（含类型）
    public string MethodFullName { get; set; } = string.Empty;

    // 调用深度（0=主方法直接调用，1=间接调用）
    public int Depth { get; set; }

    // 方法代码
    public string Code { get; set; } = string.Empty;

    // 所属类型
    public string? TypeName { get; set; }

    // 是否已解析（如果找不到方法代码，则为 false）
    public bool Resolved { get; set; }
}