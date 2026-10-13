using System.Text.Json.Nodes;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

/// <summary>单个补丁的应用结果</summary>
public class PatchResult
{
    public int Index { get; set; }
    public string Target { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Status { get; set; } = "applied"; // applied / failed / skipped
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Layer 补丁引擎：负责大 JSON 的定位、局部修改与状态恢复。
/// 设计原则：AI 永远不接触完整 JSON——
///   1. BuildSummary   把几千行的编辑器状态压缩成几十行摘要（供 AI 定位）
///   2. ApplyPatches   按 AI 描述的小补丁（targetId + path + op + value）做程序化修改
///   3. ApplyStoredDiffs 把数据库里已存的层差分（diff 格式）重新应用到编辑器状态（恢复草稿）
/// 所有 JSON 操作都是确定性的程序行为（JsonNode 引用修改），不经过任何"重新生成"。
/// </summary>
public static class LayerPatchEngine
{
    // ==================== 摘要生成（layer_inspect 的核心） ====================

    /// <summary>
    /// 把 GetApp 返回的完整编辑器状态压缩为结构化摘要：
    /// 组件树骨架（id/类型/标题/绑定）、事件/规则/工具/dataviews 清单、页面清单。
    /// 一个几千行 JSON 的视图，摘要通常只有几十行，供 AI 精确定位目标。
    /// </summary>
    public static JsonObject BuildSummary(JsonObject appState)
    {
        var layout = appState["Layout"] as JsonObject;

        var summary = new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["name"] = layout?["name"]?.GetValue<string>(),
                ["caption"] = layout?["caption"]?.GetValue<string>(),
                ["viewType"] = layout?["viewType"]?.GetValue<string>(),
                ["pageType"] = layout?["pageType"]?.GetValue<string>()
            }
        };

        // 组件树骨架：递归输出，每项一行关键信息
        var components = new JsonArray();
        if (layout?["components"] is JsonArray rootComponents)
        {
            foreach (var component in rootComponents.OfType<JsonObject>())
            {
                components.Add(SummarizeComponent(component));
            }
        }
        summary["components"] = components;

        // 事件清单
        var events = new JsonArray();
        if (appState["Events"] is JsonArray eventArray)
        {
            foreach (var e in eventArray.OfType<JsonObject>())
            {
                events.Add(new JsonObject
                {
                    ["id"] = e["id"]?.GetValue<string>(),
                    ["description"] = e["description"]?.GetValue<string>(),
                    ["trigger"] = e["trigger"] is JsonObject t ? $"{t["type"]}:{t["target"]}/{t["hook"]}" : null,
                    ["actionCount"] = (e["actions"] as JsonArray)?.Count ?? 0
                });
            }
        }
        summary["events"] = events;

        // 规则清单
        var rules = new JsonArray();
        if (appState["Rules"] is JsonArray ruleArray)
        {
            foreach (var r in ruleArray.OfType<JsonObject>())
            {
                rules.Add(new JsonObject
                {
                    ["id"] = r["id"]?.GetValue<string>(),
                    ["condition"] = r["condition"]?.GetValue<string>(),
                    ["dataView"] = r["dataView"]?.GetValue<string>()
                });
            }
        }
        summary["rules"] = rules;

        // 工具栏按钮清单
        var tools = new JsonArray();
        if (appState["ToolBar"] is JsonArray toolArray)
        {
            foreach (var t in toolArray.OfType<JsonObject>())
            {
                tools.Add(new JsonObject
                {
                    ["id"] = t["id"]?.GetValue<string>(),
                    ["type"] = t["type"]?.GetValue<string>(),
                    ["text"] = t["text"]?.GetValue<string>(),
                    ["epBinding"] = t["epBinding"]?.GetValue<string>()
                });
            }
        }
        summary["tools"] = tools;

        // 数据视图清单
        var dataViews = new JsonArray();
        if (appState["DataViews"] is JsonArray dvArray)
        {
            foreach (var d in dvArray.OfType<JsonObject>())
            {
                dataViews.Add(new JsonObject
                {
                    ["id"] = d["id"]?.GetValue<string>(),
                    ["table"] = d["table"]?.GetValue<string>(),
                    ["schema"] = d["schema"]?.GetValue<string>(),
                    ["datasetId"] = d["datasetId"]?.GetValue<string>(),
                    ["description"] = d["description"]?.GetValue<string>()
                });
            }
        }
        summary["dataviews"] = dataViews;

        // 页面清单（Dashboard 类型视图通常为空）
        var pages = new JsonArray();
        if (appState["Pages"] is JsonArray pageArray)
        {
            foreach (var p in pageArray.OfType<JsonObject>())
            {
                pages.Add(new JsonObject
                {
                    ["name"] = p["name"]?.GetValue<string>(),
                    ["componentCount"] = (p["components"] as JsonArray)?.Count ?? 0
                });
            }
        }
        summary["pages"] = pages;

        return summary;
    }

    /// <summary>单个组件的摘要：id（定位键）、类型、标题、数据绑定、子组件树</summary>
    private static JsonObject SummarizeComponent(JsonObject component)
    {
        var model = component["model"] as JsonObject;
        var summary = new JsonObject
        {
            ["id"] = component["id"]?.GetValue<string>(),
            ["type"] = component["sourceTypeId"]?.GetValue<string>(),
            ["title"] = model?["title"]?.GetValue<string>(),
            // 数据绑定在不同组件模型里的位置不同，两处都探测
            ["epBinding"] = model?["epBinding"]?.GetValue<string>() ?? (model?["gridModel"] as JsonObject)?["epBinding"]?.GetValue<string>()
        };

        if (component["components"] is JsonArray children && children.Count > 0)
        {
            var childSummaries = new JsonArray();
            foreach (var child in children.OfType<JsonObject>())
            {
                childSummaries.Add(SummarizeComponent(child));
            }
            summary["children"] = childSummaries;
        }
        return summary;
    }

    // ==================== 局部读取（layer_read_part 的核心） ====================

    /// <summary>
    /// 读取编辑器状态的局部内容：按 target+targetId 定位目标，可选 subPath 下钻。
    /// 返回 {target, targetId, subPath, content}，content 是定位到的 JSON 节点。
    /// </summary>
    public static JsonObject ReadPart(JsonObject appState, string target, string targetId, string? subPath = null)
    {
        var targetNode = LocateTarget(appState, target, targetId);
        if (targetNode == null)
        {
            throw new EpicorLayerException(
                $"未找到目标：target={target}, targetId={targetId}。请先用 layer_inspect 查看该视图的组件/事件/规则等 id 清单。");
        }

        JsonNode? content = targetNode;
        if (!string.IsNullOrWhiteSpace(subPath))
        {
            foreach (var segment in subPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                content = NavigateStep(content, segment);
                if (content == null)
                {
                    throw new EpicorLayerException($"subPath '{subPath}' 在段 '{segment}' 处无法导航（属性不存在或索引越界）。");
                }
            }
        }

        return new JsonObject
        {
            ["target"] = target,
            ["targetId"] = targetId,
            ["subPath"] = subPath,
            ["content"] = content?.DeepClone()
        };
    }

    // ==================== 目标定位 ====================

    /// <summary>
    /// 按 target + targetId 在编辑器状态中定位目标节点。
    /// target: layout（组件树 DFS）/ events / rules / tools / dataviews（数组顶层 id 匹配）/ pages（name 匹配或组件 DFS）
    /// </summary>
    private static JsonNode? LocateTarget(JsonObject appState, string target, string targetId)
    {
        switch (target.ToLowerInvariant())
        {
            case "layout":
            {
                // 在 Layout.components 树中深度优先查找 id 匹配的组件
                if (appState["Layout"]?["components"] is JsonArray components)
                {
                    var found = FindComponentById(components, targetId);
                    if (found != null) return found;
                }
                // 允许直接定位 Layout 根（targetId 为 null 或 "*"）
                return targetId is null or "*" ? appState["Layout"] : null;
            }
            case "events":
                return FindByIdInArray(appState["Events"] as JsonArray, targetId);
            case "rules":
                return FindByIdInArray(appState["Rules"] as JsonArray, targetId);
            case "tools":
                return FindByIdInArray(appState["ToolBar"] as JsonArray, targetId);
            case "dataviews":
                return FindByIdInArray(appState["DataViews"] as JsonArray, targetId);
            case "pages":
            {
                if (appState["Pages"] is JsonArray pages)
                {
                    // 优先按 page.name 匹配
                    foreach (var page in pages.OfType<JsonObject>())
                    {
                        if (string.Equals(page["name"]?.GetValue<string>(), targetId, StringComparison.OrdinalIgnoreCase))
                        {
                            return page;
                        }
                    }
                    // 其次在所有 page 的组件树中查找组件 id
                    foreach (var page in pages.OfType<JsonObject>())
                    {
                        if (page["components"] is JsonArray components)
                        {
                            var found = FindComponentById(components, targetId);
                            if (found != null) return found;
                        }
                    }
                }
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>组件树深度优先查找 id 匹配的组件对象</summary>
    private static JsonObject? FindComponentById(JsonArray components, string id)
    {
        foreach (var component in components.OfType<JsonObject>())
        {
            if (string.Equals(component["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            {
                return component;
            }
            if (component["components"] is JsonArray children)
            {
                var found = FindComponentById(children, id);
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>在 JSON 数组顶层按 id 属性匹配查找对象</summary>
    private static JsonObject? FindByIdInArray(JsonArray? array, string id)
    {
        if (array == null) return null;
        foreach (var item in array.OfType<JsonObject>())
        {
            if (string.Equals(item["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }

    // ==================== AI 补丁应用 ====================

    /// <summary>
    /// 应用 AI 描述的补丁列表。每个补丁：
    ///   target   - layout/events/rules/tools/dataviews/pages
    ///   targetId - 组件 id / 事件 id / 规则 id / page name（定位键，稳定不随数组顺序变化）
    ///   path     - 相对目标的属性路径（'/' 分隔，段可为属性名或数组索引；空=目标本身）
    ///   op       - replace(必须已存在) / set(存在则替换不存在则创建) / add(仅新增) / remove / append(数组末尾追加) / insert-before / insert-after
    ///   value    - 新值（remove 时不需要）
    ///   index    - insert-before/after 时的插入位置
    /// </summary>
    public static List<PatchResult> ApplyPatches(JsonObject appState, JsonArray patches)
    {
        var results = new List<PatchResult>();
        for (var i = 0; i < patches.Count; i++)
        {
            var result = new PatchResult { Index = i };
            try
            {
                if (patches[i] is not JsonObject patch)
                {
                    result.Status = "failed";
                    result.Message = "补丁必须是 JSON 对象";
                    results.Add(result);
                    continue;
                }

                result.Target = patch["target"]?.GetValue<string>() ?? string.Empty;
                result.TargetId = patch["targetId"]?.GetValue<string>() ?? string.Empty;
                var path = patch["path"]?.GetValue<string>() ?? string.Empty;
                var op = (patch["op"]?.GetValue<string>() ?? "replace").ToLowerInvariant();
                var value = patch["value"]?.DeepClone();
                var index = patch["index"]?.GetValue<int>();

                // 1. 定位目标
                var targetNode = LocateTarget(appState, result.Target, result.TargetId);
                if (targetNode == null)
                {
                    result.Status = "failed";
                    result.Message = $"未找到目标：target={result.Target}, targetId={result.TargetId}。请先用 layer_inspect 查看可用 id。";
                    results.Add(result);
                    continue;
                }

                // 2. 导航到 path 的父节点
                var current = targetNode;
                JsonObject? parentObject = null;
                JsonArray? parentArray = null;
                var lastSegment = string.Empty;

                if (!string.IsNullOrEmpty(path))
                {
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var s = 0; s < segments.Length; s++)
                    {
                        var segment = segments[s];
                        lastSegment = segment;

                        if (current is JsonObject obj)
                        {
                            if (s == segments.Length - 1)
                            {
                                parentObject = obj;
                                current = obj[segment]!;
                            }
                            else
                            {
                                current = obj[segment] ?? throw new InvalidOperationException($"路径不存在：段 '{segment}'（{string.Join("/", segments[..(s + 1)])}）");
                            }
                        }
                        else if (current is JsonArray arr)
                        {
                            if (!int.TryParse(segment, out var arrayIndex) || arrayIndex < 0 || arrayIndex >= arr.Count)
                            {
                                throw new InvalidOperationException($"数组索引无效：'{segment}'（{string.Join("/", segments[..(s + 1)])}，数组长度 {arr.Count}）");
                            }
                            if (s == segments.Length - 1)
                            {
                                parentArray = arr;
                                current = arr[arrayIndex]!;
                            }
                            else
                            {
                                current = arr[arrayIndex]!;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"路径无法继续导航：段 '{segment}' 处不是对象或数组");
                        }
                    }
                }

                // 3. 执行操作
                switch (op)
                {
                    case "replace":
                        if (parentObject != null)
                        {
                            if (parentObject[lastSegment] == null) throw new InvalidOperationException($"replace 失败：'{lastSegment}' 不存在（不存在时请用 op=set 或 add）");
                            parentObject[lastSegment] = value;
                        }
                        else if (parentArray != null && int.TryParse(lastSegment, out var ri) && value != null)
                        {
                            parentArray[ri] = value;
                        }
                        else
                        {
                            throw new InvalidOperationException("replace 需要非空 path 指向已存在的属性");
                        }
                        break;

                    case "set": // upsert：存在则替换，不存在则创建
                        if (parentObject != null)
                        {
                            parentObject[lastSegment] = value;
                        }
                        else if (parentArray != null && int.TryParse(lastSegment, out var si) && value != null)
                        {
                            parentArray[si] = value;
                        }
                        else
                        {
                            throw new InvalidOperationException("set 需要非空 path");
                        }
                        break;

                    case "add":
                        if (parentObject != null)
                        {
                            if (parentObject[lastSegment] != null) throw new InvalidOperationException($"add 失败：'{lastSegment}' 已存在（覆盖请用 replace 或 set）");
                            parentObject[lastSegment] = value;
                        }
                        else
                        {
                            throw new InvalidOperationException("add 需要非空 path 指向对象属性");
                        }
                        break;

                    case "remove":
                        if (parentObject != null)
                        {
                            if (parentObject[lastSegment] == null) throw new InvalidOperationException($"remove 失败：'{lastSegment}' 不存在");
                            parentObject.Remove(lastSegment);
                        }
                        else if (parentArray != null && int.TryParse(lastSegment, out var rmi))
                        {
                            parentArray.RemoveAt(rmi);
                        }
                        else
                        {
                            throw new InvalidOperationException("remove 需要非空 path");
                        }
                        break;

                    case "append":
                    {
                        var targetArray = current as JsonArray ?? (parentObject?[lastSegment] as JsonArray);
                        if (targetArray == null) throw new InvalidOperationException("append 失败：目标不是数组（path 应指向数组）");
                        if (value == null) throw new InvalidOperationException("append 需要 value");
                        targetArray.Add(value);
                        break;
                    }

                    case "insert-before":
                    case "insert-after":
                    {
                        var targetArray = current as JsonArray ?? (parentObject?[lastSegment] as JsonArray);
                        if (targetArray == null) throw new InvalidOperationException($"{op} 失败：目标不是数组");
                        if (value == null) throw new InvalidOperationException($"{op} 需要 value");
                        if (index is not int insertIndex || insertIndex < 0 || insertIndex > targetArray.Count)
                        {
                            throw new InvalidOperationException($"{op} 需要有效的 index（0~{targetArray.Count}）");
                        }
                        if (op == "insert-after") insertIndex++;
                        targetArray.Insert(insertIndex, value);
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"不支持的 op：'{op}'（支持 replace/set/add/remove/append/insert-before/insert-after）");
                }

                result.Status = "applied";
                result.Message = $"已应用：{result.Target}/{result.TargetId}/{path} {op}";
            }
            catch (Exception ex)
            {
                result.Status = "failed";
                result.Message = ex.Message;
            }
            results.Add(result);
        }
        return results;
    }

    // ==================== 存储差分恢复（草稿/发布版 → 编辑器状态） ====================

    /// <summary>
    /// 把数据库存储的层差分（GetLayer 返回的 Content：{文件名: {Name, Version, Diffs, Operation}}）
    /// 重新应用到编辑器状态——即"恢复该层的修改"。
    /// 用于 layer_update（在草稿上继续改）和 layer_publish（把草稿内容发布）。
    /// 对已生效的差分（目标已不存在等）容错跳过并记录。
    /// </summary>
    public static List<string> ApplyStoredDiffs(JsonObject appState, JsonObject storedContent)
    {
        var log = new List<string>();
        foreach (var (fileName, pageInfoNode) in storedContent)
        {
            if (pageInfoNode is not JsonObject pageInfo) continue;

            var operation = pageInfo["Operation"]?.GetValue<string>() ?? "Replace";
            var diffs = pageInfo["Diffs"] as JsonArray;

            // 定位该文件在编辑器状态中的对应节点
            var fileNode = GetEditorFileNode(appState, fileName, out var setBack, out var removeFile);

            switch (operation)
            {
                case "Replace":
                    if (fileNode == null)
                    {
                        log.Add($"跳过 [{fileName}]：编辑器状态中无此文件且无 Diffs 可应用");
                        continue;
                    }
                    if (diffs == null || diffs.Count == 0)
                    {
                        log.Add($"[{fileName}]：无差异（空层文件）");
                        continue;
                    }
                    foreach (var diff in diffs.OfType<JsonObject>())
                    {
                        ApplySingleDiff(fileNode, setBack, fileName, diff, log);
                    }
                    break;

                case "Add":
                    // 新增文件（通常是新 page）：Diffs[0].Value 是完整内容
                    var addValue = diffs?.OfType<JsonObject>().FirstOrDefault()?["Value"]?.DeepClone();
                    if (addValue != null && setBack != null)
                    {
                        setBack(addValue);
                        log.Add($"[{fileName}]：已添加（Add）");
                    }
                    else
                    {
                        log.Add($"跳过 [{fileName}]：Add 操作缺少 Value 或目标容器");
                    }
                    break;

                case "Remove":
                    if (removeFile != null)
                    {
                        removeFile();
                        log.Add($"[{fileName}]：已移除（Remove）");
                    }
                    else
                    {
                        log.Add($"跳过 [{fileName}]：无法执行 Remove（文件不存在或为核心七件套）");
                    }
                    break;

                default:
                    log.Add($"跳过 [{fileName}]：未知 Operation '{operation}'");
                    break;
            }
        }
        return log;
    }

    /// <summary>获取文件在编辑器状态中的节点。返回 (节点, 写回委托, 移除委托)</summary>
    private static JsonNode? GetEditorFileNode(JsonObject appState, string fileName, out Action<JsonNode>? setBack, out Action? removeFile)
    {
        setBack = null;
        removeFile = null;

        // 七件套（PascalCase 响应 → 编辑器字段）
        var coreMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["layout"] = "Layout",
            ["events"] = "Events",
            ["rules"] = "Rules",
            ["tools"] = "ToolBar",
            ["dataviews"] = "DataViews"
        };

        if (coreMap.TryGetValue(fileName, out var editorField))
        {
            var node = appState[editorField];
            var field = editorField;
            setBack = v => appState[field] = v;
            removeFile = () => { }; // 核心七件套不允许整体移除（运行时必需）
            return node;
        }

        // 其他文件名视为 page 名：在 Pages 数组中按 name 匹配
        if (appState["Pages"] is JsonArray pages)
        {
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i] is JsonObject page &&
                    string.Equals(page["name"]?.GetValue<string>(), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var index = i;
                    setBack = v => pages[index] = v;
                    removeFile = () => pages.RemoveAt(index);
                    return page;
                }
            }
        }

        // page 不存在（新增 page 场景）：提供添加回调
        setBack = v =>
        {
            if (v is not JsonObject newPage) return;
            if (newPage["name"] == null) newPage["name"] = fileName;
            var pagesArray = appState["Pages"] as JsonArray;
            if (pagesArray == null)
            {
                pagesArray = new JsonArray();
                appState["Pages"] = pagesArray;
            }
            pagesArray.Add(newPage);
        };
        return null;
    }

    /// <summary>应用单条存储差分。
    /// 坐标系规则（来自服务端 JsonDiff.GetDiffs 源码）：
    ///   - diff.Id 非空 → 逐元素 diff：Id 是组件/事件的 id，Path 相对该元素内部（层级结构中元素内部结构与扁平结构一致）
    ///   - diff.Id 为空 → 根属性 diff：Path 相对文件根（如 /navigation/dataSetId）
    ///   - Path 为空 → 整体替换：Value 即新内容（新增组件时 Value 是完整组件对象）</summary>
    private static void ApplySingleDiff(JsonNode fileNode, Action<JsonNode>? setBack, string fileName, JsonObject diff, List<string> log)
    {
        var id = diff["Id"]?.GetValue<string>();
        var path = diff["Path"]?.GetValue<string>();
        var op = diff["Operation"]?.GetValue<string>() ?? "Replace";
        var value = diff["Value"]?.DeepClone();
        var index = diff["Index"];

        // ---------- 逐元素 diff：按 Id 定位目标元素 ----------
        if (!string.IsNullOrEmpty(id))
        {
            var (target, parentArray) = LocateByIdWithParent(fileNode, id);

            if (target == null)
            {
                // 目标不存在：Add 操作时 Value 是完整新元素（组件按 parentId 挂载）
                if (op is "Add" or "Replace" && value != null)
                {
                    AddTokenById(fileNode, value, log, fileName);
                }
                else
                {
                    log.Add($"跳过 [{fileName}] {op} id={id}：目标元素不存在（可能已被其他层移除）");
                }
                return;
            }

            switch (op)
            {
                case "Remove":
                    if (parentArray != null)
                    {
                        var removeIndex = IndexOfNode(parentArray, target);
                        if (removeIndex >= 0) parentArray.RemoveAt(removeIndex);
                        log.Add($"[{fileName}]：移除元素 id={id}");
                    }
                    else
                    {
                        // 组件树中的组件：从其父组件的 components 数组移除
                        RemoveFromComponentTree(fileNode, id, log, fileName);
                    }
                    return;

                case "Add":
                case "Replace":
                default:
                    if (string.IsNullOrEmpty(path) || path == "/")
                    {
                        // 无 Path：Value 是该元素的完整新内容 → 整体替换
                        if (value != null)
                        {
                            if (parentArray != null)
                            {
                                var replaceIndex = IndexOfNode(parentArray, target);
                                if (replaceIndex >= 0) parentArray[replaceIndex] = value;
                            }
                            else
                            {
                                ReplaceInComponentTree(fileNode, id, value);
                            }
                            log.Add($"[{fileName}]：整体替换元素 id={id}");
                        }
                        return;
                    }
                    // 有 Path：在元素内部导航应用（相对元素）
                    ApplyPathOperation(target, path, op, value, index, fileName, id, log);
                    return;
            }
        }

        // ---------- 根属性 diff：Path 相对文件根 ----------
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            // 整个文件替换
            if (setBack != null && value != null)
            {
                setBack(value);
                log.Add($"[{fileName}]：整体替换（无 Path 无 Id）");
            }
            return;
        }
        ApplyPathOperation(fileNode, path, op, value, index, fileName, "", log);
    }

    /// <summary>在目标节点内按 path 应用单个操作（path 相对 target）</summary>
    private static void ApplyPathOperation(JsonNode target, string path, string op, JsonNode? value, JsonNode? index, string fileName, string id, List<string> log)
    {
        try
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = target;

            // 导航到父节点（倒数第二段）
            for (var s = 0; s < segments.Length - 1; s++)
            {
                current = NavigateStep(current, segments[s]);
                if (current == null)
                {
                    log.Add($"跳过 [{fileName}] {op} id={id} {path}：父路径不存在");
                    return;
                }
            }

            var lastSegment = segments[^1];

            if (current is JsonObject parentObj)
            {
                switch (op)
                {
                    case "Remove":
                        parentObj.Remove(lastSegment);
                        break;
                    default: // Replace / Add：值是绝对值，直接设置
                        if (value != null) parentObj[lastSegment] = value;
                        break;
                }
                log.Add($"[{fileName}]：{op} id={id} {path}");
            }
            else if (current is JsonArray parentArr)
            {
                if (int.TryParse(lastSegment, out var arrIndex))
                {
                    switch (op)
                    {
                        case "Remove" when arrIndex < parentArr.Count:
                            parentArr.RemoveAt(arrIndex);
                            break;
                        default:
                            if (arrIndex < parentArr.Count && value != null) parentArr[arrIndex] = value;
                            else if (value != null) parentArr.Add(value);
                            break;
                    }
                    log.Add($"[{fileName}]：{op} id={id} {path}(index={arrIndex})");
                }
                else if (index?.GetValue<int>() is int idx)
                {
                    switch (op)
                    {
                        case "Remove" when idx < parentArr.Count:
                            parentArr.RemoveAt(idx);
                            break;
                        default:
                            if (idx < parentArr.Count && value != null) parentArr[idx] = value;
                            else if (value != null) parentArr.Add(value);
                            break;
                    }
                    log.Add($"[{fileName}]：{op} id={id} {path}(index={idx})");
                }
            }
        }
        catch (Exception ex)
        {
            log.Add($"跳过 [{fileName}] {op} id={id} {path}：{ex.Message}");
        }
    }

    /// <summary>按 id 定位元素：数组文件（events/rules/tools/dataviews）返回元素及其所在数组；layout 返回组件及其父数组（父组件的 components）</summary>
    private static (JsonNode? target, JsonArray? parentArray) LocateByIdWithParent(JsonNode fileNode, string id)
    {
        if (fileNode is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                if (string.Equals(item["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                {
                    return (item, array);
                }
            }
            return (null, null);
        }

        if (fileNode is JsonObject obj)
        {
            // 文件根本身带匹配的 id
            if (string.Equals(obj["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            {
                return (obj, null);
            }
            // layout：在组件树 DFS（返回父容器数组便于整体替换/删除）
            if (obj["components"] is JsonArray rootComponents)
            {
                var (found, parent) = FindComponentWithParent(rootComponents, id);
                if (found != null) return (found, parent);
            }
        }
        return (null, null);
    }

    /// <summary>组件树 DFS：返回 (匹配组件, 其所在数组)</summary>
    private static (JsonObject? found, JsonArray? parent) FindComponentWithParent(JsonArray components, string id)
    {
        foreach (var component in components.OfType<JsonObject>())
        {
            if (string.Equals(component["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            {
                return (component, components);
            }
            if (component["components"] is JsonArray children)
            {
                var (found, parent) = FindComponentWithParent(children, id);
                if (found != null) return (found, parent);
            }
        }
        return (null, null);
    }

    /// <summary>新增元素：数组文件直接追加；layout 的组件按 parentId 挂载到对应父组件</summary>
    private static void AddTokenById(JsonNode fileNode, JsonNode value, List<string> log, string fileName)
    {
        if (fileNode is JsonArray arr)
        {
            arr.Add(value);
            log.Add($"[{fileName}]：追加新元素");
            return;
        }
        if (fileNode is JsonObject layout)
        {
            if (value is not JsonObject newComponent) return;
            var parentId = newComponent["parentId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(parentId))
            {
                var rootComponents = layout["components"] as JsonArray;
                if (rootComponents == null)
                {
                    rootComponents = new JsonArray();
                    layout["components"] = rootComponents;
                }
                rootComponents.Add(newComponent);
                log.Add($"[{fileName}]：新增根组件 id={newComponent["id"]}");
            }
            else
            {
                var (parent, _) = FindComponentWithParent(layout["components"] as JsonArray ?? new JsonArray(), parentId);
                if (parent != null)
                {
                    var children = parent["components"] as JsonArray;
                    if (children == null)
                    {
                        children = new JsonArray();
                        parent["components"] = children;
                    }
                    children.Add(newComponent);
                    log.Add($"[{fileName}]：新增子组件 id={newComponent["id"]}（挂到 parentId={parentId}）");
                }
                else
                {
                    // 父组件找不到：退而挂到根（保留组件不丢失）
                    var fallbackComponents = layout["components"] as JsonArray;
                    if (fallbackComponents == null)
                    {
                        fallbackComponents = new JsonArray();
                        layout["components"] = fallbackComponents;
                    }
                    fallbackComponents.Add(newComponent);
                    log.Add($"[{fileName}]：新增组件 id={newComponent["id"]}（原父组件不存在，已挂到根）");
                }
            }
        }
    }

    /// <summary>从组件树中移除指定 id 的组件</summary>
    private static void RemoveFromComponentTree(JsonNode fileNode, string id, List<string> log, string fileName)
    {
        if (fileNode is not JsonObject layout || layout["components"] is not JsonArray rootComponents) return;
        if (TryRemoveComponent(rootComponents, id))
        {
            log.Add($"[{fileName}]：移除组件 id={id}");
        }
        else
        {
            log.Add($"跳过 [{fileName}] Remove id={id}：组件树中未找到");
        }
    }

    private static bool TryRemoveComponent(JsonArray components, string id)
    {
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is JsonObject component &&
                string.Equals(component["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            {
                components.RemoveAt(i);
                return true;
            }
            if (components[i] is JsonObject withChildren && withChildren["components"] is JsonArray children &&
                TryRemoveComponent(children, id))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>在组件树中替换指定 id 的组件</summary>
    private static void ReplaceInComponentTree(JsonNode fileNode, string id, JsonNode value)
    {
        if (fileNode is not JsonObject layout || layout["components"] is not JsonArray rootComponents) return;
        ReplaceComponent(rootComponents, id, value);
    }

    private static bool ReplaceComponent(JsonArray components, string id, JsonNode value)
    {
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is JsonObject component &&
                string.Equals(component["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            {
                components[i] = value;
                return true;
            }
            if (components[i] is JsonObject withChildren && withChildren["components"] is JsonArray children &&
                ReplaceComponent(children, id, value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>求数组中某节点的索引（引用比较）</summary>
    private static int IndexOfNode(JsonArray array, JsonNode node)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (ReferenceEquals(array[i], node)) return i;
        }
        return -1;
    }

    /// <summary>path 单段导航（对象属性或数组索引）</summary>
    private static JsonNode? NavigateStep(JsonNode? node, string segment)
    {
        if (node == null) return null;
        if (node is JsonObject obj) return obj[segment];
        if (node is JsonArray arr && int.TryParse(segment, out var index) && index >= 0 && index < arr.Count)
        {
            return arr[index];
        }
        return null;
    }

    // ==================== 保存请求构造 ====================

    /// <summary>
    /// 把编辑器状态（GetApp 的 PascalCase 响应）转换为 SaveApp 请求的 camelCase 七件套。
    /// 关键安全点：pages 必须有值（哪怕是空数组）——缺失会导致服务端把基础层所有 page 标记为 Remove！
    /// </summary>
    public static JsonObject BuildSaveRequest(JsonObject appState, string viewId, string layerName,
        string description, string commentText, bool wip, bool isNew, string typeCode = "KNTCCustLayer", string deviceType = "Desktop")
    {
        return new JsonObject
        {
            ["id"] = viewId,
            ["applicationType"] = "view",
            ["deviceType"] = deviceType,
            ["commentText"] = commentText,
            ["uxAppVersion"] = 0,
            ["layerInfo"] = new JsonObject
            {
                ["company"] = "",           // 全局层（Company 空串，实测必需——传 null 会导致匹配失败）
                ["typeCode"] = typeCode,
                ["layerName"] = layerName,
                ["layerDescription"] = description,
                ["parentLayers"] = new JsonArray(),
                ["wip"] = wip,
                ["IsNew"] = isNew,
                ["CGCCode"] = "",
                ["PublishParentLayers"] = false
            },
            // GetApp 响应（PascalCase）→ SaveApp 请求（camelCase）的字段映射
            ["layout"] = appState["Layout"]?.DeepClone(),
            ["events"] = appState["Events"]?.DeepClone(),
            ["rules"] = appState["Rules"]?.DeepClone(),
            ["tools"] = appState["ToolBar"]?.DeepClone(),
            ["dataviews"] = appState["DataViews"]?.DeepClone(),
            // ★ pages/orphans 必须非 null：null/缺失会被服务端判定为"删除全部页面"
            ["pages"] = appState["Pages"]?.DeepClone() as JsonArray ?? new JsonArray(),
            ["orphans"] = appState["Orphans"]?.DeepClone() as JsonArray ?? new JsonArray()
        };
    }

    // ==================== 保存后差分回执 ====================

    /// <summary>
    /// 把 SaveApp 后从服务端读回的存储差分（GetLayer 的 Content）解析成紧凑回执：
    /// 每个文件的 Operation、Diffs 数量、变更路径列表——供 AI 闭环核验改动是否符合预期。
    /// </summary>
    public static JsonArray BuildDiffReceipt(JsonObject? storedLayer)
    {
        var receipt = new JsonArray();
        if (storedLayer?["Content"] is not JsonObject content) return receipt;

        foreach (var (fileName, pageInfoNode) in content)
        {
            if (pageInfoNode is not JsonObject pageInfo) continue;

            var entry = new JsonObject
            {
                ["file"] = fileName,
                ["operation"] = pageInfo["Operation"]?.GetValue<string>(),
                ["diffCount"] = (pageInfo["Diffs"] as JsonArray)?.Count ?? 0,
                ["paths"] = new JsonArray()
            };
            if (pageInfo["Diffs"] is JsonArray diffs && entry["paths"] is JsonArray paths)
            {
                foreach (var diff in diffs.OfType<JsonObject>())
                {
                    paths.Add($"{diff["Operation"]?.GetValue<string>()} {diff["Path"]?.GetValue<string>()}");
                }
            }
            receipt.Add(entry);
        }
        return receipt;
    }
}
