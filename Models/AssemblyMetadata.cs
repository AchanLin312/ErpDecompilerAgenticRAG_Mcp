using System.ComponentModel.DataAnnotations;

namespace ErpDecompilerAgenticRAG_Mcp.Models;

public class AssemblyMetadata
{
    [Key]
    public string AssemblyKey { get; set; } = string.Empty; //格式: DirectoryPath:AssemblyName
    
    public string AssemblyName { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public DateTime FileLastWriteTime { get; set; }
    public long FileSize { get; set; }
    public int TotalTypeCount { get; set; }
    public int CachedTypeCount { get; set; } = 0;
    public string IsFullyDecompiled { get; set; } = "NO";
    public DateTime DecompileTime { get; set; }
}