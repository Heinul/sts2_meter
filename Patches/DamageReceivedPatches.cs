using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace DamageMeterMod.Patches;

/// <summary>
/// 받은 피해 + 사망 추적 패치.
///
/// 확인된 시그니처 (로그에서 발견):
///   AfterDamageReceived(
///     [0] PlayerChoiceContext choiceContext,
///     [1] IRunState runState,
///     [2] CombatState combatState,
///     [3] Creature target,
///     [4] DamageResult result,
///     [5] ValueProp props,
///     [6] Creature dealer,
///     [7] CardModel cardSource)
///
///   AfterDeath(
///     [0] IRunState runState,
///     [1] CombatState combatState,
///     [2] Creature creature,
///     [3] Boolean wasRemovalPrevented,
///     [4] Single deathAnimLength)
/// </summary>
public static class DamageReceivedPatches
{
    // ---------------------------------------------------------------
    // 패치 5: AfterDamageReceived — 플레이어가 데미지를 받은 후
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterDamageReceivedPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterDamageReceived");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterDamageReceived not found");

            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterDamageReceived with {parameters.Length} params:");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }

            return method;
        }

        /// <summary>
        /// Typed params:
        ///   __3 = Creature target (피격자)
        ///   __4 = DamageResult result
        ///   __6 = Creature dealer (공격자)
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Creature __3, DamageResult __4, Creature __6)
        {
            try
            {
                var target = __3;
                var result = __4;
                var dealer = __6;

                // target이 플레이어인 경우만 추적
                if (target == null || !target.IsPlayer) return;

                int totalDamage = result.TotalDamage;
                if (totalDamage <= 0) return;

                var player = target.Player;
                if (player == null) return;

                string playerId = player.NetId.ToString();
                string playerName = target.Name ?? L10N.Unknown;
                string sourceName = dealer?.Name ?? L10N.Unknown;

                DamageTracker.Instance.RecordDamageReceived(
                    playerId, playerName, sourceName,
                    totalDamage, result.UnblockedDamage, result.BlockedDamage,
                    result.WasTargetKilled);

                ModEntry.LogDebug(
                    $"[DamageMeter] {playerName} ← {sourceName} {totalDamage} dmg " +
                    $"(Blocked:{result.BlockedDamage} Killed:{result.WasTargetKilled})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterDamageReceived error: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    // 패치 6: AfterDeath — 엔티티 사망 후
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterDeathPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterDeath");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterDeath not found");

            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterDeath with {parameters.Length} params:");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }

            return method;
        }

        /// <summary>
        /// Typed param: __2 = Creature creature (사망한 엔티티)
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Creature __2)
        {
            try
            {
                var creature = __2;
                if (creature == null || !creature.IsPlayer) return;

                var player = creature.Player;
                if (player == null) return;

                string playerId = player.NetId.ToString();
                string playerName = creature.Name ?? L10N.Unknown;

                DamageTracker.Instance.RecordDamageReceived(
                    playerId, playerName, L10N.Death,
                    0, 0, 0, wasKilled: true);

                ModEntry.Log($"[DamageMeter] {playerName} 사망!");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterDeath error: {ex.Message}");
            }
        }
    }
}
