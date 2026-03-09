using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;

namespace DamageMeterMod.Patches;

/// <summary>
/// 받은 피해 + 사망 추적 패치.
/// AfterDamageReceived, AfterDeath 훅.
///
/// 주의: 이 훅들의 파라미터 시그니처는 미확인.
/// 런타임에서 object[] __args로 타입을 발견한 후 typed 파라미터로 전환 예정.
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

            // 시그니처 발견용 로깅
            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterDamageReceived with {parameters.Length} params:");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }

            return method;
        }

        [HarmonyPostfix]
        public static void Postfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 4) return;

                // AfterDamageGiven과 유사한 구조로 추정:
                // (PlayerChoiceContext, CombatState, Creature dealer, DamageResult, ValueProp, Creature target, ...)
                // target이 플레이어인 경우를 찾아야 함

                // 모든 args에서 Creature와 DamageResult를 찾음
                object? damageResult = null;
                object? dealerCreature = null;
                object? targetCreature = null;

                var creatureType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Creatures.Creature");
                var damageResultType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.DamageResult");

                foreach (var arg in __args)
                {
                    if (arg == null) continue;
                    var argType = arg.GetType();

                    if (damageResultType != null && damageResultType.IsAssignableFrom(argType))
                    {
                        damageResult = arg;
                    }
                    else if (creatureType != null && creatureType.IsAssignableFrom(argType))
                    {
                        if (dealerCreature == null)
                            dealerCreature = arg;
                        else
                            targetCreature = arg;
                    }
                }

                if (damageResult == null || targetCreature == null) return;

                // target이 플레이어인지 확인
                var isPlayerProp = creatureType?.GetProperty("IsPlayer");
                var playerProp = creatureType?.GetProperty("Player");
                var nameProp = creatureType?.GetProperty("Name");

                bool isPlayer = (bool)(isPlayerProp?.GetValue(targetCreature) ?? false);
                if (!isPlayer) return;

                // DamageResult에서 값 추출
                int totalDamage = (int)(damageResultType?.GetProperty("TotalDamage")?.GetValue(damageResult) ?? 0);
                int unblockedDamage = (int)(damageResultType?.GetProperty("UnblockedDamage")?.GetValue(damageResult) ?? 0);
                int blockedDamage = (int)(damageResultType?.GetProperty("BlockedDamage")?.GetValue(damageResult) ?? 0);
                bool wasKilled = (bool)(damageResultType?.GetProperty("WasTargetKilled")?.GetValue(damageResult) ?? false);

                if (totalDamage <= 0) return;

                // 플레이어 ID
                var playerObj = playerProp?.GetValue(targetCreature);
                var netIdProp = playerObj?.GetType().GetProperty("NetId");
                string playerId = netIdProp?.GetValue(playerObj)?.ToString() ?? "unknown";
                string playerName = nameProp?.GetValue(targetCreature)?.ToString() ?? "알 수 없음";

                // 공격자 이름
                string sourceName = nameProp?.GetValue(dealerCreature)?.ToString() ?? "알 수 없음";

                DamageTracker.Instance.RecordDamageReceived(
                    playerId, playerName, sourceName,
                    totalDamage, unblockedDamage, blockedDamage, wasKilled);

                ModEntry.LogDebug(
                    $"[DamageMeter] {playerName} ← {sourceName} {totalDamage} dmg " +
                    $"(Blocked:{blockedDamage} Killed:{wasKilled})");
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

        [HarmonyPostfix]
        public static void Postfix(object[] __args)
        {
            try
            {
                if (__args == null) return;

                var creatureType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Creatures.Creature");
                if (creatureType == null) return;

                // args에서 Creature를 찾음 (사망한 엔티티)
                foreach (var arg in __args)
                {
                    if (arg == null) continue;
                    if (!creatureType.IsAssignableFrom(arg.GetType())) continue;

                    var isPlayerProp = creatureType.GetProperty("IsPlayer");
                    bool isPlayer = (bool)(isPlayerProp?.GetValue(arg) ?? false);
                    if (!isPlayer) continue;

                    var playerProp = creatureType.GetProperty("Player");
                    var nameProp = creatureType.GetProperty("Name");

                    var playerObj = playerProp?.GetValue(arg);
                    var netIdProp = playerObj?.GetType().GetProperty("NetId");

                    string playerId = netIdProp?.GetValue(playerObj)?.ToString() ?? "unknown";
                    string playerName = nameProp?.GetValue(arg)?.ToString() ?? "알 수 없음";

                    DamageTracker.Instance.RecordDamageReceived(
                        playerId, playerName, "사망",
                        0, 0, 0, wasKilled: true);

                    ModEntry.Log($"[DamageMeter] {playerName} 사망!");
                    break;
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterDeath error: {ex.Message}");
            }
        }
    }
}
