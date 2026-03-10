using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace DamageMeterMod.Patches;

/// <summary>
/// 카드 사용 + 블록 획득 추적 패치.
///
/// 확인된 시그니처:
///   AfterCardPlayed(CombatState, PlayerChoiceContext, CardPlay)
///   AfterBlockGained(CombatState, Creature, Decimal amount, ValueProp, CardModel)
/// </summary>
public static class CardBlockPatches
{
    // 이미 AfterDamageGiven에서 기록된 카드인지 중복 방지용
    private static long _lastDamageCardTimestamp;

    public static void MarkDamageCardRecorded()
    {
        _lastDamageCardTimestamp = DateTime.UtcNow.Ticks;
    }

    // ---------------------------------------------------------------
    // 패치 8: AfterCardPlayed — 모든 카드 사용 후
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterCardPlayedPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterCardPlayed");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterCardPlayed not found");

            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterCardPlayed with {parameters.Length} params:");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }

            return method;
        }

        /// <summary>
        /// params: __2 = CardPlay (object, reflection으로 접근)
        /// CardPlay에서 CardModel과 Player를 추출.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(object __2)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__2 == null) return;

                var cardPlayType = __2.GetType();

                // CardPlay에서 CardModel 추출 (Card, CardModel, Model 등의 프로퍼티)
                object? cardModel = null;
                var cardProp = cardPlayType.GetProperty("Card")
                    ?? cardPlayType.GetProperty("CardModel")
                    ?? cardPlayType.GetProperty("Model");
                if (cardProp != null)
                    cardModel = cardProp.GetValue(__2);

                if (cardModel == null)
                {
                    // 프로퍼티 디버그 로깅 (최초 1회)
                    ModEntry.LogDebug($"[DamageMeter] CardPlay type: {cardPlayType.FullName}");
                    foreach (var prop in cardPlayType.GetProperties())
                    {
                        try
                        {
                            var val = prop.GetValue(__2);
                            ModEntry.LogDebug($"[DamageMeter]   CardPlayProp: {prop.Name} ({prop.PropertyType.Name}) = {val}");
                        }
                        catch { }
                    }
                    return;
                }

                // 카드 이름
                var cardModelType = cardModel.GetType();
                string cardName = "알 수 없음";
                var titleProp = cardModelType.GetProperty("Title");
                if (titleProp != null)
                    cardName = titleProp.GetValue(cardModel)?.ToString() ?? cardName;
                else
                {
                    var idProp = cardModelType.GetProperty("Id");
                    cardName = idProp?.GetValue(cardModel)?.ToString() ?? cardName;
                }

                // 카드 타입 (Attack, Skill, Power 등)
                string cardType = "Unknown";
                var typeProp = cardModelType.GetProperty("Type");
                if (typeProp != null)
                    cardType = typeProp.GetValue(cardModel)?.ToString() ?? cardType;

                // 소유자 플레이어
                var ownerProp = cardModelType.GetProperty("Owner");
                var ownerPlayer = ownerProp?.GetValue(cardModel);
                if (ownerPlayer == null) return;

                var netIdProp = ownerPlayer.GetType().GetProperty("NetId");
                string playerId = netIdProp?.GetValue(ownerPlayer)?.ToString() ?? "unknown";

                var creatureProp = ownerPlayer.GetType().GetProperty("Creature");
                var creature = creatureProp?.GetValue(ownerPlayer);
                string playerName = "알 수 없음";
                if (creature != null)
                {
                    var nameProp = creature.GetType().GetProperty("Name");
                    playerName = nameProp?.GetValue(creature)?.ToString() ?? playerName;
                }

                DamageTracker.Instance.EnsurePlayerRegistered(playerId, playerName);

                // 독 귀속용 마지막 행동 플레이어 갱신
                PoisonPatches.SetLastActingPlayer(playerId, playerName);

                // 카드 사용 로그 기록 (데미지 카드는 AfterDamageGiven에서 이미 기록)
                DamageTracker.Instance.RecordCardPlayed(
                    playerId, playerName, cardName, cardType);

                ModEntry.LogDebug(
                    $"[DamageMeter] 카드 사용: {playerName} [{cardName}] ({cardType})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterCardPlayed error: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    // 패치 9: AfterBlockGained — 블록(쉴드) 획득 후
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterBlockGainedPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterBlockGained");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterBlockGained not found");

            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterBlockGained with {parameters.Length} params:");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }

            return method;
        }

        /// <summary>
        /// Typed params:
        ///   __1 = Creature (블록을 얻은 대상)
        ///   __2 = Decimal (블록 양)
        ///   __4 = CardModel (object, 블록의 출처 카드)
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Creature __1, decimal __2, object __4)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;

                var creature = __1;
                if (creature == null || !creature.IsPlayer) return;

                int blockAmount = (int)__2;
                if (blockAmount <= 0) return;

                var player = creature.Player;
                if (player == null) return;

                string playerId = player.NetId.ToString();
                string playerName = creature.Name ?? "알 수 없음";

                // 카드 이름 추출
                string cardName = "알 수 없음";
                if (__4 != null)
                {
                    var titleProp = __4.GetType().GetProperty("Title");
                    if (titleProp != null)
                        cardName = titleProp.GetValue(__4)?.ToString() ?? cardName;
                    else
                    {
                        var idProp = __4.GetType().GetProperty("Id");
                        cardName = idProp?.GetValue(__4)?.ToString() ?? cardName;
                    }
                }

                DamageTracker.Instance.RecordBlockGained(
                    playerId, playerName, blockAmount, cardName);

                ModEntry.LogDebug(
                    $"[DamageMeter] 블록 획득: {playerName} +{blockAmount} ({cardName})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterBlockGained error: {ex.Message}");
            }
        }
    }
}
