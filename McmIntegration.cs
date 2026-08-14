using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using SpiritVale.ModConfig;

namespace NevarisDamageMeter;

internal static class McmIntegration
{
    internal static void Register()
    {
        ModPage page = ModConfigMenu.Register(
            Plugin.Guid,
            Plugin.Name,
            "Configure the damage meter without leaving the game.",
            Plugin.Version);

        page.AddHeader("GENERAL", "Changes save immediately.");
        page.BindToggle(Plugin.Settings.StartVisible, "Show on world entry",
            "Show the meter when your character enters the world.");
        page.BindToggle(Plugin.Settings.IncludeSummons, "Include summon damage",
            "Attribute summon and clone damage to their owning player.");

        page.AddHeader("ENCOUNTER");
        page.BindFloatSlider(Plugin.Settings.NewEncounterAfter, "New encounter delay",
            0f, 120f, 1f, "0s",
            "Start a new encounter after this many idle seconds. 0 disables automatic reset.");
        page.BindIntegerSlider(Plugin.Settings.MaxRows, "Maximum rows",
            1, 20, 1, "Maximum number of attackers shown in the meter.");

        AddHotkeyControls(page);

        page.AddHeader("DISPLAY");
        page.BindFloatSlider(Plugin.Settings.UiScale, "Meter scale",
            0.55f, 1.25f, 0.05f, "0.00x",
            "Resize the meter while preserving its aspect ratio.",
            _ => MeterController.ApplyConfiguredScale());

        page.AddHeader("ACTIONS");
        page.AddButton("ResetEncounter", "Reset current encounter",
            MeterController.ResetEncounter, "Clear all recorded damage now.");
        page.AddButton("ResetLayout", "Reset meter layout",
            MeterController.ResetLayout, "Restore the default position and size.");

        Plugin.Info("Registered the Damage Meter page with Mod Configuration Menu.");
    }

    private static void AddHotkeyControls(ModPage page)
    {
        MethodInfo bindHotkey = typeof(ModPage).GetMethod(
            "BindHotkey",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[]
            {
                typeof(ConfigEntry<KeyboardShortcut>),
                typeof(string),
                typeof(string),
                typeof(Action<KeyboardShortcut>)
            },
            null);

        if (bindHotkey is null)
        {
            page.AddHeader("HOTKEYS",
                $"Shortcuts remain active ({Plugin.Settings.ToggleHotkey.Value} toggle, "
                + $"{Plugin.Settings.ResetHotkey.Value} reset), but this MCM version cannot edit chords.");
            Plugin.Warning("The installed MCM does not expose multi-key hotkey controls; "
                + "all other Damage Meter settings were registered.");
            return;
        }

        page.AddHeader("HOTKEYS", "Hold the desired keys, press the main key last, then release.");
        bindHotkey.Invoke(page, new object[]
        {
            Plugin.Settings.ToggleHotkey,
            "Toggle meter",
            "Show or hide the damage meter.",
            null
        });
        bindHotkey.Invoke(page, new object[]
        {
            Plugin.Settings.ResetHotkey,
            "Reset encounter",
            "Clear all damage from the current encounter.",
            null
        });
    }
}
