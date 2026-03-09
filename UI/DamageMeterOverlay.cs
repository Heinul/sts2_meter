using Godot;
using DamageMeterMod.Core;

namespace DamageMeterMod.UI;

/// <summary>
/// 데미지 리더보드 오버레이 UI.
///
/// CanvasLayer를 사용하여 게임 UI 위에 독립적으로 렌더링.
/// 드래그 가능한 패널로 위치 조절 가능.
/// DamageTracker의 OnDataChanged 이벤트를 구독하여 실시간 업데이트.
///
/// 레이아웃:
///   ┌─────────────────────────────────┐
///   │  ⚔ Damage Meter        [−][×]  │  ← 헤더 (드래그 영역)
///   ├─────────────────────────────────┤
///   │  Player Name  │ Damage │   %   │  ← 컬럼 헤더
///   ├─────────────────────────────────┤
///   │  ████████████ Player1  1,234 52%│  ← 바 + 데이터
///   │  ████████     Player2    890 37%│
///   │  ████         Player3    256 11%│
///   ├─────────────────────────────────┤
///   │  Turn: 5  │  Total: 2,380       │  ← 푸터
///   └─────────────────────────────────┘
/// </summary>
public partial class DamageMeterOverlay : CanvasLayer
{
    // 색상 팔레트 (STS 테마에 맞춤)
    private static readonly Color BgColor = new(0.1f, 0.1f, 0.15f, 0.85f);
    private static readonly Color HeaderColor = new(0.15f, 0.12f, 0.2f, 0.95f);
    private static readonly Color BorderColor = new(0.6f, 0.5f, 0.2f, 0.8f);
    private static readonly Color TextColor = new(0.95f, 0.92f, 0.85f);
    private static readonly Color SubTextColor = new(0.7f, 0.65f, 0.55f);
    private static readonly Color[] BarColors = new[]
    {
        new Color(0.85f, 0.25f, 0.25f),  // 빨강 (1등)
        new Color(0.25f, 0.65f, 0.85f),  // 파랑 (2등)
        new Color(0.25f, 0.8f, 0.35f),   // 초록 (3등)
        new Color(0.85f, 0.7f, 0.2f),    // 노랑 (4등)
    };

    // 레이아웃 상수
    private const float PANEL_WIDTH = 320f;
    private const float HEADER_HEIGHT = 32f;
    private const float ROW_HEIGHT = 28f;
    private const float FOOTER_HEIGHT = 24f;
    private const float PADDING = 8f;
    private const float BAR_HEIGHT = 18f;

    // UI 노드
    private PanelContainer _panel = null!;
    private VBoxContainer _rootContainer = null!;
    private VBoxContainer _rowsContainer = null!;
    private Label _footerLabel = null!;
    private Label _titleLabel = null!;

    // 드래그 상태
    private bool _isDragging;
    private Vector2 _dragOffset;

    // 업데이트 제어 (성능)
    private bool _needsUpdate;
    private double _updateTimer;
    private const double UPDATE_INTERVAL = 0.1; // 100ms마다 UI 갱신

    // 축소 상태
    private bool _isMinimized;
    private bool _isVisible = true;

    public override void _Ready()
    {
        // CanvasLayer 설정 (게임 UI 위에 표시)
        Layer = 100;

        BuildUI();
        SubscribeToEvents();
    }

    /// <summary>UI 트리 구축 (순수 코드, .tscn 불필요).</summary>
    private void BuildUI()
    {
        _panel = new PanelContainer();
        _panel.Position = new Vector2(20, 200); // 기본 위치: 좌측 상단
        _panel.CustomMinimumSize = new Vector2(PANEL_WIDTH, 0);

        // 배경 스타일
        var panelStyle = new StyleBoxFlat
        {
            BgColor = BgColor,
            BorderColor = BorderColor,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = PADDING,
            ContentMarginRight = PADDING,
            ContentMarginTop = 0,
            ContentMarginBottom = PADDING
        };
        _panel.AddThemeStyleboxOverride("panel", panelStyle);

        // 루트 컨테이너
        _rootContainer = new VBoxContainer();
        _rootContainer.AddThemeConstantOverride("separation", 2);
        _panel.AddChild(_rootContainer);

        // 헤더 영역
        BuildHeader();

        // 컬럼 헤더
        BuildColumnHeader();

        // 데이터 행 컨테이너
        _rowsContainer = new VBoxContainer();
        _rowsContainer.AddThemeConstantOverride("separation", 2);
        _rootContainer.AddChild(_rowsContainer);

        // 구분선
        var separator = new HSeparator();
        _rootContainer.AddChild(separator);

        // 푸터
        _footerLabel = CreateLabel("Turn: 0  |  Total: 0", 11, SubTextColor);
        _footerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _rootContainer.AddChild(_footerLabel);

        AddChild(_panel);
    }

