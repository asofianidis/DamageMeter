using HarmonyLib;

namespace NevarisDamageMeter;

// This was the only lifecycle hook in the bootable 1.0.1 build. It performs no
// meter work unless this exact controller is already spawned and locally owned.
[HarmonyPatch(typeof(PlayerController), "Update")]
internal static class RuntimeTickPatch
{
    private static bool _loggedFailure;

    private static void Postfix(PlayerController __instance)
    {
        try
        {
            if (!__instance.IsSpawned || !__instance.IsOwner)
                return;

            PlayerWorldGate.Tick(__instance);
        }
        catch (System.Exception ex)
        {
            if (_loggedFailure)
                return;
            _loggedFailure = true;
            Plugin.Error($"Damage meter world gate disabled after an update error: {ex}");
        }
    }
}
