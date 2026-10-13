using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErpDecompilerAgenticRAG_Mcp.Services;
using ModelContextProtocol.Server;

namespace ErpDecompilerAgenticRAG_Mcp.Tools;

/// <summary>
/// Epicor Kinetic Layer（自定义层）增删改查 MCP 工具集。
/// 基于 Ice.Lib.MetaFXSvc BO API（REST API V2 OData）。
///
/// 架构原则：AI 永远不接触完整 Layer JSON（大视图可达数千行）——
///   - layer_inspect 返回压缩摘要（组件树/事件/规则清单）供 AI 定位
///   - layer_read_part 返回局部内容（按 id + subPath）
///   - layer_update 只接收小补丁（targetId + path + op + value），完整 JSON 的搬运在服务内部完成
///
/// 安全原则：
///   - 创建/更新默认只写 WIP 草稿（不影响运行时界面），发布是独立操作
///   - ⚠️ 发布（layer_publish / layer_update publish=true）后改动立即对所有用户的界面生效
/// </summary>
[McpServerToolType]
public class LayerTools
{
    private readonly LayerService _layerService;

    public LayerTools(LayerService layerService)
    {
        _layerService = layerService;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string Serialize(JsonNode node) => node.ToJsonString(JsonOptions);

    // ==================== 查询类（只读，安全） ====================

    [McpServerTool(Name = "layer_inspect")]
    [Description(@"查看指定 Epicor Kinetic 视图的 UI 结构摘要（基于 GetApp，含基础层+所有已发布层合并后的状态）。
返回：组件树骨架（id/类型/标题/数据绑定，供精确定位）、事件/规则/工具栏/数据视图清单、页面清单、现有 Layer 列表。
这是 Layer 开发的第一步：先 inspect 拿到 id，再用 layer_read_part 看细节，最后 layer_update 打补丁。

必需参数：
- viewId: 视图全名，如 Erp.UI.PartEntry / Ice.UI.UD106Entry / Ice.UI.ConnectedUsers
示例：{ ""viewId"": ""Ice.UI.ConnectedUsers"" }")]
    public async Task<string> Inspect([Description("视图全名，如 Erp.UI.PartEntry")] string viewId)
    {
        var appState = await _layerService.GetAppStateAsync(viewId);
        var summary = LayerPatchEngine.BuildSummary(appState);

        // 顺带返回现有层清单，AI 可了解该视图已有哪些层
        var layers = await _layerService.ListLayersAsync(viewId, includeUnpublished: true);
        summary["existingLayers"] = layers;

        return Serialize(summary);
    }

    [McpServerTool(Name = "layer_read_part")]
    [Description(@"读取视图 UI 状态的局部内容（不返回整个大 JSON）。
先用 layer_inspect 拿到目标 id，再用本工具读取该目标的完整 JSON；可用 subPath 进一步下钻到具体属性。

必需参数：
- viewId: 视图全名
- target: layout（组件）/ events（事件）/ rules（规则）/ tools（工具栏按钮）/ dataviews（数据视图）/ pages（页面）
- targetId: 目标 id（layout/pages 为组件 id 或页面 name；其余为条目 id）
可选参数：
- subPath: 相对目标的属性路径（'/' 分隔，段可为属性名或数组索引），如 ""model/gridModel/editOptions"" 或 ""actions/2""

示例：{ ""viewId"": ""Ice.UI.ConnectedUsers"", ""target"": ""layout"", ""targetId"": ""grdConnectedUsers"", ""subPath"": ""model/gridModel"" }")]
    public async Task<string> ReadPart(
        [Description("视图全名，如 Erp.UI.PartEntry")] string viewId,
        [Description("目标类别：layout/events/rules/tools/dataviews/pages")] string target,
        [Description("目标 id（组件 id / 事件 id / 页面 name 等，来自 layer_inspect）")] string targetId,
        [Description("可选：相对目标的属性路径，进一步缩小返回范围")] string? subPath = null)
    {
        var result = await _layerService.ReadPartAsync(viewId, target, targetId, subPath);
        return Serialize(result);
    }

    [McpServerTool(Name = "layer_list")]
    [Description(@"列出指定视图下的所有 Layer（含 WIP 草稿）。
返回每层的名称、描述、WIP 状态、最后修改人/时间、是否有草稿内容。

必需参数：
- viewId: 视图全名
可选参数：
- includeUnpublished: 是否包含未发布的 WIP 草稿（默认 true）
示例：{ ""viewId"": ""Erp.UI.APInvoiceEntry"" }")]
    public async Task<string> ListLayers(
        [Description("视图全名")] string viewId,
        [Description("是否包含未发布草稿，默认 true")] bool includeUnpublished = true)
    {
        var layers = await _layerService.ListLayersAsync(viewId, includeUnpublished);
        return Serialize(new JsonObject
        {
            ["viewId"] = viewId,
            ["count"] = layers.Count,
            ["layers"] = layers
        });
    }

    [McpServerTool(Name = "layer_exists")]
    [Description(@"判断指定视图下是否存在某个 Layer。返回 true/false。
示例：{ ""viewId"": ""Erp.UI.PartEntry"", ""layerName"": ""MyLayer"" }")]
    public async Task<string> Exists(
        [Description("视图全名")] string viewId,
        [Description("层名")] string layerName)
    {
        var exists = await _layerService.LayerExistsAsync(viewId, layerName);
        return Serialize(new JsonObject
        {
            ["viewId"] = viewId,
            ["layerName"] = layerName,
            ["exists"] = exists
        });
    }

    [McpServerTool(Name = "layer_audit_logs")]
    [Description(@"读取视图（或指定层）的发布审计日志（KNTCPublishLog）：谁在什么时候发布过什么层。
可选参数：
- layerName: 只看某个层（不传则返回该视图全部）
示例：{ ""viewId"": ""Erp.UI.APInvoiceEntry"", ""layerName"": ""VendorInvoice"" }")]
    public async Task<string> AuditLogs(
        [Description("视图全名")] string viewId,
        [Description("可选：层名（不传返回该视图全部日志）")] string? layerName = null)
    {
        var logs = await _layerService.GetAuditLogsAsync(viewId, layerName);
        return Serialize(new JsonObject
        {
            ["viewId"] = viewId,
            ["layerName"] = layerName,
            ["count"] = logs.Count,
            ["auditLogs"] = logs
        });
    }

    // ==================== 写入类（会改变数据） ====================

    [McpServerTool(Name = "layer_create")]
    [Description(@"在指定视图上创建新的自定义 Layer（WIP 草稿状态，不影响运行时界面）。
新层初始为“空层”（与当前界面状态一致，无任何修改），创建后用 layer_update 打补丁。

必需参数：
- viewId: 视图全名
- layerName: 新层名（唯一、不含逗号、≤50字符；同视图下不能与现有层重名）
- description: 层描述（会显示在 App Studio 的层列表中）
示例：{ ""viewId"": ""Ice.UI.ConnectedUsers"", ""layerName"": ""ud_MyLayer"", ""description"": ""Connected Users 定制"" }")]
    public async Task<string> Create(
        [Description("视图全名，如 Erp.UI.PartEntry")] string viewId,
        [Description("新层名（不含逗号，≤50字符）")] string layerName,
        [Description("层描述")] string description)
    {
        var result = await _layerService.CreateLayerAsync(viewId, layerName, description);
        return Serialize(result);
    }

    [McpServerTool(Name = "layer_update")]
    [Description(@"用补丁方式修改指定 Layer 的内容（默认保存为 WIP 草稿，不影响运行时界面）。
支持在已有草稿基础上继续修改（自动恢复草稿内容后叠加新补丁）。任一补丁失败则整体放弃保存。

必需参数：
- viewId: 视图全名
- layerName: 已存在的层名（不存在请先用 layer_create）
- patches: 补丁数组的 JSON 字符串，每个补丁：
  {
    ""target"": ""layout|events|rules|tools|dataviews|pages"",   // 修改哪类内容
    ""targetId"": ""组件id或事件id"",                              // 定位键（来自 layer_inspect，稳定不变）
    ""path"": ""model/props/readonly"",                           // 相对目标的属性路径（'/'分隔，可为数组索引），空=目标本身
    ""op"": ""replace|set|add|remove|append|insert-before|insert-after"",
    ""value"": <任意JSON值>,                                       // remove 不需要
    ""index"": 2                                                   // 仅 insert-before/after 需要
  }
  op 说明：replace=改已有属性(必须存在)；set=有则改无则建；add=仅新增；remove=删除；append=数组末尾追加；insert-before/after=按 index 插入

可选参数：
- description: 更新层描述（不传保留原描述）
- publish: ⚠️ true 则保存后直接发布，改动立即对所有用户生效（默认 false 只存草稿）

示例：把网格设为可编辑
{ ""viewId"": ""Ice.UI.ConnectedUsers"", ""layerName"": ""ud_MyLayer"",
  ""patches"": ""[{""target"":""layout"",""targetId"":""grdConnectedUsers"",""path"":""model/gridModel/editable"",""op"":""replace"",""value"":true}]"" }")]
    public async Task<string> Update(
        [Description("视图全名")] string viewId,
        [Description("已存在的层名")] string layerName,
        [Description(@"补丁数组的 JSON 字符串，如 [{""target"":""layout"",""targetId"":""grid1"",""path"":""model/title"",""op"":""replace"",""value"":""新标题""}]")] string patches,
        [Description("可选：更新层描述")] string? description = null,
        [Description("⚠️ true 则直接发布生效（默认 false 存草稿）")] bool publish = false)
    {
        JsonArray patchArray;
        try
        {
            var parsed = JsonNode.Parse(patches);
            patchArray = parsed as JsonArray ?? throw new JsonException("patches 必须是 JSON 数组");
        }
        catch (JsonException ex)
        {
            return Serialize(new JsonObject
            {
                ["success"] = false,
                ["message"] = $"patches 不是合法的 JSON 数组：{ex.Message}"
            });
        }

        var result = await _layerService.UpdateLayerAsync(viewId, layerName, patchArray, publish, description ?? "");
        return Serialize(result);
    }

    [McpServerTool(Name = "layer_publish")]
    [Description(@"⚠️ 发布指定 Layer：改动立即对运行时界面生效（所有用户可见），并产生审计日志。
发布 = 把该层当前内容（草稿优先）正式化：内容从草稿区转存发布区（Ice.XXXDef 的 SysCharacter03 → Content 列）。
发布前建议先在 App Studio 或浏览器中预览确认（打开视图 URL 加 ?layer=层名 可预览指定层）。

必需参数：
- viewId: 视图全名
- layerName: 层名
示例：{ ""viewId"": ""Ice.UI.ConnectedUsers"", ""layerName"": ""ud_MyLayer"" }")]
    public async Task<string> Publish(
        [Description("视图全名")] string viewId,
        [Description("层名")] string layerName)
    {
        var result = await _layerService.PublishLayerAsync(viewId, layerName);
        return Serialize(result);
    }

    [McpServerTool(Name = "layer_delete")]
    [Description(@"删除指定 Layer（WIP 草稿与发布版一并删除）。KNTCPublishLog 审计历史按 Epicor 设计保留。
⚠️ 不可恢复，删除前请确认。若只想撤回未发布的修改，删除后重建即可。

必需参数：
- viewId: 视图全名
- layerName: 层名
示例：{ ""viewId"": ""Ice.UI.ConnectedUsers"", ""layerName"": ""ud_MyLayer"" }")]
    public async Task<string> Delete(
        [Description("视图全名")] string viewId,
        [Description("层名")] string layerName)
    {
        var result = await _layerService.DeleteLayerAsync(viewId, layerName);
        return Serialize(result);
    }
}
