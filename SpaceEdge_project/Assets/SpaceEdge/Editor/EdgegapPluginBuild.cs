// Command-line entry point for the Edgegap plugin's "Build server" button (Tools > Edgegap Server Hosting).
// Same output: a Linux Dedicated Server build in Builds/EdgegapServer/ServerBuild, which the plugin's Dockerfile copies.
// Also builds the Windows test client. Usage (project closed in the Editor):
//   <Unity> -batchmode -projectPath <project> -executeMethod EdgegapPluginBuild.BuildServer -logFile build-server.log
//   <Unity> -batchmode -projectPath <project> -executeMethod EdgegapPluginBuild.BuildClient -logFile build-client.log
#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class EdgegapPluginBuild
{
    public static void BuildServer()
    {
        // EdgegapBuildUtils is internal to the plugin assembly.
        Type utils = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Edgegap.EdgegapBuildUtils"))
            .FirstOrDefault(t => t != null);
        if (utils == null)
        {
            Debug.LogError("EdgegapPluginBuild | Edgegap plugin (com.edgegap.unity-servers-plugin) not installed.");
            EditorApplication.Exit(1);
            return;
        }

        var report = (BuildReport)utils.GetMethod("BuildServer").Invoke(null, new object[] { "EdgegapServer" });
        Debug.Log($"EdgegapPluginBuild | {report.summary.result}: {report.summary.outputPath}");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>
    /// Windows test client in Builds/Client/win64/Game.exe (in the project folder). The plugin's server build leaves
    /// the Standalone subtarget on Dedicated Server, so a plain -buildWindows64Player would try a Windows server build.
    /// </summary>
    public static void BuildClient()
    {
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            locationPathName = "Builds/Client/win64/Game.exe",
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
        });
        Debug.Log($"EdgegapPluginBuild | {report.summary.result}: {report.summary.outputPath}");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
#endif
