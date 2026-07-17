using System.Reflection;
using Godot;
using HarmonyLib;
using DamageMeterMod.Core;
using DamageMeterMod.Persistence;

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
public static partial class ModInfoPatches
{
    private const string SettingsPanelName = "DamageMeterModSettingsPanel";
    private const string CaptureLayerName = "DamageMeterModKeyCaptureLayer";

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
        private const string ContainerTypeName =
            "MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer";

        // 타입/메서드가 없으면 이 패치만 스킵 (PatchAll 중단 방지).
        [HarmonyPrepare]
        public static bool Prepare()
        {
            bool ok = AccessTools.Method(AccessTools.TypeByName(ContainerTypeName), "Fill") != null;
            if (!ok)
                ModEntry.LogWarning("[DamageMeter] NModInfoContainer.Fill not found — mod info localization disabled");
            return ok;
        }

        [HarmonyTargetMethod]
        static MethodBase Target()
        {
            return AccessTools.Method(AccessTools.TypeByName(ContainerTypeName), "Fill");
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

                var root = __instance as Control;
                RemoveSettingsUi(root);

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

                AddSettingsUi(root, descNode);

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

    private static void RemoveSettingsUi(Control? root)
    {
        var existing = root?.FindChild(SettingsPanelName, true, false);
        if (existing == null) return;

        existing.GetParent()?.RemoveChild(existing);
        existing.QueueFree();
    }

    private static void AddSettingsUi(Control? root, object? descNode)
    {
        if (root == null) return;

        var panel = new VBoxContainer
        {
            Name = SettingsPanelName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 110)
        };
        panel.AddThemeConstantOverride("separation", 6);

        var title = new Label
        {
            Text = L10N.ModSettingsTitle,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeFontSizeOverride("font_size", 14);
        panel.AddChild(title);

        // 토글 키 행 + 위치 초기화 키 행
        panel.AddChild(MakeKeyRow(root, L10N.ToggleKeyLabel, isReset: false));
        panel.AddChild(MakeKeyRow(root, L10N.ResetKeyLabel, isReset: true));

        if (descNode is Node desc && desc.GetParent() is Container parentContainer)
        {
            parentContainer.AddChild(panel);
            return;
        }

        root.AddChild(panel);
        panel.AnchorLeft = 0f;
        panel.AnchorRight = 1f;
        panel.AnchorTop = 1f;
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = 0f;
        panel.OffsetRight = 0f;
        panel.OffsetTop = -118f;
        panel.OffsetBottom = 0f;
    }

    /// <summary>키 바인딩 한 행 생성 (라벨 + 현재키 버튼 + 기본값 버튼).</summary>
    private static HBoxContainer MakeKeyRow(Control root, string labelText, bool isReset)
    {
        var settings = ModSettings.Current;
        Func<Key> getKey = isReset ? settings.GetResetKey : settings.GetToggleKey;

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 8);

        var label = new Label
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.AddChild(label);

        var keyButton = new Button
        {
            Text = L10N.ToggleKeyCurrent(ModSettings.FormatKey(getKey())),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        keyButton.Pressed += () => StartKeyCapture(root, keyButton, isReset);
        row.AddChild(keyButton);

        var resetButton = new Button
        {
            Text = L10N.ToggleKeyReset,
            CustomMinimumSize = new Vector2(84, 0)
        };
        resetButton.Pressed += () =>
        {
            if (isReset) settings.ResetKey = ModSettings.DefaultResetKey;
            else settings.ToggleKey = ModSettings.DefaultToggleKey;
            settings.Save();
            keyButton.Text = L10N.ToggleKeyCurrent(ModSettings.FormatKey(getKey()));
        };
        row.AddChild(resetButton);

        return row;
    }

    private static void StartKeyCapture(Control root, Button keyButton, bool isReset)
    {
        var sceneRoot = root.GetTree()?.Root;
        if (sceneRoot == null) return;

        var existing = sceneRoot.FindChild(CaptureLayerName, true, false);
        if (existing != null)
        {
            existing.GetParent()?.RemoveChild(existing);
            existing.QueueFree();
        }

        keyButton.Text = L10N.ToggleKeyCapture;
        var settings = ModSettings.Current;
        Func<Key> getKey = isReset ? settings.GetResetKey : settings.GetToggleKey;

        var captureLayer = new KeyCaptureLayer
        {
            Name = CaptureLayerName,
            Validate = key => isReset ? settings.IsValidResetKey(key) : settings.IsValidToggleKey(key),
            Captured = key =>
            {
                if (isReset) settings.ResetKey = key;
                else settings.ToggleKey = key;
                settings.Save();
                keyButton.Text = L10N.ToggleKeyCurrent(ModSettings.FormatKey(getKey()));
            },
            Cancelled = () =>
            {
                keyButton.Text = L10N.ToggleKeyCurrent(ModSettings.FormatKey(getKey()));
            }
        };

        sceneRoot.AddChild(captureLayer);
    }

    private sealed partial class KeyCaptureLayer : CanvasLayer
    {
        public Action<Key>? Captured { get; init; }
        public Action? Cancelled { get; init; }
        public Func<Key, bool>? Validate { get; init; }

        private Label? _messageLabel;

        public override void _Ready()
        {
            Layer = 1024;

            var dimmer = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.45f),
                AnchorRight = 1f,
                AnchorBottom = 1f
            };
            AddChild(dimmer);

            _messageLabel = new Label
            {
                Text = L10N.ToggleKeyCapture,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AnchorLeft = 0.25f,
                AnchorTop = 0.45f,
                AnchorRight = 0.75f,
                AnchorBottom = 0.55f
            };
            _messageLabel.AddThemeFontSizeOverride("font_size", 18);
            AddChild(_messageLabel);
        }

        public override void _Input(InputEvent @event)
        {
            if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
                return;

            GetViewport().SetInputAsHandled();

            var key = keyEvent.Keycode != Key.None ? keyEvent.Keycode : keyEvent.PhysicalKeycode;
            if (key == Key.Escape)
            {
                Cancelled?.Invoke();
                QueueFree();
                return;
            }

            if (Validate != null && !Validate(key))
            {
                if (_messageLabel != null)
                    _messageLabel.Text = L10N.ToggleKeyReserved;
                return;
            }

            Captured?.Invoke(key);
            QueueFree();
        }
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
