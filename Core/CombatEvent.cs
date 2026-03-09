namespace DamageMeterMod.Core;

/// <summary>전투 이벤트 종류.</summary>
public enum CombatEventType
{
    DamageDealt,      // 플레이어가 데미지를 줌
    DamageReceived,   // 플레이어가 데미지를 받음
    PoisonDamage,     // 독 데미지 (비율 귀속)
    Death             // 사망
}

/// <summary>
/// 전투 중 발생한 단일 이벤트.
/// 카드 로그, 받은 피해 로그, 전투 기록에서 공통으로 사용.
/// </summary>
public readonly struct CombatEvent
{
    /// <summary>이벤트 발생 턴 번호.</summary>
    public required int Turn { get; init; }

    /// <summary>이벤트 유형.</summary>
    public required CombatEventType EventType { get; init; }

    /// <summary>이벤트 관련 플레이어 ID.</summary>
    public required string PlayerId { get; init; }

    /// <summary>플레이어 표시 이름.</summary>
    public required string PlayerName { get; init; }

    /// <summary>사용한 카드 이름 (DamageDealt인 경우).</summary>
    public string CardName { get; init; }

    /// <summary>대상 이름 (적 또는 플레이어).</summary>
    public string TargetName { get; init; }

    /// <summary>공격 출처 이름 (DamageReceived인 경우 공격한 적 이름).</summary>
    public string SourceName { get; init; }

    /// <summary>총 데미지.</summary>
    public required int Damage { get; init; }

    /// <summary>방어된 데미지.</summary>
    public int BlockedDamage { get; init; }

    /// <summary>실제 HP에 들어간 데미지.</summary>
    public int UnblockedDamage { get; init; }

    /// <summary>대상이 죽었는지 여부.</summary>
    public bool WasKill { get; init; }

    /// <summary>이벤트 발생 시각 (UTC ticks).</summary>
    public long TimestampTicks { get; init; }
}
