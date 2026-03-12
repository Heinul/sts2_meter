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

    // CardModel 참조 캐시: cardName → CardModel object (hover tip용)
    private readonly Dictionary<string, object> _cardModelCache = new();

    // 런 전체 누적 기록
    private readonly Dictionary<string, PlayerDamageRecord> _runRecords = new();
    private int _runCombatCount;
    private object? _currentRunStateRef;

    // 플레이어 색상 매핑
    public PlayerColorMap ColorMap { get; } = new();

    // 전투 상태
    public bool IsActive { get; private set; }
    public int CombatTurn { get; private set; }
    /// <summary>로컬(내) 플레이어 ID. 최소화 표시 등에 사용.</summary>
    public string LocalPlayerId { get; set; } = string.Empty;

    /// <summary>CardModel 참조를 캐시에 저장. 같은 카드명이면 최신으로 갱신.</summary>
    public void CacheCardModel(string cardName, object cardModel)
    {
        if (string.IsNullOrEmpty(cardName) || cardModel == null) return;
        lock (_dataLock)
        {
            _cardModelCache[cardName] = cardModel;
        }
    }

    /// <summary>캐시된 CardModel 참조를 가져옴. 없으면 null.</summary>
    public object? GetCachedCardModel(string cardName)
    {
        if (string.IsNullOrEmpty(cardName)) return null;
        lock (_dataLock)
        {
            return _cardModelCache.GetValueOrDefault(cardName);
        }
    }

    /// <summary>UI 갱신이 필요할 때 발생하는 콜백.</summary>
    public event Action? OnDataChanged;

    /// <summary>전투 로그가 추가되었을 때 발생하는 콜백.</summary>
    public event Action? OnCombatLogChanged;

    /// <summary>전투 시작 시 호출. 이전 전투 데이터를 누적에 합산 후 초기화.</summary>
    public void StartCombat(IEnumerable<(string id, string name)> players)
    {
#if DEBUG
        PoisonDebugLogger.Initialize();
        PoisonDebugLogger.LogCombatEvent("StartCombat");
#endif

        lock (_dataLock)
        {
            // 이전 전투 데이터를 누적에 합산
            if (_records.Count > 0)
            {
                foreach (var kvp in _records)
                {
                    if (_runRecords.TryGetValue(kvp.Key, out var existing))
                    {
                        existing.MergeFrom(kvp.Value);
                        _runRecords[kvp.Key] = existing; // struct 재할당
                    }
                    else
                    {
                        var newRecord = new PlayerDamageRecord(kvp.Key, kvp.Value.DisplayName);
                        newRecord.MergeFrom(kvp.Value);
                        _runRecords[kvp.Key] = newRecord;
                    }
                }
            }

            // 기존 동작: 현재 전투 데이터 초기화
            _records.Clear();
            _combatLog.Clear();
            _poisonAttribution.Clear();
            _cardModelCache.Clear();
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
        lock (_dataLock)
        {
            _runCombatCount++; // 완료된 전투만 카운트
        }
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
                CardName = cardName ?? L10N.Unknown,
                TargetName = targetName ?? L10N.Unknown,
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
                SourceName = sourceName ?? L10N.Unknown,
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
                CardName = cardName ?? L10N.Unknown,
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
                CardName = cardName ?? L10N.Unknown,
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

#if DEBUG
            // 귀속 테이블 스냅샷을 파일에 기록
            var snapshot = new Dictionary<string, Dictionary<string, int>>();
            foreach (var kvp in _poisonAttribution)
                snapshot[kvp.Key] = new Dictionary<string, int>(kvp.Value);
            PoisonDebugLogger.LogPoisonApplied(monsterKey, playerId, stacksAdded, snapshot);
#endif
        }
    }

    /// <summary>독 데미지 틱 시 비율 귀속하여 기록.</summary>
    public void RecordPoisonDamageTick(string monsterKey, string monsterName, int totalPoisonDamage)
    {
        if (!IsActive || totalPoisonDamage <= 0) return;

        lock (_dataLock)
        {
            if (!_poisonAttribution.TryGetValue(monsterKey, out var playerStacks) || playerStacks.Count == 0)
            {
#if DEBUG
                PoisonDebugLogger.LogPoisonDamageTick(monsterKey, monsterName, totalPoisonDamage, null, null);
#endif
                return;
            }

            int totalStacks = playerStacks.Values.Sum();
            if (totalStacks <= 0)
            {
#if DEBUG
                PoisonDebugLogger.Log($"  !! totalStacks=0 for {monsterKey} → 스킵");
#endif
                return;
            }

            // 비율 귀속: 각 플레이어의 기여 스택 비율로 데미지 분배
            var sortedPlayers = playerStacks.OrderByDescending(kv => kv.Value).ToList();
#if DEBUG
            var damageShares = new Dictionary<string, int>();
#endif

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

#if DEBUG
                damageShares[playerId] = share;
#endif

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
                    SourceName = L10N.Poison,
                    Damage = share,
                    UnblockedDamage = share,
                    BlockedDamage = 0,
                    WasKill = false,
                    TimestampTicks = DateTime.UtcNow.Ticks
                });
            }

#if DEBUG
            PoisonDebugLogger.LogPoisonDamageTick(monsterKey, monsterName, totalPoisonDamage,
                new Dictionary<string, int>(playerStacks), damageShares);
            PoisonDebugLogger.LogRecordsState(new Dictionary<string, PlayerDamageRecord>(_records));
