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

#if DEBUG
                // 모든 파워 변경을 파일에 기록 (Poison 아닌 것도 포함)
                PoisonDebugLogger.IncrementHookCall();
                PoisonDebugLogger.LogAllPowerChanges(__1, __2);
#endif

                // === 파워 이름 확인 (LocString → 로컬라이즈된 이름) ===
                string powerName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(__1.Title)
                    ?? __1.GetType().Name;

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
                var ownerCreature = __1.Owner;

#if DEBUG
                PoisonDebugLogger.LogPowerAmountChanged(__1, __2, __3,
                    powerName, ownerCreature?.Name, ownerCreature?.IsMonster ?? false);
#endif

                if (ownerCreature == null)
                {
                    ModEntry.LogDebug($"[DamageMeter] Poison PowerModel owner is null. Type: {__1.GetType().FullName}");
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
                // __2는 훅 파라미터 amount, __1.Amount는 PowerModel의 실제 현재값
                int paramAmount = (int)__2;
                int modelAmount = (int)__1.Amount;
#if DEBUG
                PoisonDebugLogger.Log($"  !! 비교: __2(param)={paramAmount}, __1.Amount(model)={modelAmount}");
#endif
                // PowerModel.Amount가 실제 현재 총량 (훅 파라미터가 아닌)
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
                    $"[DamageMeter] 독 변화: {monsterName} {oldAmount}→{currentAmount} (diff={diff}, applier={__3?.Name})");

                if (diff > 0)
                {
                    // === 독 증가 → 적용 ===
                    string applicantId = _lastActingPlayerId;
                    string applicantName = _lastActingPlayerName;

#if DEBUG
                    PoisonDebugLogger.Log($"  귀속 후보: lastActing={_lastActingPlayerId} ({_lastActingPlayerName}), applier={__3?.Name}");
#endif

                    if (__3 != null && __3.IsPlayer && __3.Player != null)
                    {
                        applicantId = __3.Player.NetId.ToString();
                        applicantName = __3.Name ?? applicantId;
#if DEBUG
                        PoisonDebugLogger.Log($"  → applier 파라미터 사용: {applicantId} ({applicantName})");
#endif
                    }
                    else
                    {
#if DEBUG
                        PoisonDebugLogger.Log($"  → lastActingPlayer 사용: {applicantId} ({applicantName})");
                        if (__3 != null)
                            PoisonDebugLogger.Log($"  (applier가 플레이어가 아님: IsPlayer={__3.IsPlayer}, Player={__3.Player})");
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
                    // 독 대량 감소: 몬스터 사망으로 독이 한번에 제거되었을 수 있음
                    // 이 경우에도 마지막 틱 데미지(=oldAmount)를 기록
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
