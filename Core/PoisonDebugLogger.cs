#if DEBUG
using Godot;

namespace DamageMeterMod.Core;

/// <summary>
/// 독 데미지 추적 전용 디버그 로거.
/// user://poison_debug.log 에 상세 로그를 기록.
/// DEBUG 빌드에서만 컴파일됨 (Release에서는 완전히 제거).
/// </summary>
public static class PoisonDebugLogger
{
    private static readonly object _writeLock = new();
    private static string? _logPath;
    private static int _eventCounter;
    private static bool _initialized;

    // 화면 표시용 링버퍼 (최근 N줄)
    private const int DISPLAY_BUFFER_SIZE = 40;
    private static readonly List<string> _displayBuffer = new(DISPLAY_BUFFER_SIZE + 10);
    private static int _poisonMatchCount;
    private static int _poisonTickCount;
    private static int _poisonApplyCount;
    private static int _hookCallCount;

    /// <summary>화면 표시용 통계.</summary>
    public static int HookCallCount => _hookCallCount;
    public static int PoisonMatchCount => _poisonMatchCount;
    public static int PoisonApplyCount => _poisonApplyCount;
    public static int PoisonTickCount => _poisonTickCount;
    public static int EventCount => _eventCounter;
    public static bool IsActive => _initialized;

    /// <summary>화면 표시용 최근 로그 라인들 복사본.</summary>
    public static List<string> GetDisplayLines()
    {
        lock (_writeLock)
        {
            return new List<string>(_displayBuffer);
        }
    }

    /// <summary>화면에 표시할 짧은 이벤트 추가.</summary>
    private static void AddDisplayLine(string line)
    {
        lock (_writeLock)
        {
            _displayBuffer.Add(line);
            if (_displayBuffer.Count > DISPLAY_BUFFER_SIZE)
                _displayBuffer.RemoveAt(0);
        }
    }

    /// <summary>훅 호출 카운터 증가.</summary>
    public static void IncrementHookCall() => System.Threading.Interlocked.Increment(ref _hookCallCount);

    /// <summary>독 매칭 카운터 증가.</summary>
    public static void IncrementPoisonMatch() => System.Threading.Interlocked.Increment(ref _poisonMatchCount);

    /// <summary>독 적용 카운터 증가.</summary>
    public static void IncrementPoisonApply() => System.Threading.Interlocked.Increment(ref _poisonApplyCount);

    /// <summary>독 틱 카운터 증가.</summary>
    public static void IncrementPoisonTick() => System.Threading.Interlocked.Increment(ref _poisonTickCount);

    /// <summary>로그 파일 경로 초기화. 전투 시작 시 호출.</summary>
    public static void Initialize()
    {
        try
        {
            // user:// → 실제 경로 변환
            var userDir = ProjectSettings.GlobalizePath("user://");
            _logPath = System.IO.Path.Combine(userDir, "poison_debug.log");
            _eventCounter = 0;
            _hookCallCount = 0;
            _poisonMatchCount = 0;
            _poisonApplyCount = 0;
            _poisonTickCount = 0;
            lock (_writeLock) { _displayBuffer.Clear(); }
            _initialized = true;

            // 기존 로그를 백업하고 새 세션 시작
            lock (_writeLock)
            {
                if (System.IO.File.Exists(_logPath))
                {
                    var backupPath = _logPath + ".prev";
                    try { System.IO.File.Copy(_logPath, backupPath, overwrite: true); } catch { }
                }

                using var sw = new System.IO.StreamWriter(_logPath, append: false);
                sw.WriteLine("========================================");
                sw.WriteLine($"  Poison Debug Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"  DamageMeterMod DEBUG build");
                sw.WriteLine("========================================");
                sw.WriteLine();
            }

            ModEntry.Log($"[PoisonDebug] 로그 파일: {_logPath}");
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[PoisonDebug] 초기화 실패: {ex.Message}");
            _initialized = false;
        }
    }

    /// <summary>로그 한 줄 기록. 타임스탬프 + 이벤트 번호 자동 부여.</summary>
    public static void Log(string message)
    {
        if (!_initialized || _logPath == null) return;

        try
        {
            var seq = System.Threading.Interlocked.Increment(ref _eventCounter);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] #{seq:D4} {message}";

            lock (_writeLock)
            {
                System.IO.File.AppendAllText(_logPath, line + "\n");
            }
        }
        catch { /* 로깅 실패는 무시 */ }
    }

    /// <summary>구분선 + 섹션 헤더.</summary>
    public static void Section(string title)
    {
        Log($"--- {title} ---");
    }

    /// <summary>AfterPowerAmountChanged 훅 호출 시 전체 파라미터 덤프.</summary>
    public static void LogPowerAmountChanged(
        object? power, decimal amount, object? applier,
        string? powerName, string? ownerName, bool isMonster)
    {
        Section("AfterPowerAmountChanged 호출");
        Log($"  PowerModel: {power?.GetType().FullName ?? "null"}");
        Log($"  PowerName (Title): '{powerName ?? "null"}'");
        Log($"  Amount (decimal): {amount}");
        Log($"  Amount (int cast): {(int)amount}");
        Log($"  Applier: {applier?.GetType().FullName ?? "null"}");
        if (applier != null)
        {
            try
            {
                var creature = applier as MegaCrit.Sts2.Core.Entities.Creatures.Creature;
                if (creature != null)
                {
                    Log($"  Applier.Name: {creature.Name}");
                    Log($"  Applier.IsPlayer: {creature.IsPlayer}");
                    Log($"  Applier.IsMonster: {creature.IsMonster}");
                    if (creature.IsPlayer && creature.Player != null)
                        Log($"  Applier.Player.NetId: {creature.Player.NetId}");
                }
            }
            catch (Exception ex) { Log($"  Applier 분석 실패: {ex.Message}"); }
        }
        Log($"  Owner.Name: '{ownerName ?? "null"}'");
        Log($"  Owner.IsMonster: {isMonster}");
    }

    /// <summary>파워 이름 매칭 결과 로깅.</summary>
    public static void LogPowerNameFilter(string powerName, bool matched, string reason)
    {
        Log($"  이름 필터: '{powerName}' → {(matched ? "매칭" : "스킵")} ({reason})");
        if (matched)
            AddDisplayLine($"  >> POISON 매칭: '{powerName}'");
    }

    /// <summary>독 수치 변화(diff) 계산 과정 로깅.</summary>
    public static void LogDiffCalculation(string monsterKey, int oldAmount, int currentAmount, int diff)
    {
        // monsterKey에서 해시 제거해서 짧게 표시
        var shortName = monsterKey.Contains('_') ? monsterKey[..monsterKey.LastIndexOf('_')] : monsterKey;
        Log($"  몬스터: {monsterKey}");
        Log($"  수치 변화: {oldAmount} → {currentAmount} (diff={diff})");
        if (diff > 0)
        {
            Log($"  판정: 독 증가 +{diff}");
            AddDisplayLine($"  [+독] {shortName} +{diff} ({currentAmount}스택)");
        }
        else if (diff == -1)
        {
            Log($"  판정: 독 틱 (데미지={oldAmount})");
            AddDisplayLine($"  [틱] {shortName} {oldAmount}dmg ({currentAmount}스택)");
        }
        else if (diff < -1)
        {
            Log($"  판정: 독 제거/해독 ({-diff} 감소)");
            AddDisplayLine($"  [해독] {shortName} {oldAmount}->{currentAmount}");
        }
        else
            Log($"  판정: 변화 없음 (diff=0)");
    }