#endif
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

    /// <summary>IRunState 참조 비교로 새 런 감지. 새 런이면 누적 초기화.</summary>
    public void CheckRunChange(object? runState)
    {
        lock (_dataLock)
        {
            bool isNewRun = false;
            string reason = "";

            if (runState == null)
            {
                if (_runRecords.Count > 0 || _currentRunStateRef != null)
                {
                    isNewRun = true;
                    reason = "runState=null, 잔여 데이터 있음";
                }
            }
            else if (_currentRunStateRef == null)
            {
                if (_runRecords.Count > 0)
                {
                    isNewRun = true;
                    reason = "이전 참조 없음 + 잔여 데이터";
                }
                else
                {
                    reason = "첫 런 (데이터 없음, 리셋 불필요)";
                }
            }
            else if (!ReferenceEquals(_currentRunStateRef, runState))
            {
                isNewRun = true;
                reason = $"참조 변경 (old={_currentRunStateRef.GetHashCode()}, new={runState.GetHashCode()})";
            }
            else
            {
                reason = $"같은 참조 (hash={runState.GetHashCode()}) → 같은 런";
            }

            ModEntry.Log($"[DamageMeter] CheckRunChange: isNewRun={isNewRun}, runRecords={_runRecords.Count}, reason={reason}");

            if (isNewRun)
            {
                _runRecords.Clear();
                _records.Clear(); // 이전 런 잔여 데이터가 StartCombat에서 재합산되는 것 방지
                _runCombatCount = 0;
            }

            _currentRunStateRef = runState;
        }
    }

    /// <summary>수동 리셋 버튼용. 누적 데이터 초기화.</summary>
    public void ResetRunData()
    {
        lock (_dataLock)
        {
            _runRecords.Clear();
            _runCombatCount = 0;
        }
        OnDataChanged?.Invoke();
    }

    /// <summary>누적 + 현재 전투 합산 스냅샷 반환.</summary>
    public IReadOnlyList<PlayerDamageSnapshot> GetRunSnapshot()
    {
        lock (_dataLock)
        {
            // _runRecords + _records 합산
            var merged = new Dictionary<string, PlayerDamageRecord>();

            foreach (var kvp in _runRecords)
            {
                merged[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in _records)
            {
                if (merged.TryGetValue(kvp.Key, out var existing))
                {
                    existing.MergeFrom(kvp.Value);
                    merged[kvp.Key] = existing;
                }
                else
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            int grandTotal = merged.Values.Sum(r => r.TotalDamage);
            // 완료된 전투 + 진행 중 전투
            int totalCombats = _runCombatCount + (IsActive ? 1 : 0);

            return merged.Values
                .OrderByDescending(r => r.TotalDamage)
                .Select(r => new PlayerDamageSnapshot
                {
                    PlayerId = r.PlayerId,
                    DisplayName = r.DisplayName,
                    TotalDamage = r.TotalDamage,
                    DirectDamage = r.DirectDamage,
                    PoisonDamage = r.PoisonDamage,
                    Percentage = grandTotal > 0
                        ? (float)r.TotalDamage / grandTotal * 100f : 0f,
                    HitCount = r.HitCount,
                    MaxSingleHit = r.MaxSingleHit,
                    CurrentTurnDamage = 0, // 누적 모드에서는 의미 없음
                    DamagePerTurn = totalCombats > 0
                        ? (float)r.TotalDamage / totalCombats : 0f,
                        // 누적 모드: "턴당" 대신 "전투당" 데미지
                    TotalDamageReceived = r.TotalDamageReceived,
                    TotalBlockedReceived = r.TotalBlockedReceived,
                    DeathCount = r.DeathCount
                })
                .ToList();
        }
    }

    /// <summary>현재 런의 전투 횟수. 진행 중 전투 포함.</summary>
    public int RunCombatCount
    {
        get { lock (_dataLock) { return _runCombatCount + (IsActive ? 1 : 0); } }
    }

#if DEBUG
    /// <summary>디버그: 전체 독 추적 상태를 파일에 덤프. F8 키로 호출.</summary>
    public string DumpPoisonDebugInfo()
    {
        lock (_dataLock)
        {
            PoisonDebugLogger.Section("=== F8 수동 덤프 ===");

            // 플레이어별 독 데미지
            PoisonDebugLogger.LogRecordsState(new Dictionary<string, PlayerDamageRecord>(_records));

            // 독 귀속 테이블
            PoisonDebugLogger.Section("Poison Attribution 전체");
            if (_poisonAttribution.Count == 0)
            {
                PoisonDebugLogger.Log("  (비어 있음 — 독 적용이 기록되지 않았음)");
            }
            else
            {
                var snapshot = new Dictionary<string, Dictionary<string, int>>();
                foreach (var kvp in _poisonAttribution)
                    snapshot[kvp.Key] = new Dictionary<string, int>(kvp.Value);
                PoisonDebugLogger.LogPoisonApplied("(전체 덤프)", "(전체)", 0, snapshot);
            }

            PoisonDebugLogger.Log($"  IsActive={IsActive}, Turn={CombatTurn}");
            PoisonDebugLogger.Section("=== 덤프 완료 ===");

            return $"독 디버그 로그가 파일에 기록됨: {PoisonDebugLogger.GetLogPath()}";
        }
    }
#endif

    /// <summary>전체 초기화 (모드 언로드 시).</summary>
    public void Dispose()
    {
        lock (_dataLock)
        {
            _records.Clear();
            _combatLog.Clear();
            _poisonAttribution.Clear();
            _runRecords.Clear();
            _runCombatCount = 0;
            _currentRunStateRef = null;
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
