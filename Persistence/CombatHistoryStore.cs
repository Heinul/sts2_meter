using System.Text.Json;
using DamageMeterMod.Core;
using Godot;
using FileAccess = Godot.FileAccess;

namespace DamageMeterMod.Persistence;

/// <summary>
/// 전투 히스토리를 디스크에 저장/로드.
/// 각 전투는 개별 JSON 파일로 저장.
/// 경로: user://damage_meter_history/
/// </summary>
public sealed class CombatHistoryStore
{
    private const string HISTORY_DIR = "user://damage_meter_history";
    private const int MAX_HISTORY_FILES = 50;

    /// <summary>전투 종료 시 호출. CombatSummary를 JSON으로 저장.</summary>
    public void SaveCombat(CombatSummary summary)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(HISTORY_DIR);

            var filename = $"{HISTORY_DIR}/combat_{summary.StartTime:yyyyMMdd_HHmmss}_{summary.CombatId}.json";
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            using var file = FileAccess.Open(filename, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                ModEntry.LogError($"[DamageMeter] 전투 기록 파일 생성 실패: {filename}");
                return;
            }
            file.StoreString(json);
            ModEntry.LogDebug($"[DamageMeter] 전투 기록 저장: {filename}");

            CleanupOldFiles();
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 전투 기록 저장 실패: {ex.Message}");
        }
    }

    /// <summary>저장된 전투 목록을 로드 (최신 순).</summary>
    public List<CombatHistoryMeta> LoadHistoryList()
    {
        var result = new List<CombatHistoryMeta>();
        try
        {
            using var dir = DirAccess.Open(HISTORY_DIR);
            if (dir == null) return result;

            dir.ListDirBegin();
            string fileName;
            while ((fileName = dir.GetNext()) != "")
            {
                if (!fileName.EndsWith(".json")) continue;
                result.Add(new CombatHistoryMeta
                {
                    FileName = fileName,
                    FilePath = $"{HISTORY_DIR}/{fileName}"
                });
            }
            dir.ListDirEnd();
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 전투 기록 목록 로드 실패: {ex.Message}");
        }

        return result.OrderByDescending(m => m.FileName).ToList();
    }

    /// <summary>특정 전투 기록을 전체 로드.</summary>
    public CombatSummary? LoadCombat(string filePath)
    {
        try
        {
            using var file = FileAccess.Open(filePath, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var json = file.GetAsText();
            return JsonSerializer.Deserialize<CombatSummary>(json);
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 전투 기록 로드 실패: {ex.Message}");
            return null;
        }
    }

    private void CleanupOldFiles()
    {
        var files = LoadHistoryList();
        if (files.Count <= MAX_HISTORY_FILES) return;

        foreach (var old in files.Skip(MAX_HISTORY_FILES))
        {
            DirAccess.RemoveAbsolute(old.FilePath);
            ModEntry.LogDebug($"[DamageMeter] 오래된 전투 기록 삭제: {old.FileName}");
        }
    }
}

/// <summary>전투 기록 메타데이터 (목록 표시용).</summary>
public struct CombatHistoryMeta
{
    public string FileName { get; set; }
    public string FilePath { get; set; }
}
