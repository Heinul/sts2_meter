using System.Reflection;
using HarmonyLib;
using DamageMeterMod.Core;

namespace DamageMeterMod.Patches;

/// <summary>
/// 게임 모드 정보 화면(NModInfoContainer)에서 우리 모드의 이름/설명을
/// 현재 게임 언어에 맞게 표시하는 Harmony 패치.
///
/// 대상: NModInfoContainer.Fill(Mod)
///   _title (MegaRichTextLabel) — 모드 이름
///   _description (MegaRichTextLabel) — 모드 설명
///
/// 패치 후 id == "DamageMeterMod"인 경우에만 L10N 값으로 교체.
/// </summary>
public static class ModInfoPatches
{
    // NModInfoContainer 필드 캐싱
    private static FieldInfo? _titleField;
    private static FieldInfo? _descField;
    private static FieldInfo? _manifestField;
    private static FieldInfo? _idField;
    private static FieldInfo? _authorField;
    private static FieldInfo? _versionField;
    private static bool _fieldsResolved;

    [HarmonyPatch]
    public static class FillPatch
    {
        [HarmonyTargetMethod]
        static MethodBase Target()
        {
            var type = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer");
            return AccessTools.Method(type, "Fill");
        }

        /// <summary>
        /// Fill(Mod) 실행 후 우리 모드이면 로컬라이즈된 텍스트로 교체.
        /// __instance = NModInfoContainer, __0 = Mod
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(object __instance, object __0)
        {
            try
            {
                // 필드 한 번만 리졸브
                if (!_fieldsResolved)
                {
                    ResolveFields(__instance.GetType(), __0.GetType());
                    _fieldsResolved = true;
                }

                // Mod.manifest 접근
                var manifest = _manifestField?.GetValue(__0);
                if (manifest == null) return;

                // id 확인 — 우리 모드인지
                var modId = _idField?.GetValue(manifest) as string;
                if (modId != "DamageMeterMod") return;

                // 타이틀 교체
                var titleNode = _titleField?.GetValue(__instance);
                if (titleNode != null)
                    SetNodeText(titleNode, L10N.ModName);

                // 설명 교체 — 게임 포맷(Author/Version + 설명)을 그대로 재현하되
                // 설명 본문만 로컬라이즈. 게임 Fill()이 _description 하나에 통합하므로
                // 단순 교체 시 Author/Version이 사라짐.
                var descNode = _descField?.GetValue(__instance);
                if (descNode != null)
                {
                    string author = _authorField?.GetValue(manifest) as string ?? "unknown";
                    string version = _versionField?.GetValue(manifest) as string ?? "unknown";
                    string composed =
                        $"[gold]Author[/gold]: {author}\n" +
                        $"[gold]Version[/gold]: {version}\n\n" +
                        L10N.ModDescription;
                    SetNodeText(descNode, composed);
                }

                ModEntry.LogDebug($"[DamageMeter] ModInfo localized: locale={L10N.Locale}");
            }
            catch (Exception ex)
            {
                ModEntry.LogError($"[DamageMeter] ModInfo patch error: {ex.Message}");
            }
        }
    }

    /// <summary>리플렉션 필드 캐싱.</summary>
    private static void ResolveFields(Type containerType, Type modType)
    {
        _titleField = AccessTools.Field(containerType, "_title");
        _descField = AccessTools.Field(containerType, "_description");
        _manifestField = AccessTools.Field(modType, "manifest");

        if (_manifestField != null)
        {
            var manifestType = _manifestField.FieldType;
            _idField = AccessTools.Field(manifestType, "id");
            _authorField = AccessTools.Field(manifestType, "author");
            _versionField = AccessTools.Field(manifestType, "version");
        }

        ModEntry.LogDebug($"[DamageMeter] ModInfo fields resolved: " +
            $"title={_titleField != null}, desc={_descField != null}, " +
            $"manifest={_manifestField != null}, id={_idField != null}, " +
            $"author={_authorField != null}, version={_versionField != null}");
    }

    /// <summary>
    /// MegaRichTextLabel / RichTextLabel / Label 등에 텍스트 설정.
    /// Godot 기본 타입 캐스트 → 실패 시 리플렉션 폴백.
    /// </summary>
    private static void SetNodeText(object node, string text)
    {
        // MegaRichTextLabel이 RichTextLabel을 상속하는 경우
        if (node is Godot.RichTextLabel rtl)
        {
            rtl.Text = text;
            return;
        }

        // Label 기반인 경우
        if (node is Godot.Label label)
        {
            label.Text = text;
            return;
        }

        // 폴백: 리플렉션으로 Text 프로퍼티 시도
        var textProp = node.GetType().GetProperty("Text",
            BindingFlags.Public | BindingFlags.Instance);
        if (textProp != null && textProp.CanWrite)
        {
            textProp.SetValue(node, text);
            return;
        }

        // 최후 폴백: SetText 메서드 시도
        var setTextMethod = node.GetType().GetMethod("SetText",
            BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(string) }, null);
        setTextMethod?.Invoke(node, new object[] { text });
    }
}
