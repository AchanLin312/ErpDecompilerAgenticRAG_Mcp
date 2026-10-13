namespace ErpDecompilerAgenticRAG_Mcp.Models;

/// <summary>
/// Epicor REST API 连接配置（从 appsettings.json 的 EpicorConnection 节读取）。
/// 认证方式与 Erp-Mcp-Bridge 保持一致：Basic Auth + X-API-Key 请求头。
/// </summary>
public class EpicorConnectionConfig
{
    /// <summary>Epicor 应用服务器地址，如 https://epicor-srv2019.gpe.com</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Epicor 实例（环境）名，如 gpedev</summary>
    public string Environment { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>默认公司（REST URL 中的公司上下文，如 GPEHZ）</summary>
    public string DefaultCompany { get; set; } = "GPEHZ";

    /// <summary>REST 调用超时（秒），GetApp 对大视图可能较慢，默认 300</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>配置是否完整（缺任何必填项时 Layer 工具应报配置错误而不是连接错误）</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerUrl) &&
        !string.IsNullOrWhiteSpace(Environment) &&
        !string.IsNullOrWhiteSpace(UserName) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}
