namespace ErpDecompilerAgenticRAG_Mcp.Models;

public class DecompileResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<string> TypeNames { get; set; } = new();

    public int TotalTypes { get; set; }

    public string? AssemblyName { get; set; }
}