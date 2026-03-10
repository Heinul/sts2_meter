using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DamageMeterMod.Patches;

/// <summary>
/// 독/DoT 데미지 비율 귀속 패치.
///
/// 확인된 시그니처 (로그에서 발견):
///   AfterPowerAmountChanged(
///     [0] CombatState combatState,
///     [1] PowerModel power,
///     [2] Decimal amount,        ← System.Decimal 타입!
///     [3] Creature applier,
///     [4] CardModel cardSource)
///
/// amount는 변경 후 현재 값. 이전 값은 직접 추적.
/// </summary>
public static class PoisonPatches
{
    // 카드를 마지막으로 낸 플레이어 (독 귀속용)
    private static string _lastActingPlayerId = string.Empty;
    private static string _lastActingPlayerName = string.Empty;

    // 몬스터별 독 수치 추적 (이전 값 비교용): monsterKey → lastAmount
    private static readonly Dictionary<string, int> _lastPoisonAmounts = new();

    /// <summary>카드를 낸 플레이어를 기록 (CombatPatches에서 호출).</summary>
    public static void SetLastActingPlayer(string playerId, string playerName)
    {
        _lastActingPlayerId = playerId;
        _lastActingPlayerName = playerName;
    }

    /// <summary>전투 시작 시 독 추적 상태 초기화.</summary>
    public static void ResetTracking()
    {
        _lastPoisonAmounts.Clear();
        _lastActingPlayerId = string.Empty;
        _lastActingPlayerName = string.Empty;
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

        /// <summary>
        /// Typed params:
        ///   __1 = PowerModel power (직접 타이핑)
        ///   __2 = Decimal amount (변경 후 현재 수치)
        ///   __3 = Creature applier (독을 건 플레이어, 틱 시 null 가능)
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(PowerModel __1, decimal __2, Creature __3)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__1 == null) return;

                // === 파워 이름 확인 (LocString → 로컬라이즈된 이름) ===
                string powerName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(__1.Title)
                    ?? __1.GetType().Name;

                if (string.IsNullOrEmpty(powerName)) return;
                if (!powerName.Contains("Poison", StringComparison.OrdinalIgnoreCase) &&
                    !powerName.Contains("독", StringComparison.OrdinalIgnoreCase))
                    return;

                // === 파워 소유자(몬스터) 찾기 ===
                var ownerCreature = __1.Owner;

                if (ownerCreature == null)
                {
                    ModEntry.LogDebug($"[DamageMeter] Poison PowerModel owner is null. Type: {__1.GetType().FullName}");
                    return;
                }

                if (!ownerCreature.IsMonster) return;

                string monsterName = ownerCreature.Name ?? "Unknown";
                string monsterKey = $"{monsterName}_{ownerCreature.GetHashCode()}";

                // === 이전 수치와 비교 ===
                int currentAmount = (int)__2;
                _lastPoisonAmounts.TryGetValue(monsterKey, out int oldAmount);
                int diff = currentAmount - oldAmount;

                // 수치 갱신
                if (currentAmount <= 0)
                    _lastPoisonAmounts.Remove(monsterKey);
                else
                    _lastPoisonAmounts[monsterKey] = currentAmount;

                ModEntry.LogDebug(
                    $"[DamageMeter] 독 변화: {monsterName} {oldAmount}→{currentAmount} (diff={diff}, applier={__3?.Name})");

                if (diff > 0)
                {
                    // === 독 증가 → 적용 ===
                    // applier 파라미터가 있으면 그 플레이어, 없으면 마지막 행동 플레이어
                    string applicantId = _lastActingPlayerId;
                    string applicantName = _lastActingPlayerName;

                    if (__3 != null && __3.IsPlayer && __3.Player != null)
                    {
                        applicantId = __3.Player.NetId.ToString();
                        applicantName = __3.Name ?? applicantId;
                    }

                    if (!string.IsNullOrEmpty(applicantId))
                    {
                        DamageTracker.Instance.RecordPoisonApplied(monsterKey, applicantId, diff);
                        ModEntry.LogDebug(
                            $"[DamageMeter] 독 적용: {applicantName} → {monsterName} +{diff} ({currentAmount}스택)");
                    }
                }
                else if (diff == -1 && oldAmount > 0)
                {
                    // === 독 틱: 데미지 = 감소 전 수치 (oldAmount) ===
                    // 예: 43스택 → 43데미지 → 42스택
                    int poisonDamage = oldAmount;
                    DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, poisonDamage);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 틱: {monsterName} {poisonDamage}뎀 (잔여 {currentAmount}스택)");
                }
                else if (diff < -1)
                {
                    // === 2 이상 감소 = 해독/제거 (데미지 아님) ===
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 제거: {monsterName} {oldAmount}→{currentAmount}");
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterPowerAmountChanged error: {ex.Message}");
            }
        }
    }
}
