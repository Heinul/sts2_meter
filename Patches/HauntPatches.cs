using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DamageMeterMod.Patches;

/// <summary>
/// 출몰(Haunt) 데미지 추적 패치.
/// HauntPower는 영혼(Soul) 카드를 낼 때마다 무작위 적에게 Amount(6/8)만큼
/// CreatureCmd.Damage를 호출하는데, dealer=null로 넘겨서 AfterDamageGiven에서
/// 누락됨(독과 동일 구조). 전용 경로로 출몰 소유 플레이어에게 귀속.
///
/// 제약: 대상(target)은 메서드 내부 RNG 지역변수라 Postfix에서 알 수 없음.
///   → 데미지량만 기록, 카드로그 대상은 "무작위 적" 라벨.
/// </summary>
public static class HauntPatches
{
    private const string HauntPowerTypeName = "MegaCrit.Sts2.Core.Models.Powers.HauntPower";

    [HarmonyPatch]
    public static class AfterCardPlayedPatch
    {
        [HarmonyPrepare]
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName(HauntPowerTypeName);
            return t != null && AccessTools.Method(t, "AfterCardPlayed") != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName(HauntPowerTypeName);
            return AccessTools.Method(t, "AfterCardPlayed")!;
        }

        /// <summary>
        /// __instance = HauntPower, __1 = CardPlay.
        /// 영혼 카드 플레이 시에만 발동(게임 내부 로직과 동일 가드).
        /// async Task라 Postfix가 데미지 적용 전 실행될 수 있으나,
        /// Unblockable이라 적이 있으면 무조건 Amount만큼 들어가므로 기록 정확.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(object __instance, object __1)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__instance is not PowerModel power) return;
                if (__1 == null) return;

                // CardPlay.Card 가 Soul 인지 (게임 내부: cardPlay.Card is Soul)
                var card = __1.GetType().GetProperty("Card")?.GetValue(__1);
                if (card == null || card.GetType().Name != "Soul") return;

                // 소유자 일치 확인 (Card.Owner.Creature == power.Owner)
                var ownerCreature = power.Owner;
                if (ownerCreature == null || !ownerCreature.IsPlayer || ownerCreature.Player == null) return;

                // 적이 없으면 데미지 미발생 → 기록 안 함
                if (!HasHittableEnemy(power)) return;

                int amount = power.Amount;
                if (amount <= 0) return;

                string playerId = ownerCreature.Player.NetId.ToString();
                string playerName = CombatPatches.GetPlayerDisplayName(ownerCreature.Player);

                // 미터 합산 + 카드로그 기록 (대상은 RNG라 알 수 없어 "무작위 적")
                DamageTracker.Instance.RecordDamage(playerId, amount);
                DamageTracker.Instance.RecordCardDamage(
                    playerId, playerName, L10N.HauntSource, L10N.HauntTarget,
                    amount, amount, 0, wasKill: false);

                ModEntry.LogDebug($"[DamageMeter] 출몰: {playerName} +{amount} (영혼 발동)");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] Haunt AfterCardPlayed error: {ex.Message}");
            }
        }

        /// <summary>power.CombatState.HittableEnemies 에 적이 있는지 (리플렉션, 인터페이스 변동 대비).</summary>
        private static bool HasHittableEnemy(PowerModel power)
        {
            try
            {
                var cs = power.GetType().GetProperty("CombatState")?.GetValue(power);
                if (cs == null) return true; // 확인 불가 시 보수적으로 통과
                var he = cs.GetType().GetProperty("HittableEnemies")?.GetValue(cs);
                if (he is System.Collections.ICollection col) return col.Count > 0;
                if (he is System.Collections.IEnumerable en)
                {
                    foreach (var _ in en) return true;
                    return false;
                }
                return true;
            }
            catch { return true; }
        }
    }
}
