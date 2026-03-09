using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;

namespace DamageMeterMod.Patches;

/// <summary>
/// 독/DoT 데미지 비율 귀속 패치.
/// AfterPowerAmountChanged 훅을 사용.
///
/// 귀속 방식:
///   - 독 적용 시 (amount 증가): 해당 플레이어의 기여 스택 수를 기록
///   - 독 틱 시 (amount 감소): 기여 비율에 따라 데미지를 플레이어들에게 분배
///   - 예: A가 5독, B가 3독 → 독 8데미지 → A: 5, B: 3
/// </summary>
public static class PoisonPatches
{
    // 현재 행동 중인 플레이어를 추적 (카드를 마지막으로 낸 플레이어)
    private static string _lastActingPlayerId = string.Empty;
    private static string _lastActingPlayerName = string.Empty;

    /// <summary>카드를 낸 플레이어를 기록 (CombatPatches에서 호출).</summary>
    public static void SetLastActingPlayer(string playerId, string playerName)
    {
        _lastActingPlayerId = playerId;
        _lastActingPlayerName = playerName;
    }

    // ---------------------------------------------------------------
    // 패치 7: AfterPowerAmountChanged — 파워 수치 변경 후
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterPowerAmountChangedPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterPowerAmountChanged");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterPowerAmountChanged not found");

            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterPowerAmountChanged with {parameters.Length} params:");
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
                if (__args == null || !DamageTracker.Instance.IsActive) return;

                // 파라미터에서 Power/PowerModel, Creature, amount 정보를 찾음
                var creatureType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Creatures.Creature");

                object? ownerCreature = null;
                string powerName = string.Empty;
                int oldAmount = 0;
                int newAmount = 0;
                bool foundAmounts = false;

                foreach (var arg in __args)
                {
                    if (arg == null) continue;
                    var argType = arg.GetType();

                    // Creature 찾기
                    if (creatureType != null && creatureType.IsAssignableFrom(argType))
                    {
                        ownerCreature = arg;
                        continue;
                    }

                    // Power/PowerModel에서 이름 추출 시도
                    var nameProp = argType.GetProperty("Name") ?? argType.GetProperty("PowerName");
                    if (nameProp != null && nameProp.PropertyType == typeof(string))
                    {
                        var name = nameProp.GetValue(arg)?.ToString();
                        if (!string.IsNullOrEmpty(name))
                            powerName = name;
                    }

                    // int 파라미터 (old/new amount)
                    if (arg is int intVal && !foundAmounts)
                    {
                        if (oldAmount == 0 && newAmount == 0)
                            oldAmount = intVal;
                        else
                        {
                            newAmount = intVal;
                            foundAmounts = true;
                        }
                    }
                }

                // "Poison" 관련인지 확인 (대소문자 무시)
                if (string.IsNullOrEmpty(powerName)) return;
                if (!powerName.Contains("Poison", StringComparison.OrdinalIgnoreCase) &&
                    !powerName.Contains("독", StringComparison.OrdinalIgnoreCase))
                    return;

                if (ownerCreature == null) return;

                // 몬스터에게 적용된 독만 추적
                var isMonsterProp = creatureType?.GetProperty("IsMonster");
                bool isMonster = (bool)(isMonsterProp?.GetValue(ownerCreature) ?? false);
                if (!isMonster) return;

                var ownerNameProp = creatureType?.GetProperty("Name");
                string monsterName = ownerNameProp?.GetValue(ownerCreature)?.ToString() ?? "Unknown";
                string monsterKey = $"{monsterName}_{ownerCreature.GetHashCode()}";

                int diff = newAmount - oldAmount;

                if (diff > 0 && !string.IsNullOrEmpty(_lastActingPlayerId))
                {
                    // 독 스택 증가 → 현재 플레이어에게 귀속
                    DamageTracker.Instance.RecordPoisonApplied(monsterKey, _lastActingPlayerId, diff);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 적용: {_lastActingPlayerName} → {monsterName} +{diff} 스택");
                }
                else if (diff < 0)
                {
                    // 독 스택 감소 → 독 틱 데미지 (감소분 = 데미지)
                    int poisonDamage = Math.Abs(diff);
                    DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, poisonDamage);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 틱: {monsterName} -{poisonDamage} (비율 귀속)");
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterPowerAmountChanged error: {ex.Message}");
            }
        }
    }
}
