using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using DamageMeterMod.Persistence;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace DamageMeterMod.Patches;

/// <summary>
/// sts2.dll v0.98 디컴파일 결과 기반 Harmony 패치.
///
/// Hook 시스템: MegaCrit.Sts2.Core.Hooks.Hook
///
/// 확인된 시그니처 (System.Reflection.Metadata 기반):
///   AfterDamageGiven(PlayerChoiceContext, CombatState, Creature dealer, DamageResult results, ValueProp, Creature target, CardModel)
///   BeforeCombatStart(IRunState, CombatState)
///   AfterCombatEnd(IRunState, CombatState, CombatRoom)
///   AfterTurnEnd(CombatState, CombatSide)
///
/// 주요 타입 구조:
///   DamageResult → TotalDamage, UnblockedDamage, BlockedDamage, OverkillDamage, WasTargetKilled
///   Creature     → IsPlayer, Player, Name, IsMonster, Side, CombatState
///   Player       → NetId (UInt64), Character (CharacterModel), Creature
///   CombatState  → Players (IReadOnlyList&lt;Player&gt;), RoundNumber, CurrentSide
/// </summary>
public static class CombatPatches
{
    /// ---------------------------------------------------------------
    /// 패치 1: AfterDamageGiven — 데미지가 적용된 후
    /// ---------------------------------------------------------------
    /// 파라미터 순서 (0-indexed):
    ///   0: PlayerChoiceContext  (MegaCrit.Sts2.Core.GameActions.Multiplayer)
    ///   1: CombatState          (MegaCrit.Sts2.Core.Combat)
    ///   2: Creature             (dealer — 데미지를 가한 엔티티)
    ///   3: DamageResult         (results — 데미지 결과)
    ///   4: ValueProp            (MegaCrit.Sts2.Core.ValueProps)
    ///   5: Creature             (target — 데미지를 받은 엔티티)
    ///   6: CardModel            (MegaCrit.Sts2.Core.Models)
    [HarmonyPatch]
    public static class AfterDamageGivenPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found in sts2.dll");

            var method = AccessTools.Method(hookType, "AfterDamageGiven");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterDamageGiven not found");

