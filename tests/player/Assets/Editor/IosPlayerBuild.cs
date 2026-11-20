using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class IosPlayerBuild
{
    const string Scene = "Assets/Player/Player.unity";

    // -executeMethod IosPlayerBuild.Build -ugoXcode <dir> [-ugoDevice]: Xcode project for the iOS simulator (arm64),
    // or for a device (iphoneos) with -ugoDevice.
    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = args[Array.IndexOf(args, "-ugoXcode") + 1];
        bool device = Array.IndexOf(args, "-ugoDevice") >= 0;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(Scene) == null)
            EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene), Scene);
        var ios = NamedBuildTarget.iOS;
        PlayerSettings.SetApplicationIdentifier(ios, "com.ugohttp.player");
        PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(ios, ManagedStrippingLevel.High);
        iOSSdkVersion sdk = PlayerSettings.iOS.sdkVersion;
        PlayerSettings.iOS.sdkVersion = device ? iOSSdkVersion.DeviceSDK : iOSSdkVersion.SimulatorSDK;
        PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        PlayerSettings.iOS.appInBackgroundBehavior = iOSAppInBackgroundBehavior.Suspend;
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        EditorUserBuildSettings.iOSXcodeBuildConfig = XcodeBuildConfig.Release;
        // An active Unity audio session holds media assertions that keep the app running in the background,
        // so the background-task checks would never see suspension or expiry.
        var audio = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0]);
        SerializedProperty disable = audio.FindProperty("m_DisableAudio");
        bool wasDisabled = disable.boolValue;
        disable.boolValue = true;
        audio.ApplyModifiedPropertiesWithoutUndo();
        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = output,
                target = BuildTarget.iOS,
                options = BuildOptions.None
            });
        }
        finally
        {
            PlayerSettings.iOS.sdkVersion = sdk;
            disable.boolValue = wasDisabled;
            audio.ApplyModifiedPropertiesWithoutUndo();
        }
        if (device && report.summary.result == BuildResult.Succeeded)
        {
            string path = System.IO.Path.Combine(output, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(path);
            plist.root.SetString("NSLocalNetworkUsageDescription", "Reaches the UgoHTTP test bench on the LAN.");
            plist.WriteToFile(path);
        }
        Debug.Log("UGO-BUILD " + report.summary.result + " " + report.summary.totalSize);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
