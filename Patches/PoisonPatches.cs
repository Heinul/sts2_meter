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
/// 양쪽 호환: object[] __args에서 PowerModel/amount/applier를 타입 기준으로 찾아 접근.
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
        /// object[] __args로 전체 파라미터를 받아 오프셋으로 접근.
        /// 스테이블/베타 양쪽 호환.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(object[] __args)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;

                if (!TryGetPowerArgs(__args, out var power, out var amount, out var applier))
                    return;
                if (power == null) return;


                // === 파워 이름 확인 (LocString → 로컬라이즈된 이름) ===
                string powerName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(power.Title)
                    ?? power.GetType().Name;

                if (string.IsNullOrEmpty(powerName))
                {
                    return;
                }

                bool isPoisonMatch = powerName.Contains("Poison", StringComparison.OrdinalIgnoreCase) ||
                    powerName.Contains("독", StringComparison.OrdinalIgnoreCase);


                if (!isPoisonMatch) return;

                // === 파워 소유자(몬스터) 찾기 ===
                var ownerCreature = power.Owner;


                if (ownerCreature == null)
                {
                    ModEntry.LogDebug($"[DamageMeter] Poison PowerModel owner is null. Type: {power.GetType().FullName}");
                    return;
                }

                if (!ownerCreature.IsMonster)
                {
                    return;
                }

                string monsterName = ownerCreature.Name ?? "Unknown";
                string monsterKey = $"{monsterName}_{ownerCreature.GetHashCode()}";

                // === 이전 수치와 비교 ===
                int paramAmount = (int)amount;
                int modelAmount = (int)power.Amount;
                int currentAmount = modelAmount;
                _lastPoisonAmounts.TryGetValue(monsterKey, out int oldAmount);
                int diff = currentAmount - oldAmount;

                // 수치 갱신
                if (currentAmount <= 0)
                    _lastPoisonAmounts.Remove(monsterKey);
                else
                    _lastPoisonAmounts[monsterKey] = currentAmount;


                ModEntry.LogDebug(
                    $"[DamageMeter] 독 변화: {monsterName} {oldAmount}→{currentAmount} (diff={diff}, applier={applier?.Name})");

                if (diff > 0)
                {
                    // === 독 증가 → 적용 ===
                    string applicantId = _lastActingPlayerId;
                    string applicantName = _lastActingPlayerName;


                    if (applier != null && applier.IsPlayer && applier.Player != null)
                    {
                        applicantId = applier.Player.NetId.ToString();
                        applicantName = applier.Name ?? applicantId;
                    }
                    else
                    {
                    }

                    if (!string.IsNullOrEmpty(applicantId))
                    {
                        DamageTracker.Instance.RecordPoisonApplied(monsterKey, applicantId, diff);
                        ModEntry.LogDebug(
                            $"[DamageMeter] 독 적용: {applicantName} → {monsterName} +{diff} ({currentAmount}스택)");
                    }
                    else
                    {
                    }
                }
                else if (diff == -1 && oldAmount > 0)
                {
                    // === 독 틱: 데미지 = 감소 전 수치 (oldAmount) ===
                    int poisonDamage = oldAmount;
                    DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, poisonDamage);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 틱: {monsterName} {poisonDamage}뎀 (잔여 {currentAmount}스택)");
                }
                else if (diff < -1)
                {
                    int poisonDamage = oldAmount;
                    DamageTracker.Instance.RecordPoisonDamageTick(monsterKey, monsterName, poisonDamage);
                    ModEntry.LogDebug(
                        $"[DamageMeter] 독 킬/대량감소: {monsterName} {poisonDamage}뎀 ({oldAmount}→{currentAmount})");
                }
                else if (diff == 0)
                {
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterPowerAmountChanged error: {ex.Message}");
            }
        }

        private static bool TryGetPowerArgs(
            object[] args,
            out PowerModel? power,
            out decimal amount,
            out Creature? applier)
        {
            power = null;
            amount = 0m;
            applier = null;

            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is not PowerModel foundPower)
                    continue;

                power = foundPower;

                if (i + 1 < args.Length && args[i + 1] is decimal adjacentAmount)
                {
                    amount = adjacentAmount;
                }
                else
                {
                    for (int j = i + 1; j < args.Length; j++)
                    {
                        if (args[j] is decimal scannedAmount)
                        {
                            amount = scannedAmount;
                            break;
                        }
                    }
                }

                if (i + 2 < args.Length && args[i + 2] is Creature adjacentApplier)
                {
                    applier = adjacentApplier;
                }
                else
                {
                    for (int j = i + 1; j < args.Length; j++)
                    {
                        if (args[j] is Creature scannedApplier)
                        {
                            applier = scannedApplier;
                            break;
                        }
                    }
                }

                return true;
            }

            return false;
        }
    }
}
