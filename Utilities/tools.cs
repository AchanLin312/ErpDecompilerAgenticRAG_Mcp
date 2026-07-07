
﻿namespace ErpDecompilerAgenticRAG_Mcp.Utilities;

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
}
