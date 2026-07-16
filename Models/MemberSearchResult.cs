namespace ErpDecompilerAgenticRAG_Mcp.Models;

// 成员搜索结果
public class MemberSearchResult
{
    // 是否成功
    public bool Success { get; set; }

    // 消息
    public string Message { get; set; } = string.Empty;

    // 搜索关键字
    public string Keyword { get; set; } = string.Empty;

    // 所属 DLL 文件名
    public string AssemblyName { get; set; } = string.Empty;

    // 搜索结果列表
    public List<MemberSearchMatch> Matches { get; set; } = new();

    // 总匹配数
    public int TotalMatches { get; set; }
}

// 成员搜索匹配项
public class MemberSearchMatch
{
    // 成员名称（方法名、属性名、字段名）
    public string MemberName { get; set; } = string.Empty;

    // 成员全名（含类型）
    public string MemberFullName { get; set; } = string.Empty;

    // 所属类型名
    public string TypeName { get; set; } = string.Empty;

    // 成员类型（Method, Property, Field）
    public string MemberType { get; set; } = string.Empty;

    // Source
    public string Source { get; set;} = string.Empty;

}