
namespace ErpDecompilerAgenticRAG_Mcp.Utilities;
using Models;
using ICSharpCode;
using ICSharpCode.Decompiler.TypeSystem;
using System.Text;

public static class ErpHelper
{
    public static bool ContainsInvalidPathCharacters(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // 检查路径注入攻击模式
        var dangerousPatterns = new[]
        {
            "..",           // 目录遍历
            "~",            // Home目录引用
            "\\",           // 反斜杠（在Linux上）
            "//",           // 双斜杠
            "|",            // 管道符
            "<",            // 重定向符
            ">",            // 重定向符
            "*",            // 通配符（在某些上下文中）
            "?",            // 通配符
            "$",            // 环境变量引用
            "%",            // 环境变量引用
            "\0",           // 空字符
            "\n",           // 换行符
            "\r"            // 回车符
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (input.Contains(pattern))
            {
                return true;
            }
        }

        // 检查是否包含可疑的URL编码或编码字符
        if (input.Contains("%2e") || input.Contains("%25") || input.Contains("%00"))
        {
            return true;
        }

        return false;
    }

    public static Models.TypeKind ConvertToTypeKind(ICSharpCode.Decompiler.TypeSystem.TypeKind ilspyKind)
    {
        if (ilspyKind is ICSharpCode.Decompiler.TypeSystem.TypeKind.Class)
            return Models.TypeKind.Class;
        if (ilspyKind is ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface)
            return Models.TypeKind.Interface;
        if (ilspyKind is ICSharpCode.Decompiler.TypeSystem.TypeKind.Enum)
            return Models.TypeKind.Enum;
        if (ilspyKind is ICSharpCode.Decompiler.TypeSystem.TypeKind.Struct)
            return Models.TypeKind.Struct;
        return Models.TypeKind.Unknown;
    }
    /// 清理文件名中的非法字符
    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder();

        foreach (var c in fileName)
        {
            if (invalidChars.Contains(c))
            {
                sanitized.Append('_');
            }
            else
            {
                sanitized.Append(c);
            }
        }
        return sanitized.ToString();
    }

    public static string GetCodeFilePath(string cacheFolder, string typeName)
    {
        // 按命名空间组织文件夹结构，例如: Erp.BO.SalesOrder -> Erp/BO/SalesOrder.cs
        var parts = typeName.Split('.');
        var namespacePath = parts.Take(parts.Length - 1).ToArray();
        var className = parts.Last();

        if (namespacePath.Length > 0)
        {
            return Path.Combine(cacheFolder, Path.Combine(namespacePath), $"{className}.cs");
        }
        else
        {
            return Path.Combine(cacheFolder, $"{className}.cs");
        }
    }




}
