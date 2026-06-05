using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DamageMeterMod.Patches;

/// <summary>
/// 독/DoT 데미지 비율 귀속 패치.
///
/// 확인된 시그니처:
///   스테이블 (5 params):
///     AfterPowerAmountChanged(CombatState, PowerModel, Decimal, Creature, CardModel)
///   베타 v0.107.0 (6 params — PlayerChoiceContext 추가):
///     AfterPowerAmountChanged(ICombatState, PlayerChoiceContext, PowerModel, Decimal, Creature, CardModel)
///
/// 양쪽 호환: TargetMethod에서 파라미터 수 감지 → object[] __args + 오프셋으로 접근.
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

    /// <summary>몬스터 사망 시 남은 독 스택을 소비하고 마지막 틱 데미지 기록.</summary>
    public static void HandleMonsterDeath(Creature creature)
    {
        if (creature == null || !creature.IsMonster) return;

        string monsterName = creature.Name ?? "Unknown";
        string monsterKey = $"{monsterName}_{creature.GetHashCode()}";

        if (_lastPoisonAmounts.TryGetValue(monsterKey, out int remainingPoison) && remainingPoison > 0)
        {
#if DEBUG
            PoisonDebugLogger.Log($"  [독 킬] {monsterName} 사망, 남은 독 {remainingPoison}스택 → 틱 데미지 기록");
            PoisonDebugLogger.IncrementPoisonTick();
#endif
            DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, remainingPoison);
            _lastPoisonAmounts.Remove(monsterKey);

            ModEntry.Log($"[DamageMeter] 독 킬: {monsterName} 남은 {remainingPoison}스택 → 마지막 틱 데미지 기록");
        }
    }

    // ---------------------------------------------------------------
    // 패치 7: AfterPowerAmountChanged — 파워 수치 변경 후
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterPowerAmountChangedPatch
    {
        // 스테이블(5파람): offset=0 → PowerModel[1], Decimal[2], Creature[3]
        // 베타(6파람):     offset=1 → PowerModel[2], Decimal[3], Creature[4]
        private static int _paramOffset;

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
            _paramOffset = parameters.Length >= 6 ? 1 : 0;
            ModEntry.Log($"[DamageMeter] Found Hook.AfterPowerAmountChanged with {parameters.Length} params (offset={_paramOffset}):");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }

            return method;
        }

        /// <summary>
        /// object[] __args로 전체 파라미터를 받아 오프셋으로 접근.
        /// 스테이블/베타 양쪽 호환.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(object[] __args)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;

                // 오프셋 적용하여 파라미터 추출
                var power = __args[1 + _paramOffset] as PowerModel;
                if (power == null) return;
                decimal amount = (decimal)__args[2 + _paramOffset];
                var applier = __args[3 + _paramOffset] as Creature;

#if DEBUG
                PoisonDebugLogger.IncrementHookCall();
                PoisonDebugLogger.LogAllPowerChanges(power, amount);
#endif

                // === 파워 이름 확인 (LocString → 로컬라이즈된 이름) ===
                string powerName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(power.Title)
                    ?? power.GetType().Name;

                if (string.IsNullOrEmpty(powerName))
                {
#if DEBUG
                    PoisonDebugLogger.LogPowerNameFilter("(empty)", false, "이름 비어있음");
#endif
                    return;
                }

                bool isPoisonMatch = powerName.Contains("Poison", StringComparison.OrdinalIgnoreCase) ||
                    powerName.Contains("독", StringComparison.OrdinalIgnoreCase);

#if DEBUG
                PoisonDebugLogger.LogPowerNameFilter(powerName, isPoisonMatch,
                    isPoisonMatch ? "Poison/독 포함" : "Poison/독 미포함");
                if (isPoisonMatch)
                    PoisonDebugLogger.IncrementPoisonMatch();
#endif

                if (!isPoisonMatch) return;

                // === 파워 소유자(몬스터) 찾기 ===
                var ownerCreature = power.Owner;

#if DEBUG
                PoisonDebugLogger.LogPowerAmountChanged(power, amount, applier,
                    powerName, ownerCreature?.Name, ownerCreature?.IsMonster ?? false);
