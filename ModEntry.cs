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
    public const string MOD_VERSION = "1.3.0";

    private static Harmony? _harmony;
    private static DamageMeterOverlay? _overlay;
    private static bool _debugMode = false;

    /// <summary>게임이 모드 로드 시 호출하는 static 진입점.</summary>
    public static void Initialize()
    {
        Log($"[DamageMeter] Initializing Damage Meter Mod v{MOD_VERSION}...");

        try
        {
            // Harmony 패치 적용
            _harmony = new Harmony(HARMONY_ID);
            _harmony.PatchAll(typeof(ModEntry).Assembly);
            Log("[DamageMeter] All Harmony patches applied.");

            // UI 오버레이를 씬 트리 루트에 추가 (deferred로 안전하게)
            var sceneTree = (SceneTree)Engine.GetMainLoop();
            _overlay = new DamageMeterOverlay();
            sceneTree.Root.CallDeferred("add_child", _overlay);
            Log("[DamageMeter] UI overlay added to scene tree.");

            Log("[DamageMeter] Mod initialized successfully. Press F7 to toggle.");

            // 비동기 업데이트 확인 (fire-and-forget, 초기화 차단 안 함)
            _ = Core.UpdateChecker.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            LogError($"[DamageMeter] Failed to initialize: {ex}");
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
