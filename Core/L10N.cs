using System.Collections.Generic;

namespace DamageMeterMod.Core;

/// <summary>
/// Lightweight localization.
/// Auto-detects game language via Godot.TranslationServer.GetLocale().
/// English (en) default, Korean (ko) supported. To add a language: add case in GetStrings() + dictionary.
/// </summary>
public static class L10N
{
    private static readonly Dictionary<string, string> Strings;
    private static readonly string CurrentLocale;

    static L10N()
    {
        CurrentLocale = DetectLocale();
        Strings = GetStrings(CurrentLocale);
        ModEntry.Log($"[DamageMeter] L10N initialized: locale={CurrentLocale}");
    }

    private static string DetectLocale()
    {
        try
        {
            var locale = Godot.TranslationServer.GetLocale();
            if (!string.IsNullOrEmpty(locale) && locale.Length >= 2)
                return locale[..2].ToLowerInvariant();
        }
        catch { }
        return "en";
    }

    public static string Locale => CurrentLocale;

    private static string Get(string key) =>
        Strings.TryGetValue(key, out var val) ? val : key;

    // ===== 단순 프로퍼티 =====

    // 탭
    public static string TabMeter => Get("tab_meter");
    public static string TabCardLog => Get("tab_card_log");
    public static string TabReceived => Get("tab_received");

    // 타이틀
    public static string Title => Get("title");
    public static string TitleWaiting => Get("title_waiting");
    public static string TitleDone => Get("title_done");

    // 헤더
    public static string HeaderPlayer => Get("header_player");
    public static string HeaderDamage => Get("header_damage");
    public static string FilterAll => Get("filter_all");
    public static string FilterTurnLabel => Get("filter_turn");
    public static string FilterPlayerLabel => Get("filter_player");

    // 빈 상태
    public static string EmptyWaiting => Get("empty_waiting");
    public static string EmptyFilterNoMatch => Get("empty_filter_no_match");
    public static string EmptyCardLog => Get("empty_card_log");
    public static string EmptyReceived => Get("empty_received");

    // 처치/사망
    public static string Kill => Get("kill");
    public static string DeathBang => Get("death_bang");

    // 받은피해 섹션
    public static string ReceivedHeader => Get("received_header");

    // 툴팁
    public static string ResizeTooltip => Get("resize_tooltip");

    // 센티널/데이터
    public static string Unknown => Get("unknown");
    public static string Poison => Get("poison");
    public static string Death => Get("death");
    public static string EffectLabel => Get("effect_label");

    // ===== 포맷 메서드 =====

    public static string TitleTurn(int turn) =>
        string.Format(Get("title_turn_fmt"), turn);

    public static string Footer(int turn, string total) =>
        string.Format(Get("footer_fmt"), turn, total);

    public static string FooterInitial => Footer(0, "0");

    public static string CountItems(int count) =>
        string.Format(Get("count_items_fmt"), count);

    public static string DamageValue(string value) =>
        string.Format(Get("damage_value_fmt"), value);

    public static string BlockValue(string value) =>
        string.Format(Get("block_value_fmt"), value);

    public static string DeathCount(int count) =>
        string.Format(Get("death_count_fmt"), count);

    public static string PowerPrefix(string title) =>
        string.Format(Get("power_prefix_fmt"), title);

    public static string EffectPrefix(string value) =>
        string.Format(Get("effect_prefix_fmt"), value);

    public static string RelicPrefix(string title) =>
        string.Format(Get("relic_prefix_fmt"), title);

    public static string PoisonTarget(string target) =>
        string.Format(Get("poison_target_fmt"), target);

    // ===== 모드 정보 (게임 모드 화면) =====
    public static string ModName => Get("mod_name");
    public static string ModDescription => Get("mod_description");

    // ===== 업데이트 알림 =====
    public static string UpdateAvailable(string version) =>
        string.Format(Get("update_available_fmt"), version);
    public static string UpdateOpenPage => Get("update_open_page");
    public static string UpdateDismiss => Get("update_dismiss");

    public static string StatThisTurn(string value) =>
        string.Format(Get("stat_this_turn_fmt"), value);

    public static string StatPerTurn(string value) =>
        string.Format(Get("stat_per_turn_fmt"), value);

    public static string StatMax(string value) =>
        string.Format(Get("stat_max_fmt"), value);

    public static string StatPoison(string value) =>
        string.Format(Get("stat_poison_fmt"), value);

    public static string ReceivedDetail(string received, string blocked, string actual) =>
        string.Format(Get("received_detail_fmt"), received, blocked, actual);

    public static string BlockedSuffix(int blocked) =>
        string.Format(Get("blocked_suffix_fmt"), blocked);

    // 누적 모드
    public static string ToggleCombat => Get("toggle_combat");
    public static string ToggleRun => Get("toggle_run");
    public static string ResetRun => Get("reset_run");

    public static string FooterRun(int combats, string total) =>
        string.Format(Get("footer_run_fmt"), combats, total);

    public static string StatPerCombat(string value) =>
        string.Format(Get("stat_per_combat_fmt"), value);

    // ===== 언어별 딕셔너리 =====

    private static Dictionary<string, string> GetStrings(string locale) => locale switch
    {
        "ko" => KoreanStrings(),
        _ => EnglishStrings(),
    };

