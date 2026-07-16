namespace ErpDecompilerAgenticRAG_Mcp.Models;
public class TypeSearchResult
{
    /// 类型完整名称
    public string TypeName { get; set; } = string.Empty;

    /// 命名空间
    public string Namespace { get; set; } = string.Empty;

    /// 所属 DLL 文件名
    public string AssemblyKey { get; set; } = string.Empty;

    /// 类型种类
    public TypeKind TypeKind { get; set; } = TypeKind.Unknown;

    /// 数据来源（cache/metadata）
    public string Source { get; set; } = "cache";

    /// 路径别名
    public string PathAlias { get; set; } = string.Empty;
}