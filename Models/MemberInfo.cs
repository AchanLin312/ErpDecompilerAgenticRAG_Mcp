namespace ErpDecompilerAgenticRAG_Mcp.Models;

/// <summary>
/// 成员类型枚举
/// </summary>
public enum MemberKind
{
    Method,
    Property,
    Field,
    Event
}

/// <summary>
/// 访问修饰符枚举
/// </summary>
public enum Visibility
{
    Public,
    Internal,
    Private,
    Protected,
    Unknown
}

/// <summary>
/// 类型成员信息（方法、属性、字段）
/// </summary>
public class MemberInfo
{
    /// <summary>
    /// 成员名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 成员类型（Method, Property, Field）
    /// </summary>
    public MemberKind Kind { get; set; } = MemberKind.Method;

    /// <summary>
    /// 访问修饰符（Public, Internal, Private, Protected）
    /// </summary>
    public Visibility Visibility { get; set; } = Visibility.Unknown;

    /// <summary>
    /// 方法签名（完整声明，不含方法体）
    /// 例如: "public void GetByID(string id)"
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// 返回类型（仅方法有效）
    /// </summary>
    public string? ReturnType { get; set; }

    /// <summary>
    /// 参数列表（仅方法有效）
    /// 例如: ["string id", "int count"]
    /// </summary>
    public List<string> Parameters { get; set; } = new();

    /// <summary>
    /// 属性类型（仅属性有效）
    /// </summary>
    public string? PropertyType { get; set; }

    /// <summary>
    /// 字段类型（仅字段有效）
    /// </summary>
    public string? FieldType { get; set; }

    /// <summary>
    /// 是否静态成员
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// 是否虚方法/抽象方法
    /// </summary>
    public bool IsVirtual { get; set; }

    /// <summary>
    /// 是否抽象方法
    /// </summary>
    public bool IsAbstract { get; set; }

    /// <summary>
    /// 避免冲突的额外信息
    /// </summary>
    public string? AdditionalInfo { get; set; }
}

/// <summary>
/// 类型成员查询结果
/// </summary>
public class TypeMembersResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 类型全名
    /// </summary>
    public string TypeFullName { get; set; } = string.Empty;

    /// <summary>
    /// 数据来源（cache/metadata）
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 方法列表
    /// </summary>
    public List<MemberInfo> Methods { get; set; } = new();

    /// <summary>
    /// 属性列表
    /// </summary>
    public List<MemberInfo> Properties { get; set; } = new();

    /// <summary>
    /// 字段列表
    /// </summary>
    public List<MemberInfo> Fields { get; set; } = new();

    /// <summary>
    /// 事件列表
    /// </summary>
    public List<MemberInfo> Events { get; set; } = new();

    /// <summary>
    /// 所有成员列表（合并）
    /// </summary>
    public List<MemberInfo> Members { get; set; } = new();

    /// <summary>
    /// 总成员数
    /// </summary>
    public int TotalCount { get; set; }
}