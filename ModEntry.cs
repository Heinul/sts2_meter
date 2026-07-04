using System.Linq;
using Godot;
using HarmonyLib;
using DamageMeterMod.Core;
using DamageMeterMod.UI;
using MegaCrit.Sts2.Core.Modding;

namespace DamageMeterMod;

/// <summary>
/// Damage Meter 모드 엔트리포인트.
///
/// STS2 모드 로딩 흐름:
///   1. mods/ 디렉토리에서 .pck 파일 감지
///   2. PCK 내부 res://mod_manifest.json 로드
///   3. entry_point DLL 로드
///   4. [ModInitializer] 어트리뷰트가 있는 클래스 탐색
///   5. MethodName에 지정된 static 메서드 호출
///   (없으면 자동으로 Harmony.PatchAll 호출)
/// </summary>
[ModInitializer("Initialize")]
public class ModEntry
{
    private const string HARMONY_ID = "com.damagemeter.sts2";
    public const string MOD_VERSION = "1.5.5";

    private static Harmony? _harmony;
    private static DamageMeterOverlay? _overlay;
    private static bool _debugMode = false;

    // 데미지 측정에 필수인 핵심 훅 (label, 대체이름들). 하나라도 없으면 호환성 문제.
    // 게임 빌드마다 훅이 개명될 수 있어 대체 이름 목록으로 확인.
    private static readonly (string label, string[] names)[] CoreHooks =
    {
        ("AfterDamageGiven",    new[] { "AfterDamageGiven" }),
        ("BeforeCombatStart",   new[] { "BeforeCombatStart" }),
        ("AfterTurnEnd",        new[] { "AfterSideTurnEnd", "AfterTurnEnd" }),
        ("AfterDamageReceived", new[] { "AfterDamageReceived" }),
    };

    /// <summary>발견되지 않은 핵심 훅 목록. 비어있으면 정상.</summary>
    public static List<string> MissingCoreHooks { get; } = new();

    /// <summary>핵심 훅이 모두 존재하면 true. false면 오버레이가 호환성 경고 표시.</summary>
    public static bool IsCompatible => MissingCoreHooks.Count == 0;

    /// <summary>게임이 모드 로드 시 호출하는 static 진입점.</summary>
    public static void Initialize()
    {
        Log($"[DamageMeter] Initializing Damage Meter Mod v{MOD_VERSION}...");

        // 핵심 훅 존재 여부 확인 — 하나라도 없으면 데미지 측정 불가.
        // 오버레이가 이 결과로 "호환성 경고"를 표시(빈 미터 대신 명확한 안내).
        CheckCompatibility();

        // Harmony 패치 적용 — 일부 패치가 실패해도 UI는 뜨도록 별도 try.
        // (게임 빌드마다 훅 시그니처/이름이 바뀔 수 있어, 패치 하나 실패가
        //  오버레이 전체 실종으로 번지지 않게 분리.)
        try
        {
            _harmony = new Harmony(HARMONY_ID);
            _harmony.PatchAll(typeof(ModEntry).Assembly);
            Log("[DamageMeter] Harmony patches applied.");
        }
        catch (Exception ex)
        {
            LogError($"[DamageMeter] PatchAll partial failure (UI still loads): {ex}");
        }

        // 패치 성공 여부와 무관하게 오버레이는 항상 생성.
        try
        {
            var sceneTree = (SceneTree)Engine.GetMainLoop();
            _overlay = new DamageMeterOverlay();
            sceneTree.Root.CallDeferred("add_child", _overlay);
            Log($"[DamageMeter] UI overlay added. Press {Persistence.ModSettings.FormatKey(Persistence.ModSettings.Current.GetToggleKey())} to toggle.");
        }
        catch (Exception ex)
        {
            LogError($"[DamageMeter] Failed to create overlay: {ex}");
        }
    }

    /// <summary>핵심 훅 존재 확인. 없는 것을 MissingCoreHooks에 기록.</summary>
    private static void CheckCompatibility()
    {
        MissingCoreHooks.Clear();
        try
        {
            var hookType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Hooks.Hook");
            if (hookType == null)
            {
                MissingCoreHooks.Add("Hook type");
                return;
            }

            foreach (var (label, names) in CoreHooks)
            {
                bool found = names.Any(n => AccessTools.Method(hookType, n) != null);
                if (!found) MissingCoreHooks.Add(label);
            }

            if (IsCompatible)
                Log("[DamageMeter] Compatibility OK — all core hooks found.");
            else
                LogWarning($"[DamageMeter] Incompatible with this game build. Missing hooks: {string.Join(", ", MissingCoreHooks)}");
        }
        catch (Exception ex)
        {
            LogError($"[DamageMeter] Compatibility check error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // 정적 로깅 유틸리티
    // ---------------------------------------------------------------

    public static void Log(string message)
    {
        if (_debugMode)
            GD.Print(message);
    }

    public static void LogDebug(string message)
    {
        if (_debugMode)
            GD.Print(message);
    }

    public static void LogWarning(string message)
    {
        GD.PushWarning(message);
    }

    public static void LogError(string message)
    {
        GD.PushError(message);
    }

    public static void SetDebugMode(bool enabled)
    {
        _debugMode = enabled;
        Log($"[DamageMeter] Debug mode: {(_debugMode ? "ON" : "OFF")}");
    }

    public static bool DebugMode => _debugMode;
}
