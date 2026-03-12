using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

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
                string cardName = L10N.Unknown;
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
                string playerName = L10N.Unknown;
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
        ///   __3 = ValueProp (블록 출처 정보, 파워 등)
        ///   __4 = CardModel (object, 블록의 출처 카드 — 파워 효과 시 null)
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
                string playerName = creature.Name ?? L10N.Unknown;

                // 카드 이름 추출
                string cardName = ExtractCardName(__4);

                // CardModel이 null이면 ValueProp(__3)에서 출처 추출 시도
                // (파워 효과로 블록을 얻는 경우: 잔상, 메탈화 등)
                if (cardName == L10N.Unknown && __3 != null)
                {
                    cardName = ExtractSourceFromValueProp(__3);
                }

                // 여전히 출처 불명(Unpowered/Unknown)이면 플레이어의 파워/유물 스캔
                if (cardName == L10N.Unknown || cardName == "Unpowered")
                {
                    var scannedSource = ScanBlockSourceFromCreature(creature, blockAmount);
                    if (scannedSource != null)
                        cardName = scannedSource;
                }

                DamageTracker.Instance.RecordBlockGained(
                    playerId, playerName, blockAmount, cardName);

                // 그래도 출처 불명인 경우 상세 디버그 덤프
                if (__4 == null && __3 != null && (cardName == L10N.Unknown || cardName == "Unpowered"))
                {
                    LogUnpoweredBlockDebug(__3, blockAmount, playerName);
                }

                ModEntry.LogDebug(
                    $"[DamageMeter] 블록 획득: {playerName} +{blockAmount} ({cardName})");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] AfterBlockGained error: {ex.Message}");
            }
        }

        // 블록을 주는 것으로 알려진 유물 타입명 → 블록량 매핑 (게임 데이터 기반)
        // 정확한 매칭이 안 될 수 있으므로, 유물 이름은 Title에서 추출
        private static readonly HashSet<string> KnownBlockRelicTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "AnchorRelic",           // 닻: 전투 시작 시 블록 10
            "CaptainsWheelRelic",    // 선장의 핸들: 3턴마다 블록
            "HornCleatRelic",        // 뿔 클리트
            "SelfFormingClayRelic",  // 자기 성형 점토
            "OrichalcumRelic",       // 오리칼쿰: 턴 종료 시 블록 없으면 6
            "ThreadAndNeedleRelic",  // 바늘과 실
        };

        // 블록을 주는 것으로 알려진 파워 타입명
        private static readonly HashSet<string> KnownBlockPowerTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "MetallicizePower",      // 메탈화: 턴 종료 시 블록
            "PlatedArmorPower",      // 철갑: 턴 종료 시 블록
            "AfterImagePower",       // 잔상: 카드 사용 시 블록 1
            "BarricadePower",        // 바리케이드: 블록 유지
            "BlurPower",             // 잔흔: 블록 유지
        };

        /// <summary>
        /// Creature의 파워/유물 목록을 스캔하여 블록 출처를 추론.
        /// CombatPatches.ExtractNonCardDamageSource와 동일한 패턴.
        /// </summary>
        private static string? ScanBlockSourceFromCreature(Creature creature, int blockAmount)
        {
            try
            {
                // 1) 플레이어 파워 중 블록을 주는 파워 확인
                var powers = creature.Powers;
                if (powers != null)
                {
                    foreach (var power in powers)
                    {
                        if (power == null) continue;

                        var powerTypeName = power.GetType().Name;

                        // 알려진 블록 파워 타입이면 바로 사용
                        if (KnownBlockPowerTypes.Contains(powerTypeName))
                        {
                            var title = CombatPatches.AfterDamageGivenPatch.GetLocStringText(power.Title);
                            if (!string.IsNullOrEmpty(title))
                            {
                                ModEntry.LogDebug($"[DamageMeter] Block from power: {title} (type={powerTypeName})");
                                return L10N.PowerPrefix(title);
                            }
                        }
                    }
                }

                // 2) 플레이어 유물 중 블록을 주는 유물 확인
                var player = creature.Player;
                if (player?.Relics != null)
                {
                    foreach (var relic in player.Relics)
                    {
                        if (relic == null) continue;

                        var relicTypeName = relic.GetType().Name;

                        // 알려진 블록 유물 타입이면 Title 추출
                        if (KnownBlockRelicTypes.Contains(relicTypeName))
                        {
                            // RelicModel에서 Title(LocString) 추출
                            var titleProp = relic.GetType().GetProperty("Title");
                            if (titleProp != null)
                            {
                                var titleObj = titleProp.GetValue(relic);
                                string? title = null;

                                // LocString 추출 시도
                                if (titleObj != null)
                                {
                                    try { title = (titleObj as dynamic)?.GetFormattedText(); } catch { }
                                    try { title ??= (titleObj as dynamic)?.GetRawText(); } catch { }
                                    title ??= titleObj.ToString();
                                }

                                if (!string.IsNullOrEmpty(title))
                                {
                                    ModEntry.LogDebug($"[DamageMeter] Block from relic: {title} (type={relicTypeName}, amount={blockAmount})");
                                    return L10N.RelicPrefix(title);
                                }
                            }

                            // Title 없으면 타입명에서 추출
                            var readable = CardNameMap.GetReadableName(relic.GetType());
                            if (readable != L10N.Unknown)
                            {
                                ModEntry.LogDebug($"[DamageMeter] Block from relic (by type): {readable}");
                                return L10N.RelicPrefix(readable);
                            }
                        }
                    }

                    // 3) 알려진 목록에 없어도 유물 전체를 로깅 (최초 1회)
                    LogRelicList(player);
                }
            }
            catch (Exception ex)
            {
                ModEntry.LogDebug($"[DamageMeter] ScanBlockSource error: {ex.Message}");
            }

            return null;
        }

        private static bool _relicListLogged;

        /// <summary>플레이어의 전체 유물 목록을 1회 로깅 (새 블록 유물 발견용).</summary>
        private static void LogRelicList(MegaCrit.Sts2.Core.Entities.Players.Player player)
        {
            if (_relicListLogged || player?.Relics == null) return;
            _relicListLogged = true;

            ModEntry.Log($"[DamageMeter] === Player Relics List ===");
            foreach (var relic in player.Relics)
            {
                if (relic == null) continue;
                var typeName = relic.GetType().Name;
                string? title = null;
                try
                {
                    var titleProp = relic.GetType().GetProperty("Title");
                    var titleObj = titleProp?.GetValue(relic);
                    if (titleObj != null)
                    {
                        try { title = (titleObj as dynamic)?.GetFormattedText(); } catch { }
                        try { title ??= (titleObj as dynamic)?.GetRawText(); } catch { }
                        title ??= titleObj.ToString();
                    }
                }
                catch { }
                ModEntry.Log($"[DamageMeter]   Relic: {typeName} → '{title ?? "null"}'");
            }
            ModEntry.Log($"[DamageMeter] === Relics End ===");
        }

        /// <summary>CardModel에서 카드 이름을 추출.</summary>
        private static string ExtractCardName(object? cardModel)
        {
            if (cardModel == null) return L10N.Unknown;

            var titleProp = cardModel.GetType().GetProperty("Title");
            if (titleProp != null)
            {
                var title = titleProp.GetValue(cardModel)?.ToString();
                if (!string.IsNullOrEmpty(title)) return title;
            }

            var idProp = cardModel.GetType().GetProperty("Id");
            var id = idProp?.GetValue(cardModel)?.ToString();
            return !string.IsNullOrEmpty(id) ? id : L10N.Unknown;
        }

        // 이름 추출용 프로퍼티 후보 목록
        private static readonly string[] NameProps = { "Name", "Title", "DisplayName", "Source", "SourceName", "Id" };

        // 타입 이름에서 추출했지만 의미 없는 이름 (제네릭 VP 이름)
        private static readonly HashSet<string> UnhelpfulTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Unpowered", "Default", "Base", "Generic", "Normal", "Standard", "Basic"
        };

        /// <summary>
        /// ValueProp에서 블록 출처(파워명, 유물명 등)를 깊게 추출.
        /// CombatPatches.ExtractSourceFromValueProp과 동일한 전략 사용.
        /// </summary>
        private static string ExtractSourceFromValueProp(object valueProp)
        {
            var vpType = valueProp.GetType();

            // 1) 직접 이름 프로퍼티
            var directName = TryExtractName(valueProp, vpType);
            if (directName != null) return directName;

            // 2) 타입 이름에서 추출 (의미 없는 제네릭 이름은 건너뜀)
            var typeName = vpType.Name;
            string? typeExtractedName = null;
            foreach (var suffix in new[] { "DamageValueProp", "BlockValueProp", "ValueProp",
                                            "DamageVP", "BlockVP", "VP", "Damage", "Effect" })
            {
                if (typeName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && typeName.Length > suffix.Length)
                {
                    var name = typeName[..^suffix.Length];
                    if (name.Length >= 2 && !UnhelpfulTypeNames.Contains(name))
                    {
                        ModEntry.LogDebug($"[DamageMeter] BlockVP type name → {name}");
                        return name;
                    }
                    typeExtractedName = name;
                    break;
                }
            }

            // 3) ToString()
            var str = valueProp.ToString();
            if (!string.IsNullOrEmpty(str) && str != vpType.FullName && str != "0"
                && (!str.Contains('.') || str.Length < 40))
                return str;

            // 4) 중첩 객체 (Power, Relic 등)
            foreach (var propName in new[] { "Power", "Relic", "Artifact", "Card", "CardModel",
                                              "Buff", "Effect", "StatusEffect", "Owner", "Parent" })
            {
                var prop = vpType.GetProperty(propName);
                if (prop == null) continue;
                try
                {
                    var nested = prop.GetValue(valueProp);
                    if (nested == null) continue;
                    var nestedName = TryExtractName(nested, nested.GetType());
                    if (nestedName != null)
                    {
                        ModEntry.LogDebug($"[DamageMeter] BlockVP.{propName}.Name → {nestedName}");
                        return nestedName;
                    }
                }
                catch { }
            }

            // 5) 모든 문자열 프로퍼티
            foreach (var prop in vpType.GetProperties())
            {
                try
                {
                    if (prop.PropertyType == typeof(string))
                    {
                        var val = prop.GetValue(valueProp) as string;
                        if (!string.IsNullOrEmpty(val) && val != "0" && val.Length > 1 && val.Length < 50
                            && !val.Contains('.') && !long.TryParse(val, out _))
                            return val;
                    }
                }
                catch { }
            }

            // 6) 모든 중첩 객체의 이름
            foreach (var prop in vpType.GetProperties())
            {
                try
                {
                    if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string)
                        || prop.PropertyType == typeof(decimal) || prop.PropertyType.IsEnum)
                        continue;
                    var nested = prop.GetValue(valueProp);
                    if (nested == null) continue;
                    var nestedName = TryExtractName(nested, nested.GetType());
                    if (nestedName != null)
                    {
                        ModEntry.LogDebug($"[DamageMeter] BlockVP.{prop.Name}.Name → {nestedName}");
                        return nestedName;
                    }
                }
                catch { }
            }

            // 디버그 덤프 (타입별 최초 1회)
            LogBlockVpDump(vpType, valueProp);

            // Unpowered 등 제네릭 타입인 경우 타입 이름이라도 반환
            if (typeExtractedName != null)
            {
                ModEntry.Log($"[DamageMeter] BlockVP fallback to type name: {typeExtractedName} (type={vpType.FullName})");
                return typeExtractedName;
            }

            return L10N.Unknown;
        }

        private static string? TryExtractName(object obj, Type type)
        {
            foreach (var propName in NameProps)
            {
                var prop = type.GetProperty(propName);
                if (prop == null) continue;
                try
                {
                    var val = prop.GetValue(obj)?.ToString();
                    if (!string.IsNullOrEmpty(val) && val != "0" && val.Length < 100
                        && !val.Contains("MegaCrit"))
                        return val;
                }
                catch { }
            }
            return null;
        }

        private static readonly HashSet<string> _loggedBlockVpTypes = new();

        private static void LogBlockVpDump(Type vpType, object valueProp)
        {
            var key = vpType.FullName ?? vpType.Name;
            if (!_loggedBlockVpTypes.Add(key)) return;

            ModEntry.Log($"[DamageMeter] === BlockVP Dump: {key} ===");
            foreach (var prop in vpType.GetProperties())
            {
                try
                {
                    var val = prop.GetValue(valueProp);
                    ModEntry.Log($"[DamageMeter]   BVP.{prop.Name} ({prop.PropertyType.Name}) = {val}");
                    if (val != null && !prop.PropertyType.IsPrimitive && prop.PropertyType != typeof(string)
                        && prop.PropertyType != typeof(decimal) && !prop.PropertyType.IsEnum)
                    {
                        foreach (var inner in val.GetType().GetProperties())
                        {
                            try
                            {
                                var iv = inner.GetValue(val);
                                ModEntry.Log($"[DamageMeter]     .{prop.Name}.{inner.Name} ({inner.PropertyType.Name}) = {iv}");
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            ModEntry.Log($"[DamageMeter] === BlockVP Dump End ===");
        }

        /// <summary>출처 불명(Unpowered) 블록의 상세 디버그. 매 발생 기록.</summary>
        private static readonly HashSet<string> _loggedUnpoweredTypes = new();

        private static void LogUnpoweredBlockDebug(object valueProp, int amount, string playerName)
        {
            var vpType = valueProp.GetType();
            var typeKey = vpType.FullName ?? vpType.Name;

            // 타입별 최초 1회만 전체 덤프
            if (_loggedUnpoweredTypes.Add(typeKey))
            {
                ModEntry.Log($"[DamageMeter] === Unpowered Block Debug: {typeKey} (amount={amount}, player={playerName}) ===");

                // 모든 프로퍼티 2단계 깊이로 덤프
                foreach (var prop in vpType.GetProperties())
                {
                    try
                    {
                        var val = prop.GetValue(valueProp);
                        ModEntry.Log($"[DamageMeter]   VP.{prop.Name} ({prop.PropertyType.Name}) = {val}");

                        // 중첩 객체 탐색
                        if (val != null && !prop.PropertyType.IsPrimitive && prop.PropertyType != typeof(string)
                            && prop.PropertyType != typeof(decimal) && !prop.PropertyType.IsEnum)
                        {
                            foreach (var inner in val.GetType().GetProperties())
                            {
                                try
                                {
                                    var iv = inner.GetValue(val);
                                    ModEntry.Log($"[DamageMeter]     .{prop.Name}.{inner.Name} ({inner.PropertyType.Name}) = {iv}");

                                    // 3단계 (유물/파워 내부)
                                    if (iv != null && !inner.PropertyType.IsPrimitive && inner.PropertyType != typeof(string)
                                        && inner.PropertyType != typeof(decimal) && !inner.PropertyType.IsEnum)
                                    {
                                        foreach (var deep in iv.GetType().GetProperties())
                                        {
                                            try
                                            {
                                                var dv = deep.GetValue(iv);
                                                ModEntry.Log($"[DamageMeter]       .{prop.Name}.{inner.Name}.{deep.Name} ({deep.PropertyType.Name}) = {dv}");
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                // 인터페이스 목록 (IRelicEffect 등 확인)
                var interfaces = vpType.GetInterfaces();
                if (interfaces.Length > 0)
                {
                    ModEntry.Log($"[DamageMeter]   Interfaces: {string.Join(", ", interfaces.Select(i => i.Name))}");
                }

                // 베이스 타입 체인
                var baseType = vpType.BaseType;
                var chain = new List<string>();
                while (baseType != null && baseType != typeof(object))
                {
                    chain.Add(baseType.FullName ?? baseType.Name);
                    baseType = baseType.BaseType;
                }
                if (chain.Count > 0)
                    ModEntry.Log($"[DamageMeter]   Inheritance: {string.Join(" → ", chain)}");

                ModEntry.Log($"[DamageMeter] === Unpowered Block Debug End ===");
            }
        }
    }
}
