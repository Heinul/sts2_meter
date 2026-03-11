using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Localization;

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

            // 파라미터 시그니처 로깅 (디버그용)
            var parameters = method.GetParameters();
            ModEntry.Log($"[DamageMeter] Found Hook.AfterDamageGiven with {parameters.Length} params:");
            foreach (var p in parameters)
            {
                ModEntry.Log($"[DamageMeter]   param[{p.Position}]: {p.ParameterType.FullName} {p.Name}");
            }
            return method;
        }

        /// <summary>
        /// Harmony Postfix — Hook.AfterDamageGiven 실행 후 호출.
        /// __2 = Creature (dealer), __3 = DamageResult (results),
        /// __4 = ValueProp (데미지 출처 정보 — 유물/파워 등),
        /// __5 = Creature (target), __6 = CardModel.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Creature __2, DamageResult __3, object __4, Creature __5, CardModel __6)
        {
            try
            {
                var dealer = __2;
                var results = __3;
                var target = __5;

                if (dealer == null) return;

                // 데미지 추출 (TotalDamage = Blocked + Unblocked)
                int damage = results.TotalDamage;
                if (damage <= 0) return;

                // 플레이어 식별: 직접 플레이어 또는 소환수(Pet)의 주인
                Player? player = null;
                string displayName;

                if (dealer.IsPlayer)
                {
                    player = dealer.Player;
                    displayName = dealer.Name ?? $"Player_{player?.NetId}";
                }
                else if (dealer.IsPet && dealer.PetOwner != null)
                {
                    player = dealer.PetOwner;
                    displayName = player.Creature?.Name ?? $"Player_{player.NetId}";
                    ModEntry.LogDebug(
                        $"[DamageMeter] Pet damage: {dealer.Name} (owner: {displayName}) → {target?.Name} {damage} dmg");
                }
                else
                {
                    return;
                }

                if (player == null) return;

                string playerId = player.NetId.ToString();

                // 전투 중 새 플레이어가 감지되면 자동 등록
                DamageTracker.Instance.EnsurePlayerRegistered(playerId, displayName);
                DamageTracker.Instance.RecordDamage(playerId, damage);

                // 독 귀속을 위해 마지막 행동 플레이어 기록
                PoisonPatches.SetLastActingPlayer(playerId, displayName);

                // 카드 이름 추출
                string cardName = L10N.Unknown;
                try
                {
                    if (__6 != null)
                    {
                        // 1) LocString 기반 로컬라이즈된 이름 (게임 언어 설정 반영)
                        cardName = GetLocStringText(__6.TitleLocString) ?? cardName;

                        // 2) Id 프로퍼티 fallback
                        if (cardName == L10N.Unknown)
                        {
                            cardName = __6.Id?.ToString() ?? cardName;
                        }

                        // 3) 클래스명에서 추출 fallback
                        if (cardName == L10N.Unknown)
                        {
                            cardName = CardNameMap.GetReadableName(__6.GetType());
                        }
                    }
                    else
                    {
                        // CardModel이 null → 비카드 데미지 (파워/렐릭/포션 등)
                        cardName = ExtractNonCardDamageSource(__4, dealer);
                    }

                    if (cardName == L10N.Unknown)
                    {
                        ModEntry.LogDebug($"[DamageMeter] 소스 추출 실패 - CardModel: {__6 != null}, ValueProp: {__4}");
                    }
                }
                catch (Exception cardEx)
                {
                    ModEntry.LogDebug($"[DamageMeter] CardModel/ValueProp error: {cardEx.Message}");
                }

                // CardModel 참조 캐시 (호버 팁용)
                if (__6 != null && cardName != L10N.Unknown)
                {
                    DamageTracker.Instance.CacheCardModel(cardName, __6);
                }

                string targetName = target?.Name ?? L10N.Unknown;

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

        /// <summary>
        /// LocString에서 로컬라이즈된 텍스트를 안전하게 추출.
        /// GetFormattedText() → GetRawText() → ToString() 순으로 시도.
        /// </summary>
        internal static string? GetLocStringText(LocString? locString)
        {
            if (locString == null) return null;
            try
            {
                var text = locString.GetFormattedText();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch { }
            try
            {
                var text = locString.GetRawText();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch { }
            var fallback = locString.ToString();
            return string.IsNullOrEmpty(fallback) ? null : fallback;
        }

        /// <summary>
        /// 비카드 데미지의 출처를 추출.
        /// Creature.Powers와 Player.Relics를 직접 스캔하여 소스를 추론.
        /// </summary>
        private static string ExtractNonCardDamageSource(object valueProp, Creature dealer)
        {
            var vpStr = valueProp?.ToString() ?? "";
            ModEntry.LogDebug($"[DamageMeter] Non-card damage. ValueProp={vpStr}, Dealer={dealer?.Name}");

            if (dealer == null) return L10N.EffectLabel;

            try
            {
                // 1) dealer의 파워 목록에서 데미지를 주는 파워 탐색
                var powers = dealer.Powers;
                if (powers != null)
                {
                    foreach (var power in powers)
                    {
                        if (power == null || !power.IsVisible) continue;

                        // Amount > 0인 파워 = 데미지를 줄 수 있는 파워
                        if (power.Amount > 0)
                        {
                            var title = GetLocStringText(power.Title);
                            if (!string.IsNullOrEmpty(title))
                            {
                                ModEntry.LogDebug($"[DamageMeter] Power source: {title} (Amount={power.Amount})");
                                return L10N.PowerPrefix(title);
                            }

                            // Title이 없으면 클래스명에서 추출
                            var readable = CardNameMap.GetReadableName(power.GetType());
                            if (readable != L10N.Unknown)
                            {
                                ModEntry.LogDebug($"[DamageMeter] Power source (by type): {readable}");
                                return L10N.PowerPrefix(readable);
                            }
                        }
                    }
                }

                // 2) Player의 렐릭 목록 스캔
                var player = dealer.Player ?? (dealer.IsPet ? dealer.PetOwner : null);
                if (player?.Relics != null)
                {
                    foreach (var relic in player.Relics)
                    {
                        if (relic == null) continue;

                        var title = GetLocStringText(relic.Title);
                        if (!string.IsNullOrEmpty(title))
                        {
                            ModEntry.LogDebug($"[DamageMeter] Relic candidate: {title}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogDebug($"[DamageMeter] Power/Relic scan error: {ex.Message}");
            }

            // 3) 소스를 특정하지 못한 경우 일반 라벨
            return L10N.EffectLabel;
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
        public static void Postfix(object __0, CombatState __1)
        {
            try
            {
                var combatState = __1;
                if (combatState == null) return;

                // 런 변경 감지 (IRunState 참조 비교)
                DamageTracker.Instance.CheckRunChange(__0);

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

                PoisonPatches.ResetTracking();
                DamageTracker.Instance.StartCombat(playerList);

                // 로컬 플레이어 탐지
                DetectLocalPlayer(combatState, playerList);

                ModEntry.Log($"[DamageMeter] Combat started. Tracking {playerList.Count} player(s): " +
                    string.Join(", ", playerList.Select(p => p.name)) +
                    $" | LocalPlayer: {DamageTracker.Instance.LocalPlayerId}");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] BeforeCombatStart error: {ex.Message}");
            }
        }

        /// <summary>
        /// 로컬 플레이어를 탐지하여 DamageTracker.LocalPlayerId에 설정.
        /// 탐지 순서:
        ///   1) 솔로면 유일한 플레이어
        ///   2) CombatState에서 LocalPlayerId / LocalPlayer / MyPlayer 프로퍼티 검색
        ///   3) Player 객체에서 IsLocal/IsMe/IsOwner/IsMine 프로퍼티 검색
        ///   4) CombatState에서 LocalPlayerIndex 등 인덱스 기반 검색
        ///   5) fallback: 첫 번째 플레이어
        /// </summary>
        private static void DetectLocalPlayer(CombatState combatState, List<(string id, string name)> playerList)
        {
            try
            {
                // 솔로면 바로 설정
                if (playerList.Count <= 1)
                {
                    if (playerList.Count == 1)
                        DamageTracker.Instance.LocalPlayerId = playerList[0].id;
                    return;
                }

                // 이미 설정되어 있고 유효하면 유지
                var current = DamageTracker.Instance.LocalPlayerId;
                if (!string.IsNullOrEmpty(current) && playerList.Any(p => p.id == current))
                    return;

                var csType = combatState.GetType();
                var players = combatState.Players;

                // 1) CombatState에서 LocalPlayerId 검색
                foreach (var propName in new[] { "LocalPlayerId", "LocalPlayer", "MyPlayer", "LocalNetId" })
                {
                    var prop = csType.GetProperty(propName);
                    if (prop != null)
                    {
                        var val = prop.GetValue(combatState);
                        if (val != null)
                        {
                            string localId = val.ToString() ?? "";
                            // Player 객체면 NetId 추출
                            var netIdProp = val.GetType().GetProperty("NetId");
                            if (netIdProp != null)
                                localId = netIdProp.GetValue(val)?.ToString() ?? localId;

                            if (playerList.Any(p => p.id == localId))
                            {
                                DamageTracker.Instance.LocalPlayerId = localId;
                                ModEntry.Log($"[DamageMeter] Local player detected via CombatState.{propName}: {localId}");
                                return;
                            }
                        }
                    }
                }

                // 2) Player 객체에서 IsLocal 등 검색
                if (players != null)
                {
                    foreach (var player in players)
                    {
                        if (player == null) continue;
                        var pType = player.GetType();

                        foreach (var propName in new[] { "IsLocal", "IsMe", "IsOwner", "IsMine", "IsHost" })
                        {
                            var prop = pType.GetProperty(propName);
                            if (prop != null && prop.PropertyType == typeof(bool))
                            {
                                bool isLocal = (bool)(prop.GetValue(player) ?? false);
                                if (isLocal)
                                {
                                    string id = player.NetId.ToString();
                                    DamageTracker.Instance.LocalPlayerId = id;
                                    ModEntry.Log($"[DamageMeter] Local player detected via Player.{propName}: {id}");
                                    return;
                                }
                            }
                        }
                    }
                }

                // 3) CombatState에서 인덱스 기반 검색
                foreach (var propName in new[] { "LocalPlayerIndex", "MyPlayerIndex" })
                {
                    var prop = csType.GetProperty(propName);
                    if (prop != null && (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(long)))
                    {
                        var val = prop.GetValue(combatState);
                        if (val != null)
                        {
                            int idx = Convert.ToInt32(val);
                            if (idx >= 0 && idx < playerList.Count)
                            {
                                DamageTracker.Instance.LocalPlayerId = playerList[idx].id;
                                ModEntry.Log($"[DamageMeter] Local player detected via CombatState.{propName}={idx}: {playerList[idx].id}");
                                return;
                            }
                        }
                    }
                }

                // 디버그: CombatState + Player 프로퍼티 로깅 (최초 1회)
                if (!_playerPropsLogged)
                {
                    _playerPropsLogged = true;

                    // CombatState 프로퍼티 로깅
                    ModEntry.Log($"[DamageMeter] CombatState type: {csType.FullName}");
                    foreach (var prop in csType.GetProperties())
                    {
                        try
                        {
                            var val = prop.GetValue(combatState);
                            ModEntry.Log($"[DamageMeter]   CS.{prop.Name} ({prop.PropertyType.Name}) = {val}");
                        }
                        catch { }
                    }

                    // Player 프로퍼티 로깅
                    if (players != null && players.Count > 0)
                    {
                        var firstPlayer = players[0];
                        if (firstPlayer != null)
                        {
                            ModEntry.Log($"[DamageMeter] Player type: {firstPlayer.GetType().FullName}");
                            foreach (var prop in firstPlayer.GetType().GetProperties())
                            {
                                try
                                {
                                    var val = prop.GetValue(firstPlayer);
                                    ModEntry.Log($"[DamageMeter]   Player.{prop.Name} ({prop.PropertyType.Name}) = {val}");
                                }
                                catch { }
                            }
                        }
                    }
                }

                // 4) fallback: 첫 번째 플레이어
                DamageTracker.Instance.LocalPlayerId = playerList[0].id;
                ModEntry.Log($"[DamageMeter] Local player fallback to first: {playerList[0].id} ({playerList[0].name})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] DetectLocalPlayer error: {ex.Message}");
                if (playerList.Count > 0)
                    DamageTracker.Instance.LocalPlayerId = playerList[0].id;
            }
        }

        private static bool _playerPropsLogged;
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
                DamageTracker.Instance.EndCombat();
                ModEntry.Log("[DamageMeter] Combat ended. Final stats preserved.");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterCombatEnd error: {ex.Message}");
            }
        }
    }

    /// ---------------------------------------------------------------
    /// 패치 3b: AfterCardPlayed — 카드 사용 후 (모든 카드 — 공격/방어/파워/스킬)
    /// ---------------------------------------------------------------
    /// 파라미터 순서:
    ///   0: CombatState
    ///   1: PlayerChoiceContext
    ///   2: CardPlay  (CardPlay.Card = CardModel, CardPlay.Target = Creature)
    ///
    /// CardModel 참조를 캐시하여 호버 팁에서 사용.
    /// AfterDamageGiven에서는 공격카드만 캐시되므로, 여기서 모든 카드를 캐시.
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

            ModEntry.Log("[DamageMeter] Found Hook.AfterCardPlayed");
            return method;
        }

        /// <summary>
        /// __2 = CardPlay (Card, Target, PlayIndex 등)
        /// CardPlay.Card에서 CardModel 참조를 가져와 캐시.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(object __2)
        {
            try
            {
                if (__2 == null) return;

                // CardPlay.Card → CardModel
                var cardProp = __2.GetType().GetProperty("Card");
                if (cardProp == null) return;

                var cardModel = cardProp.GetValue(__2) as CardModel;
                if (cardModel == null) return;

                // LocString 기반 로컬라이즈된 이름
                string cardName = AfterDamageGivenPatch.GetLocStringText(cardModel.TitleLocString) ?? L10N.Unknown;

                if (cardName == L10N.Unknown)
                    cardName = cardModel.Id?.ToString() ?? L10N.Unknown;

                if (cardName == L10N.Unknown)
                    cardName = CardNameMap.GetReadableName(cardModel.GetType());

                if (cardName != L10N.Unknown)
                {
                    DamageTracker.Instance.CacheCardModel(cardName, cardModel);
                    ModEntry.LogDebug($"[DamageMeter] CardModel cached via AfterCardPlayed: {cardName}");
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogDebug($"[DamageMeter] AfterCardPlayed cache error: {ex.Message}");
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

        /// <summary>
        /// __1 = CombatSide (enum: Player=0, Monster=1 등)
        /// 플레이어턴 + 적턴 = 1라운드. 적턴 종료 시에만 턴 카운트 증가.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(object __1)
        {
            try
            {
                // CombatSide를 문자열로 변환하여 판별
                string side = __1?.ToString() ?? "";

                // 디버그: CombatSide 값 로깅 (최초 2회)
                if (_turnEndLogCount < 2)
                {
                    _turnEndLogCount++;
                    ModEntry.Log($"[DamageMeter] AfterTurnEnd CombatSide = '{side}' (type: {__1?.GetType().FullName})");
                }

                // 적 턴 종료 시에만 라운드 증가 (플레이어턴 + 적턴 = 1턴)
                // CombatSide enum: "Player" 또는 "Monster"/"Enemy" 등
                bool isPlayerTurnEnd = side.Contains("Player", StringComparison.OrdinalIgnoreCase);

                if (isPlayerTurnEnd)
                {
                    // 플레이어 턴 종료 → 턴당 데미지만 리셋 (라운드 카운트 올리지 않음)
                    ModEntry.LogDebug($"[DamageMeter] Player turn ended (turn {DamageTracker.Instance.CombatTurn})");
                    return;
                }

                // 적 턴 종료 → 라운드 카운트 증가 + 턴당 데미지 리셋
                DamageTracker.Instance.OnTurnEnd();
                ModEntry.LogDebug($"[DamageMeter] Round ended. Now turn {DamageTracker.Instance.CombatTurn}");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterTurnEnd error: {ex.Message}");
            }
        }

        private static int _turnEndLogCount;
    }
}
