using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Yuki.ConfGenTool.Editor
{
    [InitializeOnLoad]
    public static class ConfGenUnityBridge
    {
        static ConfGenUnityBridge()
        {
            BindLogger();
        }

        public static void BindLogger()
        {
            global::ConfGen.OnLog = msg => Debug.Log(msg);
            global::ConfGen.OnWarn = msg => Debug.LogWarning(msg);
            global::ConfGen.OnError = msg => Debug.LogError(msg);
        }
    }

    public sealed class ConfGenEditorWindow : EditorWindow
    {
        private const string PrefInputDir = "Yuki.ConfGen.InputDir";
        private const string PrefCodeDir = "Yuki.ConfGen.CodeDir";
        private const string PrefBytesDir = "Yuki.ConfGen.BytesDir";
        private const string PrefSideIndex = "Yuki.ConfGen.SideIndex";

        private static readonly string[] Sides = { "C", "S", "ALL", "NONE" };

        private string _inputDir;
        private string _codeDir;
        private string _bytesDir;
        private int _sideIndex;

        [MenuItem("Tools/Yuki ConfGen/Config Generator")]
        public static void ShowWindow()
        {
            var win = GetWindow<ConfGenEditorWindow>("Yuki ConfGen");
            win.minSize = new Vector2(460f, 220f);
            win.Show();
        }

        private void OnEnable()
        {
            _inputDir = EditorPrefs.GetString(PrefInputDir, "Configs");
            _codeDir = EditorPrefs.GetString(PrefCodeDir, "Assets/Scripts/Generated/Conf");
            _bytesDir = EditorPrefs.GetString(PrefBytesDir, "Assets/Resources/Conf");
            _sideIndex = Mathf.Clamp(EditorPrefs.GetInt(PrefSideIndex, 0), 0, Sides.Length - 1);
            ConfGenUnityBridge.BindLogger();
        }

        private void OnDisable()
        {
            SavePrefs();
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefInputDir, _inputDir ?? string.Empty);
            EditorPrefs.SetString(PrefCodeDir, _codeDir ?? string.Empty);
            EditorPrefs.SetString(PrefBytesDir, _bytesDir ?? string.Empty);
            EditorPrefs.SetInt(PrefSideIndex, _sideIndex);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Yuki ConfGen 配置表工具", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            _inputDir = DrawFolderField("配置表目录 (Input)", _inputDir);
            _codeDir = DrawFolderField("代码输出目录 (Code)", _codeDir);
            _bytesDir = DrawFolderField("二进制输出目录 (Bytes)", _bytesDir);
            _sideIndex = EditorGUILayout.Popup("导出侧别 (Side)", _sideIndex, Sides);

            EditorGUILayout.Space(12f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("1. 生成 C# 代码", GUILayout.Height(32f)))
                {
                    RunGenerateCode();
                }

                if (GUILayout.Button("2. 导出二进制 (.bytes)", GUILayout.Height(32f)))
                {
                    RunBuildBinary();
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "提示：首次修改表结构后，请先点击「生成 C# 代码」，待 Unity 编译完成后再点击「导出二进制 (.bytes)」。",
                MessageType.Info);
        }

        private static string DrawFolderField(string label, string current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                current = EditorGUILayout.TextField(label, current);
                if (GUILayout.Button("选择", GUILayout.Width(56f)))
                {
                    var selected = EditorUtility.OpenFolderPanel(label, current, string.Empty);
                    if (!string.IsNullOrEmpty(selected))
                    {
                        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                        var fullSelected = Path.GetFullPath(selected);
                        if (fullSelected.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            current = fullSelected.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        }
                        else
                        {
                            current = fullSelected;
                        }
                    }
                }
            }
            return current;
        }

        private void RunGenerateCode()
        {
            SavePrefs();
            ConfGenUnityBridge.BindLogger();
            try
            {
                var outCs = Path.Combine(_codeDir, "ConfData.cs");
                var outRacast = Path.Combine(_codeDir, "RacastSet");
                global::ConfGen.GenerateCode(_inputDir, outCs, outRacast, Sides[_sideIndex]);
                AssetDatabase.Refresh();
                Debug.Log("[ConfGen] 代码生成完成。");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void RunBuildBinary()
        {
            SavePrefs();
            ConfGenUnityBridge.BindLogger();
            try
            {
                var outBytes = Path.Combine(_bytesDir, "ConfData.bytes");
                global::ConfGen.BuildBinary(_inputDir, outBytes);
                AssetDatabase.Refresh();
                Debug.Log("[ConfGen] 二进制导出完成。");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
