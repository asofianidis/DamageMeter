using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace NevarisDamageMeter;

// FishNet must deserialize every finalized Damage payload before the game can
// apply it or optionally draw a floater. Capturing here avoids relying on the
// recipient's health callbacks or the user's damage-number display settings.
[HarmonyPatch]
internal static class DamageCapturePatch
{
    private static MethodBase TargetMethod()
    {
        Type readers = typeof(Damage).Assembly.GetType(
            "FishNet.Serializing.Generated.GeneratedReaders___Internal", true);
        MethodInfo method = AccessTools.GetDeclaredMethods(readers)
            .FirstOrDefault(candidate =>
                candidate.IsStatic
                && candidate.ReturnType == typeof(Damage)
                && candidate.GetParameters().Length == 1);

        if (method is null)
            throw new MissingMethodException(readers.FullName,
                "static one-parameter method returning Damage");

        Plugin.Info($"Network damage capture target resolved as {readers.FullName}.{method.Name}.");
        return method;
    }

    private static void Postfix(Damage __result)
    {
        try
        {
            MeterController.NoteNetworkDamageCapture();
            MeterController.Record(__result);
        }
        catch (Exception ex)
        {
            Plugin.Warning($"Could not record damage: {ex.Message}");
        }
    }
}
