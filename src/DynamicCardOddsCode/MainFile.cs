using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using System.Reflection;

namespace DynamicCardOdds;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "DynamicCardOdds";
    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        DcoAdaptiveOdds.InitializeStorage();
        DcoLibraryUi.EnsureInstalled();

        var harmony = new Harmony(ModId);
        var android = IsAndroidRuntime();
        var patches = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
            .Where(type => !android || !IsAndroidUiPatch(type))
            .Where(type => !android || type != typeof(DcoCardRewardRerollPatch))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        if (android && TrySchedulePatches(harmony, patches))
        {
            Logger.Info($"[DynamicCardOdds] v1.0.6 Android patch queue scheduled: {patches.Count} gameplay hooks; disabled-card masks use a layout-independent Polygon2D.");
            return;
        }

        foreach (var type in patches)
            PatchOne(harmony, type);
        Logger.Info($"[DynamicCardOdds] v1.0.6 loaded; patches={patches.Count}; disabled-card masks use a layout-independent Polygon2D; anti-repeat deck fingerprint intentionally disabled.");
    }

    private static bool IsAndroidUiPatch(Type type) =>
        type == typeof(DcoLibraryOpenedPatch) ||
        type == typeof(DcoLibraryClosedPatch) ||
        type == typeof(DcoLibraryFilterPatch) ||
        type == typeof(DcoLibraryGridInitPatch) ||
        type == typeof(DcoLibraryGridAssignPatch) ||
        type == typeof(DcoLibraryFinalFilterPatch) ||
        type == typeof(DcoCardVisualScopePatch) ||
        type == typeof(DcoCardReloadScopePatch);

    internal static void PatchOne(Harmony harmony, Type type)
    {
        try
        {
            harmony.CreateClassProcessor(type).Patch();
            Logger.Info($"[DynamicCardOdds] patched {type.FullName}.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[DynamicCardOdds] patch skipped for {type.FullName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TrySchedulePatches(Harmony harmony, IReadOnlyList<Type> patches)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
                return false;
            var installer = new AndroidPatchInstaller();
            installer.Configure(harmony, patches);
            tree.Root.CallDeferred("add_child", installer);
            return true;
        }
        catch { return false; }
    }

    internal static bool IsAndroidRuntime()
    {
        try
        {
            if (OS.HasFeature("android") || string.Equals(OS.GetName(), "Android", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        try { return !OperatingSystem.IsWindows() && Directory.Exists("/data/data") && Directory.Exists("/system"); }
        catch { return false; }
    }
}

internal partial class AndroidPatchInstaller : Node
{
    private readonly Queue<Type> _pending = new();
    private Harmony? _harmony;
    private double _delay = 0.75;

    internal void Configure(Harmony harmony, IEnumerable<Type> patches)
    {
        _harmony = harmony;
        foreach (var patch in patches)
            _pending.Enqueue(patch);
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready() => SetProcess(true);

    public override void _Process(double delta)
    {
        _delay -= delta;
        if (_delay > 0)
            return;
        _delay = 0.25;
        if (_harmony is null || _pending.Count == 0)
        {
            SetProcess(false);
            QueueFree();
            return;
        }
        MainFile.PatchOne(_harmony, _pending.Dequeue());
    }
}
