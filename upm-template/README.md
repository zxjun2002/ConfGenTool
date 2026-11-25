# Yuki ConfGen (Unity UPM)

Unity Editor 端原生配置表生成包，支持读取 `.csv / .xlsx / .xls` 与 `*.schema.json`，直接生成 `ConfData.cs`、`RacastSet` 及 `MemoryPack` 二进制文件 `ConfData.bytes`。

> 所有内置 DLL 均已限定为 **Editor 专用**，不会打入运行时游戏包体。

## 安装方式

### 方式一：通过 Git URL 安装（推荐）

打开 Unity -> **Window -> Package Manager -> `+` -> Add package from git URL...**：

- 跟踪最新版本：
  ```text
  https://github.com/zxjun2002/ConfGenTool.git#upm
  ```
- 锁定指定版本（以 `v1.0.11` 为例）：
  ```text
  https://github.com/zxjun2002/ConfGenTool.git#v1.0.11-upm
  ```

### 方式二：从 GitHub Releases 手动下载安装

1. 在 [GitHub Releases](https://github.com/zxjun2002/ConfGenTool/releases) 下载对应版本的 `com.zxjun2002.confgen-x.y.z.tgz`。
2. 打开 Unity -> **Window -> Package Manager -> `+` -> Add package from tarball...** 选择下载的 `.tgz` 文件即可。

## 前置依赖

- 本包已通过 UPM 自动依赖 `com.unity.nuget.newtonsoft-json`。
- 你的 Unity 项目需已安装 **MemoryPack**（用于编译生成的 `[MemoryPackable]` `ConfData.cs` 并在运行时反序列化 `ConfData.bytes`）。

## 使用方法

### 1. 使用可视化窗口

打开 Unity 顶部菜单：**Tools -> Yuki ConfGen -> Config Generator**，配置好表目录与输出目录后点击按钮即可：
- **1. 生成 C# 代码**：调用 `ConfGen.GenerateCode(...)` 生成 `ConfData.cs` 与 `RacastSet`。
- **2. 导出二进制数据**：等待 Unity 编译完新生成的 `ConfData.cs` 后，调用 `ConfGen.BuildBinary(...)` 导出 `ConfData.bytes`。

### 2. 在自己的 Editor 脚本中直接调用 API

```csharp
using System.IO;
using UnityEditor;

public static class MyConfMenu
{
    [MenuItem("Tools/Build My Configs")]
    public static void Build()
    {
        ConfGen.GenerateCode("Configs", "Assets/Scripts/Generated/ConfData.cs", "Assets/Scripts/Generated/RacastSet", "C");
        AssetDatabase.Refresh();
    }

    [MenuItem("Tools/Export My Bytes")]
    public static void ExportBytes()
    {
        ConfGen.BuildBinary("Configs", "Assets/Resources/ConfData.bytes");
        AssetDatabase.Refresh();
    }
}
```
