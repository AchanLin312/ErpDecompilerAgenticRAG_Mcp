// Models/TypeRecord.cs（已有，只需添加数据注解）
using System.ComponentModel.DataAnnotations;

namespace ErpDecompilerAgenticRAG_Mcp.Models;

public class TypeRecord
{
    [Key]
    public string TypeName { get; set; } = string.Empty;
    
    public string AssemblyName { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public TypeKind TypeKind { get; set; } = TypeKind.Unknown;
    public string CodeFilePath { get; set; } = string.Empty;
    public DateTime DecompileTime { get; set; }
}