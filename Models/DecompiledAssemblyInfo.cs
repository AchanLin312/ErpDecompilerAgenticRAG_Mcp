namespace ErpDecompilerAgenticRAG_Mcp.Models;
public class DecompiledAssemblyInfo
{
    /// <summary>
    /// DLL 文件名
    /// </summary>
    public string AssemblyName { get; set; } = string.Empty;

    /// <summary>
    /// 路径别名
    /// </summary>
    public string PathAlias { get; set; } = string.Empty;

    /// <summary>
    /// 类型数量
    /// </summary>
    public int TypeCount { get; set; }

    /// <summary>
    /// 反编译时间
    /// </summary>
    public DateTime? DecompileTime { get; set; }
}