---
name: "epicor-database-docs"
description: "访问 Epicor Kinetic 数据库表的官方帮助文档。当用户询问 Epicor 数据库表结构、字段描述、表之间的关系、或需要查阅 Epicor 数据字典时调用此 skill。"
---

# Epicor 数据库文档访问

## 文档位置

```
C:\Users\ssi-LinYaoChen\Desktop\学习文件\Kinetic 2023.1 Extended.chm
```

## 查看方法

CHM 是编译帮助文件，需先解压为 HTML 再搜索：

```powershell
# 解压（已解压过可跳过）
hh.exe -decompile "C:\Users\ssi-LinYaoChen\Desktop\学习文件\Kinetic2023_1_Extended" "C:\Users\ssi-LinYaoChen\Desktop\学习文件\Kinetic 2023.1 Extended.chm"
```

解压后用 Grep 搜索表名或字段名，再用 Read 读取对应的 HTML 文件。
