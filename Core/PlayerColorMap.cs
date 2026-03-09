using Godot;

namespace DamageMeterMod.Core;

/// <summary>
/// PlayerId → Color 고정 매핑.
/// 랭킹이 바뀌어도 플레이어의 색상은 유지됨.
/// </summary>
public sealed class PlayerColorMap
{
    private static readonly Color[] Palette = new[]
    {
        new Color(0.85f, 0.25f, 0.25f),  // 빨강
        new Color(0.25f, 0.65f, 0.85f),  // 파랑
        new Color(0.25f, 0.8f, 0.35f),   // 초록
        new Color(0.85f, 0.7f, 0.2f),    // 노랑
        new Color(0.7f, 0.35f, 0.85f),   // 보라
        new Color(0.85f, 0.5f, 0.2f),    // 주황
    };

    private readonly Dictionary<string, int> _playerColorIndex = new();
    private int _nextIndex;

    /// <summary>플레이어 ID에 할당된 색상을 반환. 첫 요청 시 자동 할당.</summary>
    public Color GetColor(string playerId)
    {
        if (!_playerColorIndex.TryGetValue(playerId, out int idx))
        {
            idx = _nextIndex % Palette.Length;
            _playerColorIndex[playerId] = idx;
            _nextIndex++;
        }
        return Palette[idx];
    }

    /// <summary>설정 저장용 내보내기.</summary>
    public Dictionary<string, int> Export() => new(_playerColorIndex);

    /// <summary>설정에서 복원.</summary>
    public void Import(Dictionary<string, int> map)
    {
        _playerColorIndex.Clear();
        foreach (var kv in map)
            _playerColorIndex[kv.Key] = kv.Value;

        _nextIndex = map.Count > 0 ? map.Values.Max() + 1 : 0;
    }

    /// <summary>전투 시작 시 초기화 (색상 매핑은 유지, 새 전투에서도 같은 색상).</summary>
    public void Reset()
    {
        // 의도적으로 비우지 않음 - 같은 플레이어는 항상 같은 색상 유지
    }
}
