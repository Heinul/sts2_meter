using System.Text.Json;
using Godot;
using FileAccess = Godot.FileAccess;

namespace DamageMeterMod.Persistence;

/// <summary>
/// 모드 설정 저장/로드. JSON 직렬화.
/// 저장 경로: user://damage_meter_settings.json
/// </summary>
public sealed class ModSettings
{
    private const string SETTINGS_FILE = "user://damage_meter_settings.json";
    public const Key DefaultToggleKey = Key.F7;
    public const Key DefaultResetKey = Key.F6;

    // 패널 위치
    public float PanelX { get; set; } = 20f;
    public float PanelY { get; set; } = 200f;

    // 패널 크기
    public float PanelWidth { get; set; }
    public float PanelHeight { get; set; }

    // UI 상태
    public bool IsVisible { get; set; } = true;
    public bool IsMinimized { get; set; }
    public int ActiveTab { get; set; }
    public bool ShowRunTotal { get; set; }
    public Key ToggleKey { get; set; } = DefaultToggleKey;
    public Key ResetKey { get; set; } = DefaultResetKey;

    // 플레이어 색상 매핑 (PlayerId → 색상 인덱스)
    public Dictionary<string, int> PlayerColors { get; set; } = new();

    // 싱글톤
    private static ModSettings? _current;
    public static ModSettings Current => _current ??= Load();

    public Key GetToggleKey()
    {
        return IsReservedKey(ToggleKey) ? DefaultToggleKey : ToggleKey;
    }

    public Key GetResetKey()
    {
        return IsReservedKey(ResetKey) ? DefaultResetKey : ResetKey;
    }

    /// <summary>항상 예약된 키 (바인딩 불가). None/Escape만.</summary>
    public static bool IsReservedKey(Key key)
    {
        return key == Key.None || key == Key.Escape;
    }

    /// <summary>토글 키로 유효한지. 예약 아니고 리셋 키와 겹치지 않아야 함.</summary>
    public bool IsValidToggleKey(Key key)
    {
        return !IsReservedKey(key) && key != GetResetKey();
    }

    /// <summary>리셋 키로 유효한지. 예약 아니고 토글 키와 겹치지 않아야 함.</summary>
    public bool IsValidResetKey(Key key)
    {
        return !IsReservedKey(key) && key != GetToggleKey();
    }

    public static string FormatKey(Key key)
    {
        return key == Key.None ? "None" : key.ToString();
    }

    public static ModSettings Load()
    {
        try
        {
            if (!FileAccess.FileExists(SETTINGS_FILE))
                return new ModSettings();

            using var file = FileAccess.Open(SETTINGS_FILE, FileAccess.ModeFlags.Read);
            if (file == null) return new ModSettings();

            var json = file.GetAsText();
            return JsonSerializer.Deserialize<ModSettings>(json) ?? new ModSettings();
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 설정 로드 실패: {ex.Message}");
            return new ModSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            using var file = FileAccess.Open(SETTINGS_FILE, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                ModEntry.LogError("[DamageMeter] 설정 파일을 열 수 없습니다.");
                return;
            }
            file.StoreString(json);
            ModEntry.LogDebug("[DamageMeter] 설정 저장 완료.");
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 설정 저장 실패: {ex.Message}");
        }
    }

    /// <summary>패널 위치를 저장 (드래그 종료 시 호출).</summary>
    public void SavePanelPosition(Vector2 position)
    {
        PanelX = position.X;
        PanelY = position.Y;
        Save();
    }
}
