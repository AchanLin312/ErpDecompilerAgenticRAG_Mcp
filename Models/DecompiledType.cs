namespace ErpDecompilerAgenticRAG_Mcp.Models;
public class DecompiledType
{
    public string TypeName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string CodeFilePath { get; set; } = string.Empty;
    public string TypeKind { get; set; } = "Class";
}