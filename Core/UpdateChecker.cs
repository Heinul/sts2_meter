using System.Text.Json;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
using HttpRequestException = System.Net.Http.HttpRequestException;

namespace DamageMeterMod.Core;

/// <summary>
/// GitHub Releases API를 통한 업데이트 확인.
/// 게임 시작 시 비동기로 최신 버전을 확인하고, 업데이트가 있으면 이벤트를 발행.
/// </summary>
public static class UpdateChecker
{
    private const string GITHUB_API_URL =
        "https://api.github.com/repos/Heinul/sts2_meter/releases/latest";
    private const int TIMEOUT_SECONDS = 5;

    /// <summary>확인된 최신 버전 (예: "1.3.0"). null이면 아직 확인 안 됨.</summary>
    public static string? LatestVersion { get; private set; }

    /// <summary>최신 릴리즈 페이지 URL.</summary>
    public static string? ReleaseUrl { get; private set; }

    /// <summary>업데이트가 있으면 true.</summary>
    public static bool IsUpdateAvailable { get; private set; }

    /// <summary>확인 중이면 true.</summary>
    public static bool IsChecking { get; private set; }

    /// <summary>업데이트 확인 완료 시 발행 (UI 스레드에서 호출하지 않을 수 있으므로 주의).</summary>
    public static event Action? OnUpdateCheckCompleted;

    /// <summary>
    /// GitHub Releases API로 최신 버전을 확인.
    /// fire-and-forget으로 호출해도 안전 (모든 예외를 내부에서 처리).
    /// </summary>
    public static async Task CheckForUpdateAsync()
    {
        if (IsChecking) return;
        IsChecking = true;

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(TIMEOUT_SECONDS);
            client.DefaultRequestHeaders.Add("User-Agent", $"DamageMeterMod/{ModEntry.MOD_VERSION}");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            var response = await client.GetAsync(GITHUB_API_URL);
            if (!response.IsSuccessStatusCode)
            {
                ModEntry.LogDebug($"[DamageMeter] Update check: HTTP {(int)response.StatusCode}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // tag_name: "v1.3.0" → "1.3.0"
            var tagName = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                ModEntry.LogDebug("[DamageMeter] Update check: tag_name is empty");
                return;
            }

            var versionStr = tagName.TrimStart('v', 'V');
            LatestVersion = versionStr;

            // html_url: 릴리즈 페이지 URL
            if (root.TryGetProperty("html_url", out var htmlUrlElem))
                ReleaseUrl = htmlUrlElem.GetString();

            // 버전 비교
            if (Version.TryParse(versionStr, out var latestVer) &&
                Version.TryParse(ModEntry.MOD_VERSION, out var currentVer))
            {
                IsUpdateAvailable = latestVer > currentVer;
                ModEntry.Log($"[DamageMeter] Update check: current={ModEntry.MOD_VERSION}, latest={versionStr}, update={IsUpdateAvailable}");
            }
            else
            {
                ModEntry.LogDebug($"[DamageMeter] Update check: version parse failed (current={ModEntry.MOD_VERSION}, latest={versionStr})");
            }
        }
        catch (TaskCanceledException)
        {
            ModEntry.LogDebug("[DamageMeter] Update check: timeout");
        }
        catch (HttpRequestException ex)
        {
            ModEntry.LogDebug($"[DamageMeter] Update check: network error - {ex.Message}");
        }
        catch (Exception ex)
        {
            ModEntry.LogDebug($"[DamageMeter] Update check: error - {ex.Message}");
        }
        finally
        {
            IsChecking = false;

            try
            {
                OnUpdateCheckCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] OnUpdateCheckCompleted error: {ex.Message}");
            }
        }
    }

    /// <summary>NexusMods 모드 페이지 URL.</summary>
    public const string NEXUSMODS_URL = "https://www.nexusmods.com/slaythespire2/mods/19";

    /// <summary>GitHub 릴리즈 폴백 URL.</summary>
    public const string GITHUB_RELEASES_URL = "https://github.com/Heinul/sts2_meter/releases/latest";

    /// <summary>브라우저에서 NexusMods 페이지를 연다.</summary>
    public static void OpenNexusMods()
    {
        ModEntry.Log($"[DamageMeter] Opening NexusMods: {NEXUSMODS_URL}");
        OS.ShellOpen(NEXUSMODS_URL);
    }

    /// <summary>브라우저에서 GitHub 릴리즈 페이지를 연다.</summary>
    public static void OpenGitHub()
    {
        var url = ReleaseUrl ?? GITHUB_RELEASES_URL;
        ModEntry.Log($"[DamageMeter] Opening GitHub: {url}");
        OS.ShellOpen(url);
    }
}
