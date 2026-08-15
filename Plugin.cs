using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using HarmonyLib;
using SpiritVale.ModConfig;
using UnityEngine;

namespace NevarisDamageMeter;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency(ModConfigPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "alex.nevaris.damagemeter";
    public const string Name = "Damage Meter";
    public const string Version = "1.3.1";

    internal static ManualLogSource Logger = null!;
    internal static MeterSettings Settings = null!;
    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        Settings = new MeterSettings(Config);
        McmIntegration.Register();

        _harmony = new Harmony(Guid);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        MeterController.Initialize();

        Info($"{Name} {Version} loaded. {Settings.ToggleHotkey.Value} toggles the meter; "
            + $"{Settings.ResetHotkey.Value} resets it.");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        PlayerWorldGate.Shutdown();
        MeterController.Shutdown();
        return base.Unload();
    }

    internal static void Info(string message) => Logger.LogInfo($"[DamageMeter] {message}");
    internal static void Warning(string message) => Logger.LogWarning($"[DamageMeter] {message}");
    internal static void Error(string message) => Logger.LogError($"[DamageMeter] {message}");
}

internal sealed class MeterSettings
{
    internal readonly ConfigEntry<KeyboardShortcut> ToggleHotkey;
    internal readonly ConfigEntry<KeyboardShortcut> ResetHotkey;
    internal readonly ConfigEntry<float> NewEncounterAfter;
    internal readonly ConfigEntry<int> MaxRows;
    internal readonly ConfigEntry<bool> StartVisible;
    internal readonly ConfigEntry<bool> IncludeSummons;
    internal readonly ConfigEntry<float> PositionX;
    internal readonly ConfigEntry<float> PositionY;
    internal readonly ConfigEntry<float> UiScale;

    internal MeterSettings(ConfigFile config)
    {
        ToggleHotkey = config.Bind("Hotkeys", "Toggle", new KeyboardShortcut(KeyCode.F8),
            "Show or hide the meter.");
        ResetHotkey = config.Bind("Hotkeys", "Reset", new KeyboardShortcut(KeyCode.F9),
            "Reset the current encounter.");
        NewEncounterAfter = config.Bind("Meter", "NewEncounterAfterSeconds", 12f,
            "Idle time before the next damage event starts a new encounter. Set to 0 to disable.");
        MaxRows = config.Bind("Meter", "MaxRows", 8, "Maximum number of attackers shown.");
        StartVisible = config.Bind("Meter", "StartVisible", true, "Show the meter when the plugin loads.");
        IncludeSummons = config.Bind("Meter", "IncludeSummons", true,
            "Attribute summon damage to the summoning player. If false, summon damage is ignored.");
        PositionX = config.Bind("Display", "PositionX", 22f,
            "Saved horizontal position of the draggable meter.");
        PositionY = config.Bind("Display", "PositionY", -165f,
            "Saved vertical position of the draggable meter.");
        UiScale = config.Bind("Display", "Scale", 1f,
            "Saved meter scale. Drag the bottom-right corner to resize it.");
    }
}
