# ConfGenTool

迁移自 Unity 的配置表工具，提供 **.NET 原生运行** 的命令行/库能力与 **Unity UPM 编辑器包**，方便在 **Unity 客户端**、**战斗服** 或 **原生 C# 项目** 中使用同一套配置表生成流程与产物（强类型 C# 代码 + MemoryPack 二进制数据）。

> Unity 原版参考：<br>
> [yuki-uitest（Gitee）](https://gitee.com/zxjun2002/yuki-uitest)

> 配套工具：（目前生成设置全面转向 json，因此搭配该工具使用）<br>
> [schema-studio（Gitee）](https://gitee.com/zxjun2002/schema-studio)

---

## ✨ 特性

- **双端同源发布**：推送版本 Tag 自动通过 GitHub Actions 同时发布 **NuGet Tool**、**Unity UPM（`upm` 分支 / Tag）** 及 **GitHub Releases 离线包**。
- **跨平台 .NET 原生运行**：无需 Unity 环境即可在命令行/CI 生成代码与二进制数据。
- **Unity Editor 开箱即用**：内置 `netstandard2.1` Editor 专用 DLL 与可视化菜单窗口，策划/美术电脑无需安装 `.NET 8 SDK`。
- **多格式输入**：支持 `.csv / .xlsx / .xls`（可选多 Sheet）。
- **强类型代码生成 & 二进制导出**：生成 `ConfData.cs`、`RacastSet` 与 MemoryPack 二进制 `.bytes`。

---

## 📦 安装

### 1. 纯 C# / 命令行（NuGet Global Tool）

```bash
# 安装
dotnet tool install --global zxjun2002.ConfGen.Tool

# 更新
dotnet tool update --global zxjun2002.ConfGen.Tool
```

### 2. Unity（Package Manager / UPM）

打开 Unity -> **Window -> Package Manager -> `+` -> Add package from git URL...**：

- 跟踪最新版本：
  ```text
  https://github.com/zxjun2002/ConfGenTool.git#upm
  ```
- 锁定指定版本（例如 `1.0.11`）：
  ```text
  https://github.com/zxjun2002/ConfGenTool.git#upm-v1.0.11
  ```

或者前往 [GitHub Releases](https://github.com/zxjun2002/ConfGenTool/releases) 手动下载 `com.zxjun2002.confgen-x.y.z.tgz`，在 Package Manager 中选择 **Add package from tarball...** 导入。

---

## 🚀 自动发版（CI）

维护者推送 `v*.*.*` 格式的 Tag 即可触发自动打包并同步发布到 NuGet、UPM 与 GitHub Releases：

```bash
git tag v1.0.11
git push origin v1.0.11
```
