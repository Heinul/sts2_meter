namespace DamageMeterMod.Core;

/// <summary>
/// 개별 플레이어의 데미지 통계를 저장하는 레코드.
/// struct로 선언하여 GC 부담 최소화.
/// </summary>
public struct PlayerDamageRecord
{
    public string PlayerId { get; }
    public string DisplayName { get; }
    public int TotalDamage { get; private set; }
    public int HitCount { get; private set; }
    public int MaxSingleHit { get; private set; }
    public int CurrentTurnDamage { get; private set; }

    public PlayerDamageRecord(string playerId, string displayName)
    {
        PlayerId = playerId;
        DisplayName = displayName;
        TotalDamage = 0;
        HitCount = 0;
        MaxSingleHit = 0;
        CurrentTurnDamage = 0;
    }

    public void AddDamage(int amount)
    {
        if (amount <= 0) return;

        TotalDamage += amount;
        HitCount++;
        CurrentTurnDamage += amount;

        if (amount > MaxSingleHit)
            MaxSingleHit = amount;
    }

    public void ResetTurnDamage()
    {
        CurrentTurnDamage = 0;
    }

    public void Reset()
    {
        TotalDamage = 0;
        HitCount = 0;
        MaxSingleHit = 0;
        CurrentTurnDamage = 0;
    }
}
