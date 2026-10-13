using System.Text.Json;
using System.Text.Json.Nodes;
using ErpDecompilerAgenticRAG_Mcp.Models;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

/// <summary>
/// Layer 增删改查编排服务（基于 Ice.Lib.MetaFXSvc BO API）。
///
/// 核心机制（来自源码级评估结论）：
///   1. GetApp 返回"编辑器状态"（基础层+所有已发布层合并后的完整 JSON）
///   2. SaveApp 必须传完整最终状态（全量传入），服务端与基础层 diff 后只存增量（增量存储）
///   3. 发布 = SaveApp + layerInfo.wip=false（PublishApp 对自定义层未实现，会抛 NotImplementedException）
///   4. WIP 草稿存 Ice.XXXDef.SysCharacter03，发布版内容转存 Content 列，KNTCPublishLog 记审计
///
/// 安全设计：
///   - 创建/更新默认只写 WIP 草稿（wip=true），不影响运行时界面；发布是独立工具
///   - 任一补丁失败则整体放弃保存（内存状态直接丢弃，不会写入半成品）
///   - pages/orphans 强制非 null（LayerPatchEngine.BuildSaveRequest），杜绝"缺 pages → 全部页面被标记 Remove"的破坏性事故
/// </summary>
public class LayerService
{
    private const string ServiceName = "Ice.Lib.MetaFXSvc";
    private const string DefaultTypeCode = "KNTCCustLayer";
    private const string DefaultDeviceType = "Desktop";

    private readonly EpicorLayerClient _client;
    private readonly EpicorConnectionConfig _config;
    private readonly ILogger<LayerService> _logger;

    public LayerService(EpicorLayerClient client, EpicorConnectionConfig config, ILogger<LayerService> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    // ==================== 查询类 ====================

    /// <summary>GetApp：获取视图的完整编辑器状态（基础+所有已发布层合并）。
    /// additionalContext.inPreviewMode=true 是 App Studio 预览通道，可跳过集成账号没有的菜单安全检查。</summary>
    public async Task<JsonObject> GetAppStateAsync(string viewId)
    {
        var payload = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["id"] = viewId,
                ["properties"] = new JsonObject
                {
                    ["applicationType"] = "view",
                    ["deviceType"] = DefaultDeviceType,
                    ["company"] = "",
                    ["additionalContext"] = new JsonObject { ["inPreviewMode"] = true }
                }
            }
        };