    private static Dictionary<string, string> KoreanStrings() => new()
    {
        ["tab_meter"] = "미터",
        ["tab_card_log"] = "카드로그",
        ["tab_received"] = "받은피해",

        ["title"] = "데미지 미터",
        ["title_waiting"] = "데미지 미터 | 대기중",
        ["title_done"] = "데미지 미터 (완료)",
        ["title_turn_fmt"] = "데미지 미터 | T{0}",

        ["header_player"] = "플레이어",
        ["header_damage"] = "데미지",
        ["filter_all"] = "전체",
        ["filter_turn"] = "턴:",
        ["filter_player"] = "플레이어:",

        ["footer_fmt"] = "턴: {0}  |  총합: {1}",

        ["empty_waiting"] = "전투 대기중...",
        ["empty_filter_no_match"] = "필터 조건에 맞는 로그가 없습니다.",
        ["empty_card_log"] = "카드 로그가 없습니다.",
        ["empty_received"] = "받은 피해 기록이 없습니다.",

        ["stat_this_turn_fmt"] = "이번턴: {0}",
        ["stat_per_turn_fmt"] = "턴당: {0}",
        ["stat_max_fmt"] = "최대: {0}",
        ["stat_poison_fmt"] = "독: {0}",

        ["kill"] = " 처치!",
        ["death_bang"] = " 사망!",
        ["death_count_fmt"] = "사망:{0}",

        ["received_header"] = "── 플레이어별 받은 피해 ──",
        ["received_detail_fmt"] = "  받은피해: {0}  |  막은피해: {1}  |  실제: {2}",
        ["blocked_suffix_fmt"] = "(막힘:{0})",

        ["count_items_fmt"] = "{0}건",
        ["damage_value_fmt"] = "데미지:{0}",
        ["block_value_fmt"] = "블록:{0}",

        ["power_prefix_fmt"] = "[파워] {0}",
        ["relic_prefix_fmt"] = "[유물] {0}",
        ["effect_prefix_fmt"] = "[효과] {0}",
        ["poison_target_fmt"] = "독 → {0}",

        ["resize_tooltip"] = "드래그하여 크기 조절",

        ["unknown"] = "알 수 없음",
        ["poison"] = "독",
        ["death"] = "사망",
        ["effect_label"] = "[효과]",

        ["toggle_combat"] = "이번 전투",
        ["toggle_run"] = "누적",
        ["footer_run_fmt"] = "전투 {0}회  |  총합: {1}",
        ["reset_run"] = "리셋",
        ["stat_per_combat_fmt"] = "전투당: {0}",

        ["mod_name"] = "데미지 미터",
        ["mod_description"] = "전투 중 플레이어별 딜량, 받은피해, 방어도, 카드 사용을 추적합니다. 미터/카드로그/받은피해 3개 탭. 솔로 및 협동 지원. F7로 토글.",

        ["update_available_fmt"] = "새 버전 {0} 출시!",
        ["update_open_page"] = "다운로드",
        ["update_dismiss"] = "닫기",
    };

    private static Dictionary<string, string> EnglishStrings() => new()
    {
        ["tab_meter"] = "Meter",
        ["tab_card_log"] = "Card Log",
        ["tab_received"] = "Received",

        ["title"] = "Damage Meter",
        ["title_waiting"] = "Damage Meter | Standby",
        ["title_done"] = "Damage Meter (Done)",
        ["title_turn_fmt"] = "Damage Meter | T{0}",

        ["header_player"] = "Player",
        ["header_damage"] = "Damage",
        ["filter_all"] = "All",
        ["filter_turn"] = "Turn:",
        ["filter_player"] = "Player:",

        ["footer_fmt"] = "Turn: {0}  |  Total: {1}",

        ["empty_waiting"] = "Waiting for combat...",
        ["empty_filter_no_match"] = "No logs match filter criteria.",
        ["empty_card_log"] = "No card logs yet.",
        ["empty_received"] = "No damage received yet.",

        ["stat_this_turn_fmt"] = "This turn: {0}",
        ["stat_per_turn_fmt"] = "Per turn: {0}",
        ["stat_max_fmt"] = "Max: {0}",
        ["stat_poison_fmt"] = "Poison: {0}",

        ["kill"] = " Kill!",
        ["death_bang"] = " Death!",
        ["death_count_fmt"] = "Deaths:{0}",

        ["received_header"] = "── Damage Received by Player ──",
        ["received_detail_fmt"] = "  Received: {0}  |  Blocked: {1}  |  Actual: {2}",
        ["blocked_suffix_fmt"] = "(Blocked:{0})",

        ["count_items_fmt"] = "{0} entries",
        ["damage_value_fmt"] = "Dmg:{0}",
        ["block_value_fmt"] = "Block:{0}",

        ["power_prefix_fmt"] = "[Power] {0}",
        ["relic_prefix_fmt"] = "[Relic] {0}",
        ["effect_prefix_fmt"] = "[Effect] {0}",
        ["poison_target_fmt"] = "Poison → {0}",

        ["resize_tooltip"] = "Drag to resize",

        ["unknown"] = "Unknown",
        ["poison"] = "Poison",
        ["death"] = "Death",
        ["effect_label"] = "[Effect]",

        ["toggle_combat"] = "This Combat",
        ["toggle_run"] = "Cumulative",
        ["footer_run_fmt"] = "{0} Combats  |  Total: {1}",
        ["reset_run"] = "Reset",
        ["stat_per_combat_fmt"] = "Per combat: {0}",

        ["mod_name"] = "Damage Meter",
        ["mod_description"] = "Tracks per-player damage dealt, received, block, and card usage during combat. 3 tabs: Meter, Card Log, Received Damage. Solo & co-op. F7 to toggle.",

        ["update_available_fmt"] = "Update {0} available!",
        ["update_open_page"] = "Download",
        ["update_dismiss"] = "Dismiss",
    };
}