    private void BuildHeader()
    {
        var headerContainer = new HBoxContainer();
        headerContainer.CustomMinimumSize = new Vector2(0, HEADER_HEIGHT);
        headerContainer.AddThemeConstantOverride("separation", 4);

        _titleLabel = CreateLabel("Damage Meter", 13, TextColor);
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerContainer.AddChild(_titleLabel);

        // 최소화 버튼
        var minBtn = new Button();
        minBtn.Text = "−";
        minBtn.CustomMinimumSize = new Vector2(24, 24);
        minBtn.Pressed += OnMinimizePressed;
        ApplyButtonStyle(minBtn);
        headerContainer.AddChild(minBtn);

        // 닫기 버튼
        var closeBtn = new Button();
        closeBtn.Text = "×";
        closeBtn.CustomMinimumSize = new Vector2(24, 24);
        closeBtn.Pressed += OnClosePressed;
        ApplyButtonStyle(closeBtn);
        headerContainer.AddChild(closeBtn);

        _rootContainer.AddChild(headerContainer);
    }

    private void BuildColumnHeader()
    {
        var colHeader = new HBoxContainer();
        colHeader.CustomMinimumSize = new Vector2(0, 20);

        var nameCol = CreateLabel("Player", 10, SubTextColor);
        nameCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        colHeader.AddChild(nameCol);

        var dmgCol = CreateLabel("Damage", 10, SubTextColor);
        dmgCol.CustomMinimumSize = new Vector2(70, 0);
        dmgCol.HorizontalAlignment = HorizontalAlignment.Right;
        colHeader.AddChild(dmgCol);

        var pctCol = CreateLabel("%", 10, SubTextColor);
        pctCol.CustomMinimumSize = new Vector2(45, 0);
        pctCol.HorizontalAlignment = HorizontalAlignment.Right;
        colHeader.AddChild(pctCol);

        _rootContainer.AddChild(colHeader);
    }

    private void SubscribeToEvents()
    {
        DamageTracker.Instance.OnDataChanged += () => _needsUpdate = true;
    }

    /// <summary>프레임마다 호출. 타이머 기반으로 UI 갱신 제어.</summary>
    public override void _Process(double delta)
    {
        if (!_isVisible || _isMinimized) return;

        _updateTimer += delta;
        if (_needsUpdate && _updateTimer >= UPDATE_INTERVAL)
        {
            _updateTimer = 0;
            _needsUpdate = false;
            RefreshUI();
        }
    }

