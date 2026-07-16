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
        if (alias == "default")
            return DefaultPath;
        return AlternativePaths.TryGetValue(alias, out var path) ? path : null;
    }

    public Dictionary<string, string> GetAllPaths()
    {
        var paths = new Dictionary<string, string>();
        // 默认路径使用 "default" 作为别名
        if (!string.IsNullOrEmpty(DefaultPath))
        {
            paths["default"] = DefaultPath;
        }

        // 添加非默认路径
        foreach (var altPath in AlternativePaths)
        {
            paths[altPath.Key] = altPath.Value;
        }

        return paths;
    }
}