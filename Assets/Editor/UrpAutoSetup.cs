#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
public static class UrpAutoSetup
{
    private const string PipelineAssetPath = "Assets/Settings/URP/HuliganiGames_UniversalRenderPipeline.asset";
    private const string RendererAssetPath = "Assets/Settings/URP/HuliganiGames_ForwardRenderer.asset";

    static UrpAutoSetup()
    {
        EditorApplication.delayCall += EnsureUrp;
    }

    private static void EnsureUrp()
    {
        try
        {
            string folder = Path.GetDirectoryName(PipelineAssetPath).Replace("\\", "/");
            EnsureFolder(folder);

            UniversalRenderPipelineAsset pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);

            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, RendererAssetPath);
            }

            if (pipelineAsset == null)
            {
                pipelineAsset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                AssetDatabase.CreateAsset(pipelineAsset, PipelineAssetPath);
            }

            SerializedObject serialized = new SerializedObject(pipelineAsset);
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            if (list != null)
            {
                list.arraySize = 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = rendererData;
            }
            SerializedProperty defaultIndex = serialized.FindProperty("m_DefaultRendererIndex");
            if (defaultIndex != null)
                defaultIndex.intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipelineAsset);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Unity 6.6: assign the default pipeline and override it for the
            // currently selected quality level. The default asset is used by
            // other quality levels that have no explicit override.
            GraphicsSettings.defaultRenderPipeline = pipelineAsset;
            QualitySettings.renderPipeline = pipelineAsset;
            AssetDatabase.SaveAssets();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[HuliganiGames] URP auto-setup skipped: " + ex.Message);
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
#endif
