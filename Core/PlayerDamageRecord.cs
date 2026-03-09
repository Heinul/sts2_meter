namespace DamageMeterMod.Core;

/// <summary>
/// 개별 플레이어의 데미지 통계를 저장하는 레코드.
/// struct로 선언하여 GC 부담 최소화.
/// </summary>
public struct PlayerDamageRecord
{
    public string PlayerId { get; }
    public string DisplayName { get; }
    // 준 데미지 통계
    public int TotalDamage { get; private set; }
    public int DirectDamage { get; private set; }
    public int PoisonDamage { get; private set; }
    public int HitCount { get; private set; }
    public int MaxSingleHit { get; private set; }
    public int CurrentTurnDamage { get; private set; }

    // 받은 데미지 통계
    public int TotalDamageReceived { get; private set; }
    public int TotalBlockedReceived { get; private set; }
    public int DeathCount { get; private set; }

    public PlayerDamageRecord(string playerId, string displayName)
    {
        PlayerId = playerId;
        DisplayName = displayName;
        TotalDamage = 0;
        DirectDamage = 0;
        PoisonDamage = 0;
        HitCount = 0;
        MaxSingleHit = 0;
        CurrentTurnDamage = 0;
        TotalDamageReceived = 0;
        TotalBlockedReceived = 0;
        DeathCount = 0;
    }

    /// <summary>직접 데미지 추가 (카드 등).</summary>
    public void AddDamage(int amount)
    {
        if (amount <= 0) return;

        TotalDamage += amount;
        DirectDamage += amount;
        HitCount++;
        CurrentTurnDamage += amount;

        if (amount > MaxSingleHit)
            MaxSingleHit = amount;
    }

    /// <summary>독/DoT 데미지 추가 (비율 귀속).</summary>
    public void AddPoisonDamage(int amount)
    {
        if (amount <= 0) return;
        TotalDamage += amount;
        PoisonDamage += amount;
        CurrentTurnDamage += amount;
    }

    /// <summary>받은 데미지 추가.</summary>
    public void AddDamageReceived(int total, int blocked)
    {
        TotalDamageReceived += total;
        TotalBlockedReceived += blocked;
    }

    /// <summary>사망 기록.</summary>
    public void RecordDeath() => DeathCount++;

    public void ResetTurnDamage()
    {
        CurrentTurnDamage = 0;
    }

    public void Reset()
    {
        TotalDamage = 0;
        DirectDamage = 0;
        PoisonDamage = 0;
        HitCount = 0;
        MaxSingleHit = 0;
        CurrentTurnDamage = 0;
        TotalDamageReceived = 0;
        TotalBlockedReceived = 0;
        DeathCount = 0;
    }
}