    /// <summary>독 적용(RecordPoisonApplied) 로깅.</summary>
    public static void LogPoisonApplied(string monsterKey, string playerId, int stacks,
        Dictionary<string, Dictionary<string, int>>? attributionSnapshot)
    {
        Section("RecordPoisonApplied");
        Log($"  몬스터: {monsterKey}, 플레이어: {playerId}, 추가 스택: {stacks}");
        if (stacks > 0)
        {
            var shortName = monsterKey.Contains('_') ? monsterKey[..monsterKey.LastIndexOf('_')] : monsterKey;
            AddDisplayLine($"  [귀속] {playerId} -> {shortName} +{stacks}");
        }

        if (attributionSnapshot != null)
        {
            Log($"  현재 귀속 테이블:");
            foreach (var (mk, players) in attributionSnapshot)
            {
                foreach (var (pid, s) in players)
                    Log($"    [{mk}] {pid} = {s}스택");
            }
        }
    }

    /// <summary>독 데미지 틱(RecordPoisonDamageTick) 로깅.</summary>
    public static void LogPoisonDamageTick(string monsterKey, string monsterName,
        int totalDamage, Dictionary<string, int>? playerStacks,
        Dictionary<string, int>? damageShares)
    {
        Section("RecordPoisonDamageTick");
        Log($"  몬스터: {monsterKey} ({monsterName})");
        Log($"  총 독 데미지: {totalDamage}");

        if (playerStacks == null || playerStacks.Count == 0)
        {
            Log($"  !! 귀속 데이터 없음 (playerStacks = {(playerStacks == null ? "null" : "empty")})");
            AddDisplayLine($"  !! {monsterName} 독틱 {totalDamage}dmg - 귀속 없음!");
            return;
        }

        int totalStacks = playerStacks.Values.Sum();
        Log($"  총 귀속 스택: {totalStacks}");
        foreach (var (pid, stacks) in playerStacks)
        {
            float ratio = totalStacks > 0 ? (float)stacks / totalStacks : 0;
            Log($"    {pid}: {stacks}스택 ({ratio:P1})");
        }

        if (damageShares != null)
        {
            Log($"  데미지 분배 결과:");
            var parts = new List<string>();
            foreach (var (pid, share) in damageShares)
            {
                Log($"    {pid}: {share} dmg");
                parts.Add($"{pid}={share}");
            }
            AddDisplayLine($"  [분배] {monsterName} {totalDamage}dmg: {string.Join(", ", parts)}");
        }
    }

    /// <summary>_records 상태에서 독 데미지 확인.</summary>
    public static void LogRecordsState(Dictionary<string, PlayerDamageRecord> records)
    {
        Section("현재 _records 독 데미지 상태");
        foreach (var (pid, rec) in records)
        {
            Log($"  {pid} ({rec.DisplayName}): Total={rec.TotalDamage}, Direct={rec.DirectDamage}, Poison={rec.PoisonDamage}");
        }
    }

    /// <summary>전투 시작/종료 이벤트.</summary>
    public static void LogCombatEvent(string eventName, int playerCount = 0)
    {
        Log("");
        Section($"전투 이벤트: {eventName}");
        if (playerCount > 0)
            Log($"  플레이어 수: {playerCount}");
    }

    /// <summary>모든 AfterPowerAmountChanged 호출을 로깅 (Poison이 아닌 것도 포함).</summary>
    public static void LogAllPowerChanges(object? power, decimal amount)
    {
        if (power == null) return;
        try
        {
            var powerModel = power as MegaCrit.Sts2.Core.Models.PowerModel;
            if (powerModel == null) return;

            string? title = null;
            try { title = powerModel.Title?.GetFormattedText(); } catch { }
            try { title ??= powerModel.Title?.GetRawText(); } catch { }
            title ??= powerModel.GetType().Name;

            var ownerName = powerModel.Owner?.Name ?? "null";
            Log($"  [모든 파워] {title} on {ownerName}: amount={amount} (type={power.GetType().Name})");
            AddDisplayLine($"[Power] {title} on {ownerName} = {amount}");
        }
        catch { }
    }

    /// <summary>로그 파일 경로 반환.</summary>
    public static string? GetLogPath() => _logPath;
}
#endif
