namespace ErpDecompilerAgenticRAG_Mcp.Models;
public class TypeSearchResult
{
    /// <summary>
    /// 类型完整名称
    /// </summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// 命名空间
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// 所属 DLL 文件名
    /// </summary>
    public string AssemblyName { get; set; } = string.Empty;

    /// <summary>
    /// 类型种类
    /// </summary>
    public TypeKind TypeKind { get; set; } = TypeKind.Unknown;

    /// <summary>
    /// 数据来源（cache/metadata）
    /// </summary>
    public string Source { get; set; } = "cache";

    /// <summary>
    /// 路径别名
    /// </summary>
    public string PathAlias { get; set; } = string.Empty;
}