using Android.App;
using Android.Content.PM;

using System;
using System.Linq;

namespace SMAPIGameLoader;

internal static class StardewApkTool
{
    public const string GamePlayStorePackageName = "com.chucklefish.stardewvalley";
    public const string GameGalaxyStorePackageName = "com.chucklefish.stardewvalleysamsung";
    public static bool IsSplitContent { get; private set; }
    
    //init at first SDK
    static StardewApkTool()
    {
        Console.WriteLine("Initialize Stardew Apk Tool");
        var playStore = ApkTool.GetPackageInfo(GamePlayStorePackageName);
        var samsung = ApkTool.GetPackageInfo(GameGalaxyStorePackageName);

        //select samsung first, better for debug, test app
        if (samsung != null)
        {
            CurrentPackageInfo = samsung;
            Console.WriteLine("Game Install From Galaxy Store");
        }
        else if (playStore != null)
        {
            CurrentPackageInfo = playStore;
            Console.WriteLine("Game Install From Play Store");

			//из-за священной войны с пиратами страдают обычные люди!!!
            var splitApks = CurrentPackageInfo.ApplicationInfo?.SplitSourceDirs;
            IsSplitContent = splitApks?.Count > 1;
        }
    }

    public static PackageInfo CurrentPackageInfo { get; private set; }
    public static bool IsInstalled { get => CurrentPackageInfo != null; }

    public static Android.Content.Context GetContext => Application.Context;
    public static string BaseApkPath => CurrentPackageInfo.ApplicationInfo.PublicSourceDir;
    public static string? Arm64ApkPath
    {
        get
        {
            try
            {
                if (CurrentPackageInfo == null)
                {
                    return null;
                }

                if (IsSplitContent)
                {
                    return CurrentPackageInfo.ApplicationInfo.SplitSourceDirs?.FirstOrDefault(path => path.Contains("split_config.arm64"));
                }

                return BaseApkPath;
            }
            catch (Exception ex)
            {
                ErrorDialogTool.Show(ex, "Error try to get Arm64ApkPath");
                return null;
            }
        }
    }
    public static string? ContentApkPath
    {
        get
        {
            try
            {
                if (CurrentPackageInfo == null)
                {
                    return null;
                }

                if (IsSplitContent)
                {
                    return CurrentPackageInfo.ApplicationInfo.SplitSourceDirs?.First(path => path.Contains("split_content"));
                }

                return BaseApkPath;
            }
            catch (Exception ex)
            {
                ErrorDialogTool.Show(ex, "Error try to get ContentApkPath");
                return null;
            }
        }
    }

    public static Version GameVersionSupport
    {
        get
        {
            if (CurrentPackageInfo == null)
                return null;

            switch (CurrentPackageInfo.PackageName)
            {
                case GamePlayStorePackageName:
                    return new(1, 6, 15, 0);
                case GameGalaxyStorePackageName:
                    return new(1, 6, 15, 0);
                default:
                    return null;
            }
        }
    }
    public static Version CurrentGameVersion 
    {
        get
        {
            try
            {
                return new Version(CurrentPackageInfo?.VersionName);
            }
            catch (Exception)
            {
                return new Version(0,0,0,0);
            }
        }
    }
    public static bool IsGameVersionSupport => CurrentGameVersion >= GameVersionSupport;
}
