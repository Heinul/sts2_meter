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
    private readonly List<CombatEvent> _combatLog = new();
    private readonly object _dataLock = new();

    // 독 귀속 추적: monsterKey → { playerId → 기여 스택 수 }
    private readonly Dictionary<string, Dictionary<string, int>> _poisonAttribution = new();

    // 플레이어 색상 매핑
    public PlayerColorMap ColorMap { get; } = new();

    // 전투 상태
    public bool IsActive { get; private set; }
    public int CombatTurn { get; private set; }
    private DateTime _combatStartTime;
    private string _combatId = string.Empty;

    /// <summary>UI 갱신이 필요할 때 발생하는 콜백.</summary>
    public event Action? OnDataChanged;

    /// <summary>전투 로그가 추가되었을 때 발생하는 콜백.</summary>
    public event Action? OnCombatLogChanged;

    /// <summary>전투 시작 시 호출. 모든 데이터를 초기화.</summary>
    public void StartCombat(IEnumerable<(string id, string name)> players)
    {
        lock (_dataLock)
        {
            _records.Clear();
            _combatLog.Clear();
            _poisonAttribution.Clear();
            CombatTurn = 1;
            IsActive = true;
            _combatStartTime = DateTime.UtcNow;
            _combatId = Guid.NewGuid().ToString("N")[..8];

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

    /// <summary>전투 중 새 플레이어가 감지되면 자동 등록.</summary>
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

    /// <summary>직접 데미지 이벤트 기록 (카드 공격 등).</summary>
    public void RecordDamage(string sourcePlayerId, int damageAmount)
    {
        if (!IsActive || damageAmount <= 0) return;

        lock (_dataLock)
        {
            if (_records.TryGetValue(sourcePlayerId, out var record))
            {
                record.AddDamage(damageAmount);
                _records[sourcePlayerId] = record;
            }
        }

        OnDataChanged?.Invoke();
    }

    /// <summary>카드별 데미지를 전투 로그에 기록.</summary>
    public void RecordCardDamage(string playerId, string playerName,
        string cardName, string targetName,
        int totalDamage, int unblockedDamage, int blockedDamage, bool wasKill)
    {
        if (!IsActive) return;

        lock (_dataLock)
        {
            _combatLog.Add(new CombatEvent
            {
                Turn = CombatTurn,
                EventType = CombatEventType.DamageDealt,
                PlayerId = playerId,
                PlayerName = playerName,
                CardName = cardName ?? "알 수 없음",
                TargetName = targetName ?? "알 수 없음",
                SourceName = string.Empty,
                Damage = totalDamage,
                UnblockedDamage = unblockedDamage,
                BlockedDamage = blockedDamage,
                WasKill = wasKill,
                TimestampTicks = DateTime.UtcNow.Ticks
            });
        }

        OnCombatLogChanged?.Invoke();
    }

    /// <summary>플레이어가 받은 데미지를 기록.</summary>
    public void RecordDamageReceived(string targetPlayerId, string targetPlayerName,
        string sourceName, int totalDamage, int unblockedDamage, int blockedDamage,
        bool wasKilled)
    {
        if (!IsActive) return;

        lock (_dataLock)
        {
            if (_records.TryGetValue(targetPlayerId, out var record))
            {
                record.AddDamageReceived(totalDamage, blockedDamage);
                if (wasKilled) record.RecordDeath();
                _records[targetPlayerId] = record;
            }

            _combatLog.Add(new CombatEvent
            {
                Turn = CombatTurn,
                EventType = wasKilled ? CombatEventType.Death : CombatEventType.DamageReceived,
                PlayerId = targetPlayerId,
                PlayerName = targetPlayerName,
                CardName = string.Empty,
                TargetName = string.Empty,
                SourceName = sourceName ?? "알 수 없음",
                Damage = totalDamage,
                UnblockedDamage = unblockedDamage,
                BlockedDamage = blockedDamage,
                WasKill = wasKilled,
                TimestampTicks = DateTime.UtcNow.Ticks
            });
        }

        OnDataChanged?.Invoke();
        OnCombatLogChanged?.Invoke();
    }

    /// <summary>블록(쉴드) 획득 기록.</summary>
    public void RecordBlockGained(string playerId, string playerName,
        int blockAmount, string cardName)
    {
        if (!IsActive || blockAmount <= 0) return;

        lock (_dataLock)
        {
            _combatLog.Add(new CombatEvent
            {
                Turn = CombatTurn,
                EventType = CombatEventType.BlockGained,
                PlayerId = playerId,
                PlayerName = playerName,
                CardName = cardName ?? "알 수 없음",
                TargetName = string.Empty,
                SourceName = string.Empty,
                Damage = blockAmount,
                UnblockedDamage = 0,
                BlockedDamage = 0,
                WasKill = false,
                TimestampTicks = DateTime.UtcNow.Ticks
            });
        }

        OnCombatLogChanged?.Invoke();
    }

    /// <summary>카드 사용 기록 (데미지 없는 카드).</summary>
    public void RecordCardPlayed(string playerId, string playerName,
        string cardName, string cardType)
    {
        if (!IsActive) return;

        lock (_dataLock)
        {
            _combatLog.Add(new CombatEvent
            {
                Turn = CombatTurn,
                EventType = CombatEventType.CardPlayed,
                PlayerId = playerId,
                PlayerName = playerName,
                CardName = cardName ?? "알 수 없음",
                TargetName = cardType,
                SourceName = string.Empty,
                Damage = 0,
                UnblockedDamage = 0,
                BlockedDamage = 0,
                WasKill = false,
                TimestampTicks = DateTime.UtcNow.Ticks
            });
        }

        OnCombatLogChanged?.Invoke();
    }

    /// <summary>독 스택 기여를 기록 (독 부여 시).</summary>
    public void RecordPoisonApplied(string monsterKey, string playerId, int stacksAdded)
    {
        if (!IsActive || stacksAdded <= 0) return;

        lock (_dataLock)
        {
            if (!_poisonAttribution.TryGetValue(monsterKey, out var playerStacks))
            {
                playerStacks = new Dictionary<string, int>();
                _poisonAttribution[monsterKey] = playerStacks;
            }

            playerStacks.TryGetValue(playerId, out int existing);
            playerStacks[playerId] = existing + stacksAdded;
        }
    }

    /// <summary>독 데미지 틱 시 비율 귀속하여 기록.</summary>
    public void RecordPoisonDamageTick(string monsterKey, string monsterName, int totalPoisonDamage)
    {
        if (!IsActive || totalPoisonDamage <= 0) return;

        lock (_dataLock)
        {
            if (!_poisonAttribution.TryGetValue(monsterKey, out var playerStacks) || playerStacks.Count == 0)
                return;

            int totalStacks = playerStacks.Values.Sum();
            if (totalStacks <= 0) return;

            // 비율 귀속: 각 플레이어의 기여 스택 비율로 데미지 분배
            var sortedPlayers = playerStacks.OrderByDescending(kv => kv.Value).ToList();

            for (int i = 0; i < sortedPlayers.Count; i++)
            {
                var (playerId, stacks) = (sortedPlayers[i].Key, sortedPlayers[i].Value);
                int share;

                if (i == 0)
                {
                    // 가장 기여가 큰 플레이어가 반올림 나머지를 가져감
                    share = totalPoisonDamage - sortedPlayers.Skip(1)
                        .Sum(kv => (int)((float)kv.Value / totalStacks * totalPoisonDamage));
                }
                else
                {
                    share = (int)((float)stacks / totalStacks * totalPoisonDamage);
                }

                if (share <= 0) continue;

                if (_records.TryGetValue(playerId, out var record))
                {
                    record.AddPoisonDamage(share);
                    _records[playerId] = record;
                }

                string playerName = _records.TryGetValue(playerId, out var rec) ? rec.DisplayName : playerId;

                _combatLog.Add(new CombatEvent
                {
                    Turn = CombatTurn,
                    EventType = CombatEventType.PoisonDamage,
                    PlayerId = playerId,
                    PlayerName = playerName,
                    CardName = string.Empty,
                    TargetName = monsterName,
                    SourceName = "독",
                    Damage = share,
                    UnblockedDamage = share,
                    BlockedDamage = 0,
                    WasKill = false,
                    TimestampTicks = DateTime.UtcNow.Ticks
                });
            }
        }

        OnDataChanged?.Invoke();
        OnCombatLogChanged?.Invoke();
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

    /// <summary>UI 렌더링용 스냅샷 반환. 총 데미지 내림차순 정렬.</summary>
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
                    DirectDamage = r.DirectDamage,
                    PoisonDamage = r.PoisonDamage,
                    Percentage = grandTotal > 0
                        ? (float)r.TotalDamage / grandTotal * 100f
                        : 0f,
                    HitCount = r.HitCount,
                    MaxSingleHit = r.MaxSingleHit,
                    CurrentTurnDamage = r.CurrentTurnDamage,
                    DamagePerTurn = CombatTurn > 0
                        ? (float)r.TotalDamage / CombatTurn
                        : 0f,
                    TotalDamageReceived = r.TotalDamageReceived,
                    TotalBlockedReceived = r.TotalBlockedReceived,
                    DeathCount = r.DeathCount
                })
                .ToList();
        }
    }

    /// <summary>전투 로그 스냅샷 반환.</summary>
    public IReadOnlyList<CombatEvent> GetCombatLogSnapshot()
    {
        lock (_dataLock)
        {
            return _combatLog.ToList();
        }
    }

    /// <summary>특정 이벤트 타입만 필터링한 로그 반환.</summary>
    public IReadOnlyList<CombatEvent> GetCombatLogSnapshot(CombatEventType eventType)
    {
        lock (_dataLock)
        {
            return _combatLog.Where(e => e.EventType == eventType).ToList();
        }
    }

    /// <summary>전투 종료 시 CombatSummary를 생성하여 반환.</summary>
    public CombatSummary BuildCombatSummary()
    {
        lock (_dataLock)
        {
            int grandTotal = _records.Values.Sum(r => r.TotalDamage);

            var players = _records.Values.Select(r => new PlayerSummary
            {
                PlayerId = r.PlayerId,
                DisplayName = r.DisplayName,
                TotalDamage = r.TotalDamage,
                DirectDamage = r.DirectDamage,
                PoisonDamage = r.PoisonDamage,
                HitCount = r.HitCount,
                MaxSingleHit = r.MaxSingleHit,
                TotalDamageReceived = r.TotalDamageReceived,
                DeathCount = r.DeathCount,
                DamagePercentage = grandTotal > 0
                    ? (float)r.TotalDamage / grandTotal * 100f
                    : 0f
            }).ToList();

            return new CombatSummary
            {
                CombatId = _combatId,
                StartTime = _combatStartTime,
                EndTime = DateTime.UtcNow,
                TotalTurns = CombatTurn,
                TotalDamageDealt = grandTotal,
                TotalDamageReceived = _records.Values.Sum(r => r.TotalDamageReceived),
                Players = players,
                CombatLog = _combatLog.ToList()
            };
        }
    }

    /// <summary>전체 초기화 (모드 언로드 시).</summary>
    public void Dispose()
    {
        lock (_dataLock)
        {
            _records.Clear();
            _combatLog.Clear();
            _poisonAttribution.Clear();
            IsActive = false;
        }
        OnDataChanged = null;
        OnCombatLogChanged = null;
        _instance = null;
    }
}

/// <summary>UI 표시용 읽기 전용 스냅샷.</summary>
public readonly struct PlayerDamageSnapshot
{
    public required string PlayerId { get; init; }
    public required string DisplayName { get; init; }
    public required int TotalDamage { get; init; }
    public required int DirectDamage { get; init; }
    public required int PoisonDamage { get; init; }
    public required float Percentage { get; init; }
    public required int HitCount { get; init; }
    public required int MaxSingleHit { get; init; }
    public required int CurrentTurnDamage { get; init; }
    public required float DamagePerTurn { get; init; }
    public required int TotalDamageReceived { get; init; }
    public required int TotalBlockedReceived { get; init; }
    public required int DeathCount { get; init; }
}
