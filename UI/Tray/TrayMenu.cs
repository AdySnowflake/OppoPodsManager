using Avalonia.Controls;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Features;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Core.Features;

namespace OppoPodsManager.UI.Tray;

// 构建托盘的最小常驻菜单，所有业务状态仍由主窗口和控制层维护。
internal static class TrayMenu
{
    // 往传入的 NativeMenu 填充内容（不新建实例）。
    // 注意：macOS 上替换 TrayIcon.Menu 实例会触发 Avalonia.Native 的
    // "The menu being updated does not match" 异常（实测每次快照刷新都抛），
    // 因此调用方必须复用同一 NativeMenu 实例、只清空重建其 Items。
    public static void Populate(
        NativeMenu menu,
        BusinessSnapshot snapshot,
        IBrandManager? manager,
        CommandDispatcher dispatcher,
        Action showMainWindow,
        Action showMiniWindow,
        Action exitApplication)
    {
        if (snapshot.IsConnected && manager is not null)
        {
            AddDeviceControls(menu, snapshot, manager, dispatcher);
            menu.Add(new NativeMenuItemSeparator());
        }
        var showItem = new NativeMenuItem(TranslationCatalog.Get("Tray_ShowMain"));
        showItem.Click += (_, _) =>
        {
            ApplicationLog.Current?.Info("Tray", "点击托盘菜单：显示主窗口。");
            showMainWindow();
        };
        var miniItem = new NativeMenuItem(TranslationCatalog.Get("Tray_ShowMini"));
        miniItem.Click += (_, _) =>
        {
            ApplicationLog.Current?.Info("Tray", "点击托盘菜单：显示小窗口。");
            showMiniWindow();
        };
        menu.Add(miniItem);
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        var exitItem = new NativeMenuItem(TranslationCatalog.Get("Tray_Quit"));
        exitItem.Click += (_, _) =>
        {
            ApplicationLog.Current?.Info("Tray", "点击托盘菜单：退出应用。");
            exitApplication();
        };
        menu.Add(exitItem);
    }

    // 将原项目最常用的三个设备开关放入原生菜单，状态由最新快照回显。
    private static void AddDeviceControls(
        NativeMenu menu,
        BusinessSnapshot snapshot,
        IBrandManager manager,
        CommandDispatcher dispatcher)
    {
        var presentation = manager.Presentation;
        AddNoiseModes(menu, snapshot, manager, dispatcher);
        if (presentation.VisibleControls.Contains("game-mode"))
        {
            AddToggle(menu, TranslationCatalog.Get("Feature_GameMode"), snapshot.Game.IsEnabled == true,
                "托盘游戏模式",
                dispatcher,
                enabled => manager.SetGameModeAsync(enabled, CancellationToken.None));
        }
        if (presentation.VisibleControls.Contains("spatial-sound"))
        {
            var enabled = presentation.ControlStates.TryGetValue("spatial-sound", out var value) && value;
            AddToggle(menu, TranslationCatalog.Get("Feature_SpatialSound"), enabled,
                "托盘空间声场",
                dispatcher,
                value => manager.SetSpatialSoundAsync(value, CancellationToken.None));
        }
        if (presentation.VisibleControls.Contains("dual-device"))
        {
            var enabled = presentation.ControlStates.TryGetValue("dual-device", out var value) && value;
            AddToggle(menu, TranslationCatalog.Get("Feature_DualDevice"), enabled,
                "托盘双设备",
                dispatcher,
                value => manager.SetDualDeviceAsync(value, CancellationToken.None));
        }
    }

    // 按型号能力平铺 ANC 选项，并用最新设备状态标记当前模式。
    private static void AddNoiseModes(
        NativeMenu menu,
        BusinessSnapshot snapshot,
        IBrandManager manager,
        CommandDispatcher dispatcher)
    {
        var modes = FlattenNoiseModes(manager.Presentation.NoiseOptions)
            .Distinct()
            .ToArray();
        foreach (var mode in modes)
        {
            var item = new NativeMenuItem($"{(snapshot.Noise.Mode == mode ? "✓ " : string.Empty)}{DeviceText.NoiseModeName(mode)}");
            item.Click += async (_, _) =>
            {
                ApplicationLog.Current?.Info("Tray", $"点击托盘菜单降噪：mode={mode}。");
                await dispatcher.RunAsync(
                    "托盘降噪",
                    active => active.SetNoiseCancellationAsync(mode, CancellationToken.None));
            };
            menu.Add(item);
        }

        if (modes.Length > 0)
            menu.Add(new NativeMenuItemSeparator());
    }

    // 将后端已经分组的 ANC 选项展开成托盘可使用的叶子模式。
    private static IEnumerable<NoiseMode> FlattenNoiseModes(IEnumerable<NoiseOptionModel> options)
    {
        foreach (var option in options)
        {
            if (option.Children.Count == 0 && option.Mode != NoiseMode.Unknown)
                yield return option.Mode;

            foreach (var child in FlattenNoiseModes(option.Children))
                yield return child;
        }
    }

    // 统一生成带勾选状态的异步菜单开关。
    private static void AddToggle(
        NativeMenu menu,
        string label,
        bool enabled,
        string operation,
        CommandDispatcher dispatcher,
        Func<bool, Task<bool>> setEnabled)
    {
        var item = new NativeMenuItem($"{(enabled ? "✓ " : string.Empty)}{label}");
        item.Click += async (_, _) =>
        {
            ApplicationLog.Current?.Info("Tray", $"点击托盘菜单开关：label={label}，enabled={!enabled}。");
            await dispatcher.RunAsync(operation, _ => setEnabled(!enabled));
        };
        menu.Add(item);
    }
}