            ModEntry.Log($"[DamageMeter] Found Hook.AfterDamageGiven → {method.DeclaringType?.FullName}.{method.Name}");
            return method;
        }

        /// <summary>
        /// Harmony Postfix — Hook.AfterDamageGiven 실행 후 호출.
        /// __2 = Creature (dealer), __3 = DamageResult (results),
        /// __5 = Creature (target), __6 = CardModel.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Creature __2, DamageResult __3, Creature __5, object __6)
        {
            try
            {
                var dealer = __2;
                var results = __3;
                var target = __5;

                // dealer가 null이거나 플레이어가 아니면 무시
                if (dealer == null) return;
                if (!dealer.IsPlayer) return;

                // 데미지 추출 (TotalDamage = Blocked + Unblocked)
                int damage = results.TotalDamage;
                if (damage <= 0) return;

                // 플레이어 식별: Player.NetId (멀티플레이 고유 ID)
                var player = dealer.Player;
                if (player == null) return;

                string playerId = player.NetId.ToString();
                string displayName = dealer.Name ?? $"Player_{playerId}";

                // 전투 중 새 플레이어가 감지되면 자동 등록
                DamageTracker.Instance.EnsurePlayerRegistered(playerId, displayName);
                DamageTracker.Instance.RecordDamage(playerId, damage);

                // 독 귀속을 위해 마지막 행동 플레이어 기록
                PoisonPatches.SetLastActingPlayer(playerId, displayName);

                // 카드 이름 추출 (CardModel.Name, reflection 사용)
                string cardName = "알 수 없음";
                try
                {
                    if (__6 != null)
                    {
                        var nameProperty = __6.GetType().GetProperty("Name");
                        cardName = nameProperty?.GetValue(__6)?.ToString() ?? cardName;
                    }
                }
                catch { /* 카드 이름은 비필수 */ }

                string targetName = target?.Name ?? "알 수 없음";

                // 카드 데미지 로그 기록
                DamageTracker.Instance.RecordCardDamage(
                    playerId, displayName, cardName, targetName,
                    results.TotalDamage, results.UnblockedDamage,
                    results.BlockedDamage, results.WasTargetKilled);

                ModEntry.LogDebug(
                    $"[DamageMeter] {displayName} [{cardName}] → {targetName} {damage} dmg " +
                    $"(Blocked:{results.BlockedDamage} Kill:{results.WasTargetKilled})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterDamageGiven error: {ex.Message}");
            }
        }
    }

    /// ---------------------------------------------------------------
    /// 패치 2: BeforeCombatStart — 전투 시작 전 초기화
    /// ---------------------------------------------------------------
    /// 파라미터 순서:
    ///   0: IRunState    (MegaCrit.Sts2.Core.Run)
    ///   1: CombatState  (MegaCrit.Sts2.Core.Combat)
    [HarmonyPatch]
    public static class BeforeCombatStartPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "BeforeCombatStart");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.BeforeCombatStart not found");

            ModEntry.Log($"[DamageMeter] Found Hook.BeforeCombatStart");
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix(CombatState __1)
        {
            try
            {
                var combatState = __1;
                if (combatState == null) return;

                var playerList = new List<(string id, string name)>();

                // CombatState.Players → IReadOnlyList<Player>
                var players = combatState.Players;
                if (players != null)
                {
                    foreach (var player in players)
                    {
                        if (player == null) continue;

                        string id = player.NetId.ToString();

                        // 표시 이름: Player.Creature.Name 또는 fallback
                        string name = player.Creature?.Name
                            ?? $"Player_{id}";

                        playerList.Add((id, name));
                    }
                }

                // 플레이어를 찾지 못한 경우 기본값
                if (playerList.Count == 0)
                {
                    ModEntry.LogWarning("[DamageMeter] No players found in CombatState. Using fallback.");
                    playerList.Add(("local_player", "You"));
                }

                DamageTracker.Instance.StartCombat(playerList);
                ModEntry.Log($"[DamageMeter] Combat started. Tracking {playerList.Count} player(s): " +
                    string.Join(", ", playerList.Select(p => p.name)));
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] BeforeCombatStart error: {ex.Message}");
            }
        }
    }

    /// ---------------------------------------------------------------
    /// 패치 3: AfterCombatEnd — 전투 종료
    /// ---------------------------------------------------------------
    /// 파라미터 순서:
    ///   0: IRunState
    ///   1: CombatState
    ///   2: CombatRoom
    [HarmonyPatch]
    public static class AfterCombatEndPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterCombatEnd");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterCombatEnd not found");

            ModEntry.Log("[DamageMeter] Found Hook.AfterCombatEnd");
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                // 전투 종료 전에 요약 생성 (IsActive=true인 상태에서)
                var summary = DamageTracker.Instance.BuildCombatSummary();
                DamageTracker.Instance.EndCombat();

                // 전투 기록 저장
                if (summary.TotalDamageDealt > 0)
                {
                    new CombatHistoryStore().SaveCombat(summary);
                }

                ModEntry.Log("[DamageMeter] Combat ended. Final stats preserved.");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterCombatEnd error: {ex.Message}");
            }
        }
    }

    /// ---------------------------------------------------------------
    /// 패치 4: AfterTurnEnd — 턴 종료
    /// ---------------------------------------------------------------
    /// 파라미터 순서:
    ///   0: CombatState
    ///   1: CombatSide
    [HarmonyPatch]
    public static class AfterTurnEndPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
                throw new InvalidOperationException("[DamageMeter] Hook type not found");

            var method = AccessTools.Method(hookType, "AfterTurnEnd");
            if (method == null)
                throw new InvalidOperationException("[DamageMeter] Hook.AfterTurnEnd not found");

            ModEntry.Log("[DamageMeter] Found Hook.AfterTurnEnd");
            return method;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                DamageTracker.Instance.OnTurnEnd();
                ModEntry.LogDebug($"[DamageMeter] Turn ended. Now turn {DamageTracker.Instance.CombatTurn}");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterTurnEnd error: {ex.Message}");
            }
        }
    }
}
