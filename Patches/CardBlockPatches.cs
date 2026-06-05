using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization;

namespace DamageMeterMod.Patches;

/// <summary>
/// 카드 사용 + 블록 획득 + 카드 라이프사이클(소멸/버림) + 단조 + 파멸 추적 패치.
///
/// 확인된 시그니처 (스테이블/베타 양쪽):
///   AfterCardPlayed(ICombatState, PlayerChoiceContext, CardPlay)
///   AfterBlockGained(ICombatState, Creature, Decimal amount, ValueProp, CardModel)
///   AfterCardExhausted(ICombatState, PlayerChoiceContext, CardModel, Boolean) — 베타 전용
///   AfterCardDiscarded(ICombatState, PlayerChoiceContext, CardModel) — 베타 전용
///   AfterForge(ICombatState, Decimal, Player, AbstractModel) — 베타 전용
///   AfterDiedToDoom(ICombatState, IReadOnlyList&lt;Creature&gt;) — 베타 전용
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

                // 카드 이름 (TitleLocString → Id → fallback)
                var cardModelType = cardModel.GetType();
                string cardName = L10N.Unknown;
                var titleLocProp = cardModelType.GetProperty("TitleLocString");
                if (titleLocProp != null)
                {
                    var locString = titleLocProp.GetValue(cardModel) as LocString;
                    cardName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(locString) ?? cardName;
                }
                if (cardName == L10N.Unknown)
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
                if (ownerPlayer is not Player typedPlayer) return;

                string playerId = typedPlayer.NetId.ToString();
                string playerName = CombatPatches.GetPlayerDisplayName(typedPlayer);

                DamageTracker.Instance.EnsurePlayerRegistered(playerId, playerName);

                // 독 귀속용 마지막 행동 플레이어 갱신
                PoisonPatches.SetLastActingPlayer(playerId, playerName);
                DoomPatches.SetLastActingPlayer(playerId, playerName);

                // 에너지 비용 추출 (CardPlay.Resources.EnergySpent)
                int energyCost = 0;
                try
                {
                    var resProp = cardPlayType.GetProperty("Resources");
                    if (resProp != null)
                    {
                        var resources = resProp.GetValue(__2);
                        if (resources != null)
                        {
                            var esProp = resources.GetType().GetProperty("EnergySpent");
                            if (esProp != null)
                                energyCost = (int)(esProp.GetValue(resources) ?? 0);
                            else
                            {
                                var esField = resources.GetType().GetField("EnergySpent");
                                if (esField != null)
                                    energyCost = (int)(esField.GetValue(resources) ?? 0);
                            }
                        }
                    }
                }
                catch { }

                // 카드 사용 로그 기록 (데미지 카드는 AfterDamageGiven에서 이미 기록)
                DamageTracker.Instance.RecordCardPlayed(
                    playerId, playerName, cardName, cardType, energyCost);

                ModEntry.LogDebug(
                    $"[DamageMeter] 카드 사용: {playerName} [{cardName}] ({cardType}) E:{energyCost}");
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
        ///   __3 = ValueProp (enum — Unpowered=4 for non-card sources)
        ///   __4 = CardModel (블록의 출처 카드 — 유물/파워 효과 시 null)
        ///
        /// 출처 판별:
        ///   CardModel != null → 카드 이름 사용
        ///   CardModel == null → [효과] 로 표시 (유물/파워 구분 불가 — 훅에 정보 없음)
        ///   핵심은 "어떤 플레이어가 얻었는지"만 정확히 추적하는 것.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Creature __1, decimal __2, object __3, object __4)
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
                string playerName = CombatPatches.GetPlayerDisplayName(player);

                // 카드 출처가 있으면 카드 이름, 없으면 [효과]
                string sourceName = ExtractCardName(__4);

                DamageTracker.Instance.RecordBlockGained(
                    playerId, playerName, blockAmount, sourceName);

                ModEntry.LogDebug(
                    $"[DamageMeter] 블록 획득: {playerName} +{blockAmount} ({sourceName})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterBlockGained error: {ex.Message}");
            }
        }

        /// <summary>CardModel에서 카드 이름을 추출. null이면 [효과] 반환.</summary>
        private static string ExtractCardName(object? cardModel)
        {
            if (cardModel == null) return L10N.EffectLabel;

            var titleLocProp = cardModel.GetType().GetProperty("TitleLocString");
            if (titleLocProp != null)
            {
                var locString = titleLocProp.GetValue(cardModel) as LocString;
                var text = CombatPatches.AfterDamageGivenPatch.GetLocStringText(locString);
                if (!string.IsNullOrEmpty(text)) return text;
            }

            var idProp = cardModel.GetType().GetProperty("Id");
            var id = idProp?.GetValue(cardModel)?.ToString();
            return !string.IsNullOrEmpty(id) ? id : L10N.EffectLabel;
        }
    }

    // ---------------------------------------------------------------
    // 패치 10: AfterCardExhausted — 카드 소멸 후 (베타 전용)
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterCardExhaustedPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterCardExhausted");
            if (method == null)
            {
                ModEntry.LogWarning("[DamageMeter] Hook.AfterCardExhausted not found (may not exist in this version)");
                throw new InvalidOperationException("[DamageMeter] Hook.AfterCardExhausted not found");
            }

            ModEntry.Log($"[DamageMeter] Found Hook.AfterCardExhausted");
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix(CardModel __2, bool __3)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__2 == null) return;

                string cardName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(__2.TitleLocString)
                    ?? __2.Id?.ToString() ?? L10N.Unknown;

                var owner = __2.Owner;
                if (owner == null) return;

                string playerId = owner.NetId.ToString();
                string playerName = CombatPatches.GetPlayerDisplayName(owner);

                DamageTracker.Instance.RecordCardExhausted(playerId, playerName, cardName, __3);
                ModEntry.LogDebug($"[DamageMeter] 카드 소멸: {playerName} [{cardName}] (에테리얼:{__3})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterCardExhausted error: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    // 패치 11: AfterCardDiscarded — 카드 버림 후 (베타 전용)
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterCardDiscardedPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterCardDiscarded");
            if (method == null)
            {
                ModEntry.LogWarning("[DamageMeter] Hook.AfterCardDiscarded not found (may not exist in this version)");
                throw new InvalidOperationException("[DamageMeter] Hook.AfterCardDiscarded not found");
            }

            ModEntry.Log($"[DamageMeter] Found Hook.AfterCardDiscarded");
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix(CardModel __2)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__2 == null) return;

                string cardName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(__2.TitleLocString)
                    ?? __2.Id?.ToString() ?? L10N.Unknown;

                var owner = __2.Owner;
                if (owner == null) return;

                string playerId = owner.NetId.ToString();
                string playerName = CombatPatches.GetPlayerDisplayName(owner);

                DamageTracker.Instance.RecordCardDiscarded(playerId, playerName, cardName);
                ModEntry.LogDebug($"[DamageMeter] 카드 버림: {playerName} [{cardName}]");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterCardDiscarded error: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------
    // 패치 12: AfterForge — 단조 후 (베타 전용)
    // ---------------------------------------------------------------
    [HarmonyPatch]
    public static class AfterForgePatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterForge");
            if (method == null)
            {
                ModEntry.LogWarning("[DamageMeter] Hook.AfterForge not found (may not exist in this version)");
                throw new InvalidOperationException("[DamageMeter] Hook.AfterForge not found");
            }

            ModEntry.Log($"[DamageMeter] Found Hook.AfterForge");
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix(decimal __1, Player __2, object __3)
        {
            try
            {
                if (!DamageTracker.Instance.IsActive) return;
                if (__2 == null) return;

                int amount = (int)__1;
                if (amount <= 0) return;

                string playerId = __2.NetId.ToString();
                string playerName = CombatPatches.GetPlayerDisplayName(__2);

                // 단조 소스 이름 추출
                string sourceName = L10N.Unknown;
                if (__3 != null)
                {
                    var titleProp = __3.GetType().GetProperty("TitleLocString");
                    if (titleProp != null)
                    {
                        var locString = titleProp.GetValue(__3) as LocString;
                        sourceName = CombatPatches.AfterDamageGivenPatch.GetLocStringText(locString) ?? sourceName;
                    }
                    if (sourceName == L10N.Unknown)
                    {
                        var idProp = __3.GetType().GetProperty("Id");
                        sourceName = idProp?.GetValue(__3)?.ToString() ?? sourceName;
                    }
                }

                DamageTracker.Instance.RecordForge(playerId, playerName, amount, sourceName);
                ModEntry.LogDebug($"[DamageMeter] 단조: {playerName} +{amount} ({sourceName})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterForge error: {ex.Message}");
            }
        }
    }

}
