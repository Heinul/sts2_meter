namespace DamageMeterMod.Core;

/// <summary>
/// 완료된 전투의 전체 요약. 디스크 저장/로드 단위.
/// </summary>
public sealed class CombatSummary
{
    public required string CombatId { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public required int TotalTurns { get; init; }
    public required int TotalDamageDealt { get; init; }
    public required int TotalDamageReceived { get; init; }
    public required List<PlayerSummary> Players { get; init; }
    public required List<CombatEvent> CombatLog { get; init; }
}

/// <summary>전투 요약 내 플레이어별 통계.</summary>
public sealed class PlayerSummary
{
    public required string PlayerId { get; init; }
    public required string DisplayName { get; init; }
    public required int TotalDamage { get; init; }
    public required int DirectDamage { get; init; }
    public required int PoisonDamage { get; init; }
    public required int HitCount { get; init; }
    public required int MaxSingleHit { get; init; }
    public required int TotalDamageReceived { get; init; }
    public required int DeathCount { get; init; }
    public required float DamagePercentage { get; init; }
}