        var result = await _client.CallBoMethodAsync(ServiceName, "GetApp", payload);
        if (result is not JsonObject appState)
        {
            throw new EpicorLayerException($"视图 '{viewId}' 不存在或无 MetaUI 内容（GetApp 返回空）。");
        }
        return appState;
    }

    /// <summary>GetLayers：列出视图下的层（可选包含未发布草稿）</summary>
    public async Task<JsonArray> ListLayersAsync(string viewId, bool includeUnpublished = true)
    {
        var payload = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["ViewId"] = viewId,
                ["TypeCode"] = DefaultTypeCode,
                ["DeviceType"] = DefaultDeviceType,
                ["IncludeUnpublishedLayers"] = includeUnpublished
            }
        };
        var result = await _client.CallBoMethodAsync(ServiceName, "GetLayers", payload);
        return result as JsonArray ?? new JsonArray();
    }

    /// <summary>LayerExists：判断层是否存在。注意 Company 必须显式传空串（传 null 会匹配失败——实测结论）</summary>
    public async Task<bool> LayerExistsAsync(string viewId, string layerName, string typeCode = DefaultTypeCode, string deviceType = DefaultDeviceType)
    {
        var payload = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["ViewId"] = viewId,
                ["LayerName"] = layerName,
                ["TypeCode"] = typeCode,
                ["DeviceType"] = deviceType,
                ["Company"] = ""
            }
        };
        var result = await _client.CallBoMethodAsync(ServiceName, "LayerExists", payload);
        return result?.GetValue<bool>() ?? false;
    }

    /// <summary>GetLayer：读取层的存储状态（含差分内容）。层不存在返回 null</summary>
    public async Task<JsonObject?> GetStoredLayerAsync(string viewId, string layerName,
        string typeCode = DefaultTypeCode, string deviceType = DefaultDeviceType)
    {
        var payload = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["ViewId"] = viewId,
                ["LayerName"] = layerName,
                ["TypeCode"] = typeCode,
                ["DeviceType"] = deviceType,
                ["Company"] = "",
                ["IncludeDraftContent"] = true
            }
        };
        var result = await _client.CallBoMethodAsync(ServiceName, "GetLayer", payload);
        return result as JsonObject;
    }

    /// <summary>GetAuditLogs：读取层的发布审计日志</summary>
    public async Task<JsonArray> GetAuditLogsAsync(string viewId, string? layerName = null)
    {
        var request = new JsonObject
        {
            ["ViewId"] = viewId,
            ["LayerName"] = layerName,
            ["Company"] = ""
        };
        var payload = new JsonObject { ["request"] = request };
        var result = await _client.CallBoMethodAsync(ServiceName, "GetAuditLogs", payload);
        return result as JsonArray ?? new JsonArray();
    }

    // ==================== 局部读取 ====================

    /// <summary>读取视图状态的局部内容（按 target+targetId 定位，可再下钻 subPath），避免返回整个大 JSON</summary>
    public async Task<JsonObject> ReadPartAsync(string viewId, string target, string targetId, string? subPath = null)
    {
        var appState = await GetAppStateAsync(viewId);
        return LayerPatchEngine.ReadPart(appState, target, targetId, subPath);
    }

    // ==================== 写入类 ====================

    /// <summary>
    /// 创建新层（WIP 草稿）：GetApp 取当前完整状态 → 原样回传 → SaveApp(wip=true, IsNew=true)。
    /// 新层初始为"空层"（与基础状态一致，无任何修改）。
    /// </summary>
    public async Task<JsonObject> CreateLayerAsync(string viewId, string layerName, string description)
    {
        ValidateLayerName(layerName);

        if (await LayerExistsAsync(viewId, layerName))
        {
            throw new EpicorLayerException($"层 '{layerName}' 已存在于视图 '{viewId}'。请换名字，或用 layer_update 修改已有层。");
        }

        var appState = await GetAppStateAsync(viewId);
        var saveRequest = LayerPatchEngine.BuildSaveRequest(appState, viewId, layerName, description,
            commentText: $"Created by MCP Layer tool at {DateTime.Now:yyyy-MM-dd HH:mm}", wip: true, isNew: true);

        await SaveAppAsync(saveRequest);

        // 读回验证 + 生成差分回执
        var stored = await GetStoredLayerAsync(viewId, layerName);
        return new JsonObject
        {
            ["success"] = true,
            ["message"] = $"层 '{layerName}' 已创建为 WIP 草稿（未发布，不影响运行时界面）。用 layer_update 添加修改，layer_publish 发布。",
            ["viewId"] = viewId,
            ["layerName"] = layerName,
            ["wip"] = stored?["WIP"]?.GetValue<bool>(),
            ["diffReceipt"] = LayerPatchEngine.BuildDiffReceipt(stored)
        };
    }

    /// <summary>
    /// 更新层（默认保存为 WIP 草稿）。
    /// 流程：GetApp（基础+已发布层）→ 恢复该层已有草稿差分 → 应用 AI 补丁 → SaveApp。
    /// 任一补丁失败则整体不保存。
    /// </summary>
    public async Task<JsonObject> UpdateLayerAsync(string viewId, string layerName, JsonArray patches,
        bool publish = false, string description = "")
    {
        if (!await LayerExistsAsync(viewId, layerName))
        {
            throw new EpicorLayerException($"层 '{layerName}' 不存在于视图 '{viewId}'。请先用 layer_create 创建。");
        }

        var appState = await GetAppStateAsync(viewId);

        // 恢复该层已有草稿（HasDraftContent=true 时其差分尚未体现在 GetApp 状态中）
        var restoreLog = new List<string>();
        var storedBefore = await GetStoredLayerAsync(viewId, layerName);
        if (storedBefore?["HasDraftContent"]?.GetValue<bool>() == true && storedBefore["Content"] is JsonObject draftContent)
        {
            restoreLog = LayerPatchEngine.ApplyStoredDiffs(appState, draftContent);
        }

        // 应用 AI 补丁
        var patchResults = LayerPatchEngine.ApplyPatches(appState, patches);
        var failed = patchResults.Where(r => r.Status == "failed").ToList();
        if (failed.Count > 0)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["message"] = $"{failed.Count}/{patchResults.Count} 个补丁失败，已放弃保存（未写入任何更改）。",
                ["patchResults"] = JsonSerializer.SerializeToNode(patchResults)
            };
        }

        // 保存（publish=false 存草稿；true 直接发布）
        var effectiveDescription = string.IsNullOrWhiteSpace(description)
            ? storedBefore?["LayerDescription"]?.GetValue<string>() ?? layerName
            : description;
        var saveRequest = LayerPatchEngine.BuildSaveRequest(appState, viewId, layerName, effectiveDescription,
            commentText: $"{(publish ? "Published" : "Updated")} by MCP Layer tool at {DateTime.Now:yyyy-MM-dd HH:mm}",
            wip: !publish, isNew: false);
        await SaveAppAsync(saveRequest);

        var storedAfter = await GetStoredLayerAsync(viewId, layerName);
        var wipAfter = storedAfter?["WIP"]?.GetValue<bool>() ?? false;
        var hasDraft = storedAfter?["HasDraftContent"]?.GetValue<bool>() ?? false;

        // 状态语义（来自服务端 CreateStoreDataRequest 源码）：
        //   WIP=true + 草稿           → 纯草稿层（从未发布）
        //   WIP=false + 草稿(HasDraftContent=true) → 已发布层上有新草稿（运行时仍显示旧发布版，编辑器显示草稿）
        //   WIP=false + 无草稿        → 稳定发布状态
        string message;
        if (publish)
        {
            message = $"层 '{layerName}' 已发布（WIP=false），改动立即对运行时界面生效。";
        }
        else if (!wipAfter && hasDraft)
        {
            message = $"层 '{layerName}' 的修改已存入草稿。注意：该层此前已发布，运行时界面仍显示原发布版内容；用 layer_publish 发布后新改动才生效。";
        }
        else
        {
            message = $"层 '{layerName}' 已保存为 WIP 草稿（未发布，不影响运行时界面）。用 layer_publish 发布。";
        }

        return new JsonObject
        {
            ["success"] = true,
            ["message"] = message,
            ["viewId"] = viewId,
            ["layerName"] = layerName,
            ["wip"] = wipAfter,
            ["hasDraftContent"] = hasDraft,
            ["patchResults"] = JsonSerializer.SerializeToNode(patchResults),
            ["draftRestoreLog"] = restoreLog.Count > 0 ? JsonSerializer.SerializeToNode(restoreLog) : null,
            ["diffReceipt"] = LayerPatchEngine.BuildDiffReceipt(storedAfter)
        };
    }

    /// <summary>
    /// 发布层：GetApp → 恢复草稿差分 → SaveApp(wip=false)。
    /// 发布后内容转存 Content 列，立即对运行时界面生效，并产生 KNTCPublishLog 审计记录。
    /// </summary>
    public async Task<JsonObject> PublishLayerAsync(string viewId, string layerName)
    {
        if (!await LayerExistsAsync(viewId, layerName))
        {
            throw new EpicorLayerException($"层 '{layerName}' 不存在于视图 '{viewId}'。");
        }

        var appState = await GetAppStateAsync(viewId);

        // 把该层的当前差分（草稿优先）应用到编辑器状态——这是要发布的内容
        var restoreLog = new List<string>();
        var stored = await GetStoredLayerAsync(viewId, layerName);
        if (stored?["Content"] is JsonObject content)
        {
            restoreLog = LayerPatchEngine.ApplyStoredDiffs(appState, content);
        }

        var saveRequest = LayerPatchEngine.BuildSaveRequest(appState, viewId, layerName,
            stored?["LayerDescription"]?.GetValue<string>() ?? layerName,
            commentText: $"Published by MCP Layer tool at {DateTime.Now:yyyy-MM-dd HH:mm}",
            wip: false, isNew: false);
        await SaveAppAsync(saveRequest);

        // 读回验证发布状态
        var published = await GetStoredLayerAsync(viewId, layerName);
        return new JsonObject
        {
            ["success"] = true,
            ["message"] = $"层 '{layerName}' 已发布，改动立即对运行时界面生效。",
            ["viewId"] = viewId,
            ["layerName"] = layerName,
            ["wip"] = published?["WIP"]?.GetValue<bool>(),
            ["publishedContentDiffCount"] = LayerPatchEngine.BuildDiffReceipt(published).Count,
            ["draftRestoreLog"] = restoreLog.Count > 0 ? JsonSerializer.SerializeToNode(restoreLog) : null
        };
    }

    /// <summary>删除层（WIP 草稿和发布版一并删除）。KNTCPublishLog 审计历史按 Epicor 设计保留</summary>
    public async Task<JsonObject> DeleteLayerAsync(string viewId, string layerName,
        string typeCode = DefaultTypeCode, string deviceType = DefaultDeviceType)
    {
        if (!await LayerExistsAsync(viewId, layerName, typeCode, deviceType))
        {
            throw new EpicorLayerException($"层 '{layerName}' 不存在于视图 '{viewId}'，无需删除。");
        }

        var payload = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["ViewId"] = viewId,
                ["LayerName"] = layerName,
                ["TypeCode"] = typeCode,
                ["DeviceType"] = deviceType,
                ["Company"] = ""
            }
        };
        await _client.CallBoMethodAsync(ServiceName, "DeleteLayer", payload);

        var stillExists = await LayerExistsAsync(viewId, layerName, typeCode, deviceType);
        return new JsonObject
        {
            ["success"] = !stillExists,
            ["message"] = stillExists
                ? $"DeleteLayer 已执行但层 '{layerName}' 仍存在，请检查。"
                : $"层 '{layerName}' 已删除（KNTCPublishLog 审计历史按 Epicor 设计保留）。",
            ["viewId"] = viewId,
            ["layerName"] = layerName
        };
    }

    // ==================== 内部方法 ====================

    private async Task SaveAppAsync(JsonObject saveRequest)
    {
        var payload = new JsonObject { ["request"] = saveRequest };
        await _client.CallBoMethodAsync(ServiceName, "SaveApp", payload);
    }

    /// <summary>层名校验：服务端规则——不允许逗号；同时限制长度防止超出 Key1(50) 字段</summary>
    private static void ValidateLayerName(string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            throw new EpicorLayerException("层名不能为空。");
        }
        if (layerName.Contains(','))
        {
            throw new EpicorLayerException("层名不允许包含逗号（Epicor 服务端校验规则）。");
        }
        if (layerName.Length > 50)
        {
            throw new EpicorLayerException($"层名长度 {layerName.Length} 超过上限 50 字符。");
        }
    }
}
