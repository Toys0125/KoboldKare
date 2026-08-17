using UnityEngine;
using System;
using System.IO;
#if UNITY_EDITOR
using UnityEditor.Build.Reporting;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor;
//using UnityEngine.AddressableAssets/KoboldKare.Initialization;
//using UnityEngine.Localization;

public class Build {
    
    static readonly string[] scenes = {"Assets/KoboldKare/Scenes/AddressableLoadingScene.unity"};
    private static string outputDirectory {
        get {
            string dir = Environment.GetEnvironmentVariable("BUILD_DIR");
            if (string.IsNullOrWhiteSpace(dir)) {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) {
                    if (args[i] == "-logFile") {
                        string logDirectory = Path.GetDirectoryName(Path.GetFullPath(args[i + 1]));
                        if (!string.IsNullOrEmpty(logDirectory)) {
                            dir = Path.Combine(logDirectory, "player");
                        }
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(dir)) {
                dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Build");
            }
            Directory.CreateDirectory(dir);
            return string.Format("{0}{1}", dir.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar);
        }
    }

    private static int ResultToExitCode(BuildResult result) {
        switch (result) {
            case BuildResult.Succeeded: return 0;
            case BuildResult.Failed: return 1; 
            case BuildResult.Unknown: return 2;
            case BuildResult.Cancelled: return 3;
            default: return 4;
        }
    }

    public static void BuildLinux() {
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.SplashScreen.logos = Array.Empty<PlayerSettings.SplashScreenLogo>();
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "CopyPDBFiles", GetIsDevBuild() ? "true" : "false");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone,BuildTarget.StandaloneLinux64);
        AddressableAssetSettings.BuildPlayerContent();
        GetBuildVersion();
        string output = $"{outputDirectory}KoboldKare";
        Debug.Log($"#### BUILDING TO {output}####");
        var report = BuildPipeline.BuildPlayer(scenes, output, BuildTarget.StandaloneLinux64, GetBuildOptions());
        Debug.Log("#### BUILD DONE ####");
        Debug.Log(report.summary);
        EditorApplication.Exit(ResultToExitCode(report.summary.result));
    }

    public static void BuildWindows() {
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.SplashScreen.logos = Array.Empty<PlayerSettings.SplashScreenLogo>();
        EditorUserBuildSettings.SetPlatformSettings("Standalone", "CopyPDBFiles", GetIsDevBuild() ? "true" : "false");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone,BuildTarget.StandaloneWindows64);
        AddressableAssetSettings.BuildPlayerContent();
        GetBuildVersion();
        string output = $"{outputDirectory}KoboldKare.exe";
        Debug.Log($"#### BUILDING TO {output}####");
        var report = BuildPipeline.BuildPlayer(scenes, output, BuildTarget.StandaloneWindows64, GetBuildOptions());
        Debug.Log("#### BUILD DONE ####");
        Debug.Log(report.summary);
        EditorApplication.Exit(ResultToExitCode(report.summary.result));
    }

    private static BuildOptions GetBuildOptions() {
        return GetIsDevBuild() ? BuildOptions.Development : BuildOptions.None;
    }

    private static bool GetIsDevBuild() {
        var development_build_env = Environment.GetEnvironmentVariable("DEVELOPMENT_BUILD");
        bool dev_build = development_build_env != null && development_build_env != "False" && development_build_env != "false";
        return dev_build;
    }
    
    private static void GetBuildVersion() {
        string version = Environment.GetEnvironmentVariable("BUILD_NUMBER");
        string date = DateTime.Now.ToString("MM.dd.yyyy");
        string gitcommit = Environment.GetEnvironmentVariable("GIT_COMMIT")?.Substring(0,8); 
        if (!String.IsNullOrEmpty(version) && !String.IsNullOrEmpty(gitcommit)) {
            PlayerSettings.bundleVersion = $"{date}_{gitcommit}";
        } else if (!String.IsNullOrEmpty(version)) {
            PlayerSettings.bundleVersion = version;
        }
    }
}

#endif
