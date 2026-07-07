public class CacheMetadata
{
    public string AssemblyKey { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public string FileLastWriteTime { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int TypeCount { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string DecompileTime { get; set; } = string.Empty;
}