using System.Text;
using System.Text.Json.Nodes;
using ErpDecompilerAgenticRAG_Mcp.Models;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

/// <summary>Epicor REST API 调用异常（包含服务端返回的详细错误信息）</summary>
public class EpicorLayerException : Exception
{
    public EpicorLayerException(string message) : base(message) { }
    public EpicorLayerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Epicor REST API V2 (OData) 轻量客户端，专用于 Layer 操作所需的 Ice.Lib.MetaFXSvc BO 方法调用。
/// 调用模式：POST {ServerUrl}/{Environment}/api/V2/odata/{company}/{service}/{method}，
/// 认证方式与 Erp-Mcp-Bridge 保持一致：Basic Auth + X-API-Key 请求头。
/// HttpClient 无共享状态，天然线程安全，无需加锁。
/// </summary>
public class EpicorLayerClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EpicorConnectionConfig _config;

    public EpicorLayerClient(IHttpClientFactory httpClientFactory, EpicorConnectionConfig config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    /// <summary>
    /// 调用 BO 方法。
    /// 返回响应中的 returnObj 节点（void 方法返回 null；整体即结果时返回根节点）。
    /// </summary>
    /// <param name="serviceName">服务名，如 Ice.Lib.MetaFXSvc</param>
    /// <param name="methodName">方法名，如 GetApp / SaveApp / DeleteLayer</param>
    /// <param name="payload">请求体 JSON（通常是 {"request": {...}} 形式）</param>
    /// <param name="company">公司上下文（不传用配置的 DefaultCompany）</param>
    public async Task<JsonNode?> CallBoMethodAsync(string serviceName, string methodName, JsonNode payload, string? company = null)
    {
        if (!_config.IsConfigured)
        {
            throw new EpicorLayerException(
                "Epicor 连接未配置。请在 appsettings.json 的 EpicorConnection 节填写 ServerUrl / Environment / UserName / Password / ApiKey 后重启 MCP。");
        }

        var companyCode = string.IsNullOrWhiteSpace(company) ? _config.DefaultCompany : company;
        var url = $"{_config.ServerUrl.TrimEnd('/')}/{_config.Environment.Trim('/')}/api/V2/odata/{companyCode}/{serviceName}/{methodName}";

        var client = _httpClientFactory.CreateClient("EpicorLayer");

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await client.PostAsync(url, new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        }
        catch (HttpRequestException ex)
        {
            throw new EpicorLayerException($"无法连接 Epicor 服务器（{url}）：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new EpicorLayerException($"调用 {serviceName}.{methodName} 超时（{_config.TimeoutSeconds}s），响应数据量可能过大或网络异常。", ex);
        }

        using var _ = httpResponse;
        var body = await httpResponse.Content.ReadAsStringAsync();

        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new EpicorLayerException(
                $"调用 {serviceName}.{methodName} 失败 (HTTP {(int)httpResponse.StatusCode})：{ExtractErrorMessage(body)}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null; // void 方法成功且无响应体
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            throw new EpicorLayerException($"调用 {serviceName}.{methodName} 成功但响应不是合法 JSON：{Truncate(body)}", ex);
        }

        // Epicor REST 响应格式：{"returnObj": ...}；void 方法可能返回 {}（保持原样返回便于上层判断）
        if (root is JsonObject obj && obj.TryGetPropertyValue("returnObj", out var returnObj))
        {
            return returnObj;
        }
        return root;
    }

    /// <summary>从 Epicor 错误响应体中提取可读的错误信息</summary>
    private static string ExtractErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                var errorMessage = obj["ErrorMessage"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    var details = new List<string>();
                    if (obj["ErrorDetails"] is JsonArray detailArray)
                    {
                        foreach (var detail in detailArray)
                        {
                            var message = detail?["Message"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(message))
                            {
                                details.Add(message);
                            }
                        }
                    }
                    return details.Count > 0
                        ? $"{errorMessage} | 详情: {string.Join("; ", details)}"
                        : errorMessage;
                }
            }
        }
        catch
        {
            // body 不是 JSON 时回退到原文
        }
        return Truncate(body);
    }

    private static string Truncate(string text, int maxLen = 800)
        => text.Length <= maxLen ? text : text[..maxLen] + "...(已截断)";
}
