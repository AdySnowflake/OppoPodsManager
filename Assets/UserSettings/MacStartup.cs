using System.Diagnostics;
using System.IO;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Assets.UserSettings;

// 负责将应用写入或移出当前用户的 macOS 登录启动项（~/Library/LaunchAgents 下的 LaunchAgent plist）。
public static class MacStartup
{
#if MACOS
    private const string Label = "com.oppo.podsmanager";

    // 同步自启动配置；开发环境没有可执行文件时只保留设置，不写入无效路径。
    public static void SetEnabled(bool enabled)
    {
        var launchAgentsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        var plistPath = Path.Combine(launchAgentsDirectory, $"{Label}.plist");

        if (!enabled)
        {
            if (File.Exists(plistPath))
                File.Delete(plistPath);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return;

        Directory.CreateDirectory(launchAgentsDirectory);
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{executablePath}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """;
        File.WriteAllText(plistPath, plist);
    }

    // 统一处理 LaunchAgent 写入失败，避免界面层承担系统设置异常。
    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            SetEnabled(enabled);
            ApplicationLog.Current?.Info("Settings", $"macOS 开机启动已更新：enabled={enabled}。");
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Settings", "设置 macOS 开机自启失败。", exception);
            Trace.TraceError("设置 macOS 开机自启失败：" + exception);
            return false;
        }
    }
#else
    // 非 macOS 平台没有 LaunchAgent 机制，no-op。
    public static bool TrySetEnabled(bool enabled) => false;
#endif
}
