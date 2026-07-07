namespace ErpDecompilerAgenticRAG_Mcp.Models;

public class ErpConfig
{
    public string DefaultPath { get; set; } = string.Empty;
    public Dictionary<string, string> AlternativePaths { get; set; } = new();
    public int MaxCallChainDepth { get; set; } = 6;
    public string DatabasePath { get; set; } = string.Empty;
    public string LogLevel { get; set; } = "Information";

    public string? GetPathByAlias(string alias = "default")
    {
        return AlternativePaths.TryGetValue(alias, out var path) ? path : DefaultPath;
    }
}