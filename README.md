# ConfGenTool

迁移自 Unity 的配置表工具，提供 **.NET 原生运行** 的命令行/库能力，方便在 **战斗服** 或 **原生 C# 项目** 中使用同一套配置表生成流程与产物（强类型 C# 代码 + 二进制数据）。

> Unity 原版参考：  
> [yuki-uitest（Gitee）](https://gitee.com/zxjun2002/yuki-uitest)
> 配套工具：（目前生成设置全面转向json，因此搭配该工具使用）  
> [schema-studio（Gitee）](https://gitee.com/zxjun2002/schema-studio)

---

## ✨ 特性

- **跨平台 .NET 原生运行**：无需 Unity 环境即可生成代码与二进制数据。
- **多格式输入**：支持 `.csv / .xlsx / .xls`（可选多 Sheet）。
- **强类型代码生成**：根据表头/类型规则生成对应的 C# 类与聚合入口（例如 `ConfData`）。
- **数据导出**：导出二进制（`.bytes`），便于运行时快速加载与最小化体积。
- **Unity / 服务器 共用**：同一产物同时被 Unity 客户端与战斗服使用，逻辑与数据口径保持一致。

---

## 📦 安装

> 假设你已经在本机 `dotnet pack` 得到 `.nupkg`。

```bash
# 1) 打包（仓库根目录）
dotnet pack -c Release

# 2) 安装为全局工具（从本地 nupkg 源）
dotnet tool install --global Yuki.ConfGen.Tool --add-source ./nupkg

# （更新）
dotnet tool update --global Yuki.ConfGen.Tool --add-source ./nupkg
