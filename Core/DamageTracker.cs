using System.Collections.Generic;
using System.Linq;

namespace DamageMeterMod.Core;

/// <summary>
/// 전투 중 모든 플레이어의 데미지를 추적하는 싱글턴 매니저.
/// Thread-safe하게 설계 (co-op 네트워크 콜백 대비).
/// </summary>
public sealed class DamageTracker
{
    private static DamageTracker? _instance;
    private static readonly object _lock = new();

    public static DamageTracker Instance
    {
        get
        {
            if (_instance is null)
            {
                lock (_lock)
                {
                    _instance ??= new DamageTracker();
                }
            }
            return _instance;
        }
    }

    private readonly Dictionary<string, PlayerDamageRecord> _records = new();
    private readonly object _dataLock = new();

    public bool IsActive { get; private set; }
    public int CombatTurn { get; private set; }

    /// <summary>
    /// UI 갱신이 필요할 때 발생하는 콜백.
    /// Godot Signal 대신 C# event를 사용하여 성능 최적화.
    /// </summary>
    public event Action? OnDataChanged;

    /// <summary>전투 시작 시 호출. 모든 데이터를 초기화.</summary>
    public void StartCombat(IEnumerable<(string id, string name)> players)
    {
        lock (_dataLock)
        {
            _records.Clear();
            CombatTurn = 1;
            IsActive = true;

            foreach (var (id, name) in players)
            {
                _records[id] = new PlayerDamageRecord(id, name);
            }
        }

        OnDataChanged?.Invoke();
    }

    /// <summary>전투 종료 시 호출. 마지막 데이터는 유지 (결과 화면용).</summary>
    public void EndCombat()
    {
        IsActive = false;
        OnDataChanged?.Invoke();
    }

    /// <summary>
    /// 전투 중 새 플레이어가 감지되면 자동 등록.
    /// AfterDamageGiven에서 BeforeCombatStart보다 먼저 호출되거나
    /// 플레이어가 나중에 참여하는 경우를 처리.
    /// </summary>
    public void EnsurePlayerRegistered(string playerId, string displayName)
    {
        if (!IsActive) return;

        lock (_dataLock)
        {
            if (!_records.ContainsKey(playerId))
            {
                _records[playerId] = new PlayerDamageRecord(playerId, displayName);
            }
        }
    }

    /// <summary>데미지 이벤트 발생 시 호출.</summary>
    public void RecordDamage(string sourcePlayerId, int damageAmount)
    {
        if (!IsActive || damageAmount <= 0) return;

        lock (_dataLock)
        {
            if (_records.TryGetValue(sourcePlayerId, out var record))
            {
                record.AddDamage(damageAmount);
                _records[sourcePlayerId] = record; // struct이므로 재할당 필요
            }
        }

        OnDataChanged?.Invoke();
    }

    /// <summary>턴 종료 시 호출.</summary>
    public void OnTurnEnd()
    {
        lock (_dataLock)
        {
            CombatTurn++;
            var keys = _records.Keys.ToList();
            foreach (var key in keys)
            {
                var record = _records[key];
                record.ResetTurnDamage();
                _records[key] = record;
            }
        }

        OnDataChanged?.Invoke();
    }

    /// <summary>
    /// UI 렌더링용 스냅샷 반환.
    /// 총 데미지 내림차순 정렬.
    /// </summary>
    public IReadOnlyList<PlayerDamageSnapshot> GetSnapshot()
    {
        lock (_dataLock)
        {
            int grandTotal = _records.Values.Sum(r => r.TotalDamage);

            return _records.Values
                .OrderByDescending(r => r.TotalDamage)
                .Select(r => new PlayerDamageSnapshot
                {
                    PlayerId = r.PlayerId,
                    DisplayName = r.DisplayName,
                    TotalDamage = r.TotalDamage,
                    Percentage = grandTotal > 0
                        ? (float)r.TotalDamage / grandTotal * 100f
                        : 0f,
                    HitCount = r.HitCount,
                    MaxSingleHit = r.MaxSingleHit,
                    CurrentTurnDamage = r.CurrentTurnDamage,
                    DamagePerTurn = CombatTurn > 0
                        ? (float)r.TotalDamage / CombatTurn
                        : 0f
                })
                .ToList();
        }
    }

    /// <summary>전체 초기화 (모드 언로드 시).</summary>
    public void Dispose()
    {
        lock (_dataLock)
        {
            _records.Clear();
            IsActive = false;
        }
        OnDataChanged = null;
        _instance = null;
    }
}

/// <summary>UI 표시용 읽기 전용 스냅샷.</summary>
public readonly struct PlayerDamageSnapshot
{
    public required string PlayerId { get; init; }
    public required string DisplayName { get; init; }
    public required int TotalDamage { get; init; }
    public required float Percentage { get; init; }
    public required int HitCount { get; init; }
    public required int MaxSingleHit { get; init; }
    public required int CurrentTurnDamage { get; init; }
    public required float DamagePerTurn { get; init; }
}