#endif

                if (ownerCreature == null)
                {
                    ModEntry.LogDebug($"[DamageMeter] Poison PowerModel owner is null. Type: {power.GetType().FullName}");
#if DEBUG
                    PoisonDebugLogger.Log("  !! Owner가 null → 스킵");
#endif
                    return;
                }

                if (!ownerCreature.IsMonster)
                {
#if DEBUG
                    PoisonDebugLogger.Log($"  !! Owner가 몬스터가 아님 (IsPlayer={ownerCreature.IsPlayer}, IsPet={ownerCreature.IsPet}) → 스킵");
#endif
                    return;
                }

                string monsterName = ownerCreature.Name ?? "Unknown";
                string monsterKey = $"{monsterName}_{ownerCreature.GetHashCode()}";

                // === 이전 수치와 비교 ===
                int paramAmount = (int)amount;
                int modelAmount = (int)power.Amount;
#if DEBUG
                PoisonDebugLogger.Log($"  !! 비교: amount(param)={paramAmount}, power.Amount(model)={modelAmount}");
#endif
                int currentAmount = modelAmount;
                _lastPoisonAmounts.TryGetValue(monsterKey, out int oldAmount);
                int diff = currentAmount - oldAmount;

                // 수치 갱신
                if (currentAmount <= 0)
                    _lastPoisonAmounts.Remove(monsterKey);
                else
                    _lastPoisonAmounts[monsterKey] = currentAmount;

#if DEBUG
                PoisonDebugLogger.LogDiffCalculation(monsterKey, oldAmount, currentAmount, diff);
#endif

                ModEntry.LogDebug(
                    $"[DamageMeter] 독 변화: {monsterName} {oldAmount}→{currentAmount} (diff={diff}, applier={applier?.Name})");

                if (diff > 0)
                {
                    // === 독 증가 → 적용 ===
                    string applicantId = _lastActingPlayerId;
                    string applicantName = _lastActingPlayerName;

#if DEBUG
                    PoisonDebugLogger.Log($"  귀속 후보: lastActing={_lastActingPlayerId} ({_lastActingPlayerName}), applier={applier?.Name}");
#endif

                    if (applier != null && applier.IsPlayer && applier.Player != null)
                    {
                        applicantId = applier.Player.NetId.ToString();
                        applicantName = applier.Name ?? applicantId;
#if DEBUG
                        PoisonDebugLogger.Log($"  → applier 파라미터 사용: {applicantId} ({applicantName})");
#endif
                    }
                    else
                    {
#if DEBUG
                        PoisonDebugLogger.Log($"  → lastActingPlayer 사용: {applicantId} ({applicantName})");
                        if (applier != null)
                            PoisonDebugLogger.Log($"  (applier가 플레이어가 아님: IsPlayer={applier.IsPlayer}, Player={applier.Player})");
                        else
                            PoisonDebugLogger.Log("  (applier가 null)");
#endif
                    }

                    if (!string.IsNullOrEmpty(applicantId))
                    {
                        DamageTracker.Instance.RecordPoisonApplied(monsterKey, applicantId, diff);
#if DEBUG
                        PoisonDebugLogger.IncrementPoisonApply();
#endif
                        ModEntry.LogDebug(
                            $"[DamageMeter] 독 적용: {applicantName} → {monsterName} +{diff} ({currentAmount}스택)");
                    }
                    else
                    {
#if DEBUG
                        PoisonDebugLogger.Log("  !! applicantId가 비어있어 독 적용 스킵됨");
#endif
                    }
                }
                else if (diff == -1 && oldAmount > 0)
                {
                    // === 독 틱: 데미지 = 감소 전 수치 (oldAmount) ===
                    int poisonDamage = oldAmount;
#if DEBUG
                    PoisonDebugLogger.Log($"  독 틱 호출: monsterKey={monsterKey}, damage={poisonDamage}");
                    PoisonDebugLogger.IncrementPoisonTick();
#endif
                    DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, poisonDamage);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 틱: {monsterName} {poisonDamage}뎀 (잔여 {currentAmount}스택)");
                }
                else if (diff < -1)
                {
                    int poisonDamage = oldAmount;
#if DEBUG
                    PoisonDebugLogger.Log($"  독 대량 감소: {oldAmount} → {currentAmount}, diff={diff} → 틱 데미지={poisonDamage}로 처리");
                    PoisonDebugLogger.IncrementPoisonTick();
#endif
                    DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, poisonDamage);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 킬/대량감소: {monsterName} {poisonDamage}뎀 ({oldAmount}→{currentAmount})");
                }
                else if (diff == 0)
                {
#if DEBUG
                    PoisonDebugLogger.Log("  diff=0 → 변화 없음, 아무 처리 안 함");
#endif
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterPowerAmountChanged error: {ex.Message}");
#if DEBUG
                PoisonDebugLogger.Log($"  !! 예외 발생: {ex}");
#endif
            }
        }
    }
}
