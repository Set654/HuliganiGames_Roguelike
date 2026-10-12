#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HuliganiGamesWindowsBuild
{
    [MenuItem("HuliganiGames/Build Final Windows x64")]
    public static void Build()
    {
        if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("Wait until Unity has finished compiling and stop Play Mode.");
            return;
        }

        string outputFolder = Path.GetFullPath("Builds/Windows-x64");
        Directory.CreateDirectory(outputFolder);
        string exePath = Path.Combine(outputFolder, "HuliganiGames.exe");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/MainMenu.unity", "Assets/Scenes/Game.unity" },
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Windows build failed. Open the Unity Console and Editor.log for errors.");

        Debug.Log("[HuliganiGames] Windows x64 release built: " + exePath +
                  " (" + report.summary.totalSize + " bytes)");
    }
}
#endif