    /// <summary>키보드 + 드래그 입력 처리.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        // F7: 패널 표시/숨기기 토글
        // F8: 디버그 모드 토글
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.F7)
            {
                ToggleVisibility();
            }
            else if (keyEvent.Keycode == Key.F8)
            {
                ModEntry.SetDebugMode(!ModEntry.DebugMode);
            }
        }

        // 드래그 처리
        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed && IsInHeaderArea(mouseButton.Position))
                {
                    _isDragging = true;
                    _dragOffset = mouseButton.Position - _panel.Position;
                }
                else
                {
                    _isDragging = false;
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotion && _isDragging)
        {
            _panel.Position = mouseMotion.Position - _dragOffset;
        }
    }

    private bool IsInHeaderArea(Vector2 mousePos)
    {
        var rect = new Rect2(_panel.Position, new Vector2(PANEL_WIDTH, HEADER_HEIGHT));
        return rect.HasPoint(mousePos);
    }

    /// <summary>스냅샷 데이터를 기반으로 UI 행을 갱신.</summary>
    private void RefreshUI()
    {
        var snapshot = DamageTracker.Instance.GetSnapshot();

        // 기존 행 제거
        foreach (var child in _rowsContainer.GetChildren())
        {
            child.QueueFree();
        }

        if (snapshot.Count == 0)
        {
            var emptyLabel = CreateLabel("Waiting for combat...", 11, SubTextColor);
            emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _rowsContainer.AddChild(emptyLabel);
            _footerLabel.Text = "Turn: 0  |  Total: 0";
            return;
        }

        // 플레이어별 행 생성
        int grandTotal = 0;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            grandTotal += entry.TotalDamage;
            CreatePlayerRow(entry, i);
        }

        // 푸터 업데이트
        int turn = DamageTracker.Instance.CombatTurn;
        _footerLabel.Text = $"Turn: {turn}  |  Total: {grandTotal:N0}";

        // 전투 종료 상태 표시
        if (!DamageTracker.Instance.IsActive && grandTotal > 0)
        {
            _titleLabel.Text = "Damage Meter (Complete)";
        }
        else
        {
            _titleLabel.Text = "Damage Meter";
        }
    }

    /// <summary>플레이어 한 명의 데미지 행을 생성.</summary>
    private void CreatePlayerRow(PlayerDamageSnapshot entry, int index)
    {
        var rowContainer = new VBoxContainer();
        rowContainer.CustomMinimumSize = new Vector2(0, ROW_HEIGHT);
        rowContainer.AddThemeConstantOverride("separation", 1);

        // 상단: 이름 + 데미지 + 비율
        var dataRow = new HBoxContainer();
        dataRow.AddThemeConstantOverride("separation", 4);

        // 색상 인디케이터 + 이름
        var nameLabel = CreateLabel(entry.DisplayName, 12, TextColor);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dataRow.AddChild(nameLabel);

        // 데미지 값
        var dmgLabel = CreateLabel(entry.TotalDamage.ToString("N0"), 12, TextColor);
        dmgLabel.CustomMinimumSize = new Vector2(70, 0);
        dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dataRow.AddChild(dmgLabel);

        // 비율
        var pctLabel = CreateLabel($"{entry.Percentage:F1}%", 12,
            index == 0 ? BarColors[0] : SubTextColor);
        pctLabel.CustomMinimumSize = new Vector2(45, 0);
        pctLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dataRow.AddChild(pctLabel);

        rowContainer.AddChild(dataRow);

        // 하단: 비율 바
        var barBg = new ColorRect();
        barBg.CustomMinimumSize = new Vector2(0, 4);
        barBg.Color = new Color(0.2f, 0.2f, 0.25f);
        barBg.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rowContainer.AddChild(barBg);

        // 비율 바 (오버레이)
        var bar = new ColorRect();
        var barColor = BarColors[index % BarColors.Length];
        bar.Color = barColor;
        float barWidth = (PANEL_WIDTH - PADDING * 2) * (entry.Percentage / 100f);
        bar.CustomMinimumSize = new Vector2(Mathf.Max(barWidth, 2), 4);
        bar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        rowContainer.AddChild(bar);

        // 턴 데미지 & DPS (툴팁 대용, 작은 텍스트)
        if (entry.CurrentTurnDamage > 0 || entry.DamagePerTurn > 0)
        {
            var detailLabel = CreateLabel(
                $"  This turn: {entry.CurrentTurnDamage:N0}  |  DPT: {entry.DamagePerTurn:F1}  |  Max: {entry.MaxSingleHit:N0}",
                9, SubTextColor);
            rowContainer.AddChild(detailLabel);
        }

        _rowsContainer.AddChild(rowContainer);
    }

    // ---------------------------------------------------------------
    // 버튼 이벤트
    // ---------------------------------------------------------------

    private void OnMinimizePressed()
    {
        _isMinimized = !_isMinimized;
        _rowsContainer.Visible = !_isMinimized;
        _footerLabel.Visible = !_isMinimized;
        _titleLabel.Text = _isMinimized ? "Damage Meter [+]" : "Damage Meter";
    }

    private void OnClosePressed()
    {
        ToggleVisibility();
    }

    public void ToggleVisibility()
    {
        _isVisible = !_isVisible;
        _panel.Visible = _isVisible;
    }

    // ---------------------------------------------------------------
    // 유틸리티
    // ---------------------------------------------------------------

    private static Label CreateLabel(string text, int fontSize, Color color)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static void ApplyButtonStyle(Button button)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.2f, 0.18f, 0.25f, 0.9f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };
        button.AddThemeStyleboxOverride("normal", style);

        var hoverStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.3f, 0.28f, 0.35f, 0.9f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };
        button.AddThemeStyleboxOverride("hover", hoverStyle);
        button.AddThemeFontSizeOverride("font_size", 14);
    }
}
