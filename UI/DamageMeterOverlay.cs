using Godot;
using DamageMeterMod.Core;
using DamageMeterMod.Persistence;

namespace DamageMeterMod.UI;

/// <summary>
/// 데미지 미터 오버레이 UI (탭 기반).
///
/// v2.0 변경사항:
///   - 카드 로그: 모든 카드 표시 (공격/방어/스킬/파워/독)
///   - 받은 피해: 막은피해/실제피해 구분 표시
///   - 최소화: 창 축소 + 1줄 요약 (총데미지/턴데미지/비율)
///   - 창 크기 조절: 우하단 드래그로 리사이즈
/// </summary>
public partial class DamageMeterOverlay : CanvasLayer
{
    // 색상 팔레트
    private static readonly Color BgColor = new(0.1f, 0.1f, 0.15f, 0.85f);
    private static readonly Color HeaderColor = new(0.15f, 0.12f, 0.2f, 0.95f);
    private static readonly Color BorderColor = new(0.6f, 0.5f, 0.2f, 0.8f);
    private static readonly Color TextColor = new(0.95f, 0.92f, 0.85f);
    private static readonly Color SubTextColor = new(0.7f, 0.65f, 0.55f);
    private static readonly Color TabActiveBg = new(0.2f, 0.18f, 0.28f, 0.95f);
    private static readonly Color TabInactiveBg = new(0.12f, 0.12f, 0.17f, 0.7f);
    private static readonly Color BlockColor = new(0.3f, 0.65f, 1.0f);
    private static readonly Color PoisonColor = new(0.3f, 0.9f, 0.3f);
    private static readonly Color CardPlayedColor = new(0.8f, 0.8f, 0.6f);

    // 레이아웃 상수
    private const float DEFAULT_PANEL_WIDTH = 360f;
    private const float HEADER_HEIGHT = 32f;
    private const float PADDING = 8f;
    private const float DEFAULT_CONTENT_HEIGHT = 280f;
    private const float MIN_PANEL_WIDTH = 280f;
    private const float MAX_PANEL_WIDTH = 600f;
    private const float MIN_CONTENT_HEIGHT = 100f;
    private const float MAX_CONTENT_HEIGHT = 600f;
    private const float RESIZE_HANDLE_SIZE = 14f;

    // UI 노드
    private PanelContainer _panel = null!;
    private VBoxContainer _rootContainer = null!;
    private Label _titleLabel = null!;
    private Label _footerLabel = null!;
    private HSeparator _footerSeparator = null!;

    // 탭 시스템
    private HBoxContainer _tabBar = null!;
    private Control[] _tabContents = null!;
    private Button[] _tabButtons = null!;
    private int _activeTab;
    private static readonly string[] TabNames = { "미터", "카드로그", "받은피해", "전투기록" };

    // 탭별 컨텐츠
    private VBoxContainer _meterRows = null!;
    private VBoxContainer _cardLogRows = null!;
    private VBoxContainer _receivedRows = null!;
    private VBoxContainer _historyRows = null!;
    private ScrollContainer[] _scrollContainers = new ScrollContainer[3];

    // 드래그 상태
    private bool _isDragging;
    private Vector2 _dragOffset;

    // 리사이즈 상태
    private bool _isResizing;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;
    private float _currentWidth = DEFAULT_PANEL_WIDTH;
    private float _currentHeight = DEFAULT_CONTENT_HEIGHT;

    // 업데이트 제어
    private bool _needsUpdate;
    private bool _needsLogUpdate;
    private double _updateTimer;
    private const double UPDATE_INTERVAL = 0.1;

    // UI 상태
    private bool _isMinimized;
    private bool _isVisible = true;
    private VBoxContainer _contentArea = null!;
    private Button _minBtn = null!;
    private HBoxContainer _resizeHandleRow = null!;

    public override void _Ready()
    {
        Layer = 100;
        BuildUI();
        SubscribeToEvents();
        LoadSettings();
    }

    private void BuildUI()
    {
        _panel = new PanelContainer();
        _panel.CustomMinimumSize = new Vector2(_currentWidth, 0);

        var panelStyle = new StyleBoxFlat
        {
            BgColor = BgColor,
            BorderColor = BorderColor,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = PADDING, ContentMarginRight = PADDING,
            ContentMarginTop = 0, ContentMarginBottom = PADDING
        };
        _panel.AddThemeStyleboxOverride("panel", panelStyle);

        _rootContainer = new VBoxContainer();
        _rootContainer.AddThemeConstantOverride("separation", 2);
        _panel.AddChild(_rootContainer);

        BuildHeader();
        BuildTabBar();
        BuildContentArea();
        BuildFooter();
        BuildResizeHandle();

        AddChild(_panel);
    }

    // ===== 헤더 =====
    private void BuildHeader()
    {
        var headerContainer = new HBoxContainer();
        headerContainer.CustomMinimumSize = new Vector2(0, HEADER_HEIGHT);
        headerContainer.AddThemeConstantOverride("separation", 4);

        _titleLabel = CreateLabel("데미지 미터", 13, TextColor);
        _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _titleLabel.ClipText = true;
        headerContainer.AddChild(_titleLabel);

        _minBtn = CreateHeaderButton("−");
        _minBtn.Pressed += OnMinimizePressed;
        headerContainer.AddChild(_minBtn);

        var closeBtn = CreateHeaderButton("×");
        closeBtn.Pressed += OnClosePressed;
        headerContainer.AddChild(closeBtn);

        _rootContainer.AddChild(headerContainer);
    }

    // ===== 탭 바 =====
    private void BuildTabBar()
    {
        _tabBar = new HBoxContainer();
        _tabBar.AddThemeConstantOverride("separation", 2);

        _tabButtons = new Button[TabNames.Length];
        for (int i = 0; i < TabNames.Length; i++)
        {
            var btn = new Button();
            btn.Text = TabNames[i];
            btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            btn.AddThemeFontSizeOverride("font_size", 10);
            btn.CustomMinimumSize = new Vector2(0, 24);

            int tabIdx = i;
            btn.Pressed += () => SwitchTab(tabIdx);
            _tabButtons[i] = btn;
            _tabBar.AddChild(btn);
        }

        _rootContainer.AddChild(_tabBar);
    }

    // ===== 콘텐츠 영역 =====
    private void BuildContentArea()
    {
        _contentArea = new VBoxContainer();
        _contentArea.AddThemeConstantOverride("separation", 0);

        var meterTab = BuildMeterTab();
        var cardLogTab = BuildScrollTab(out _cardLogRows, 0);
        var receivedTab = BuildScrollTab(out _receivedRows, 1);
        var historyTab = BuildScrollTab(out _historyRows, 2);

        _tabContents = new Control[] { meterTab, cardLogTab, receivedTab, historyTab };
        foreach (var tab in _tabContents)
        {
            tab.Visible = false;
            _contentArea.AddChild(tab);
        }

        _rootContainer.AddChild(_contentArea);
        SwitchTab(0);
    }

    private VBoxContainer BuildMeterTab()
    {
        var container = new VBoxContainer();
        container.AddThemeConstantOverride("separation", 2);

        var colHeader = new HBoxContainer();
        colHeader.CustomMinimumSize = new Vector2(0, 20);

        var nameCol = CreateLabel("플레이어", 10, SubTextColor);
        nameCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        colHeader.AddChild(nameCol);

        var dmgCol = CreateLabel("데미지", 10, SubTextColor);
        dmgCol.CustomMinimumSize = new Vector2(70, 0);
        dmgCol.HorizontalAlignment = HorizontalAlignment.Right;
        colHeader.AddChild(dmgCol);

        var pctCol = CreateLabel("%", 10, SubTextColor);
        pctCol.CustomMinimumSize = new Vector2(45, 0);
        pctCol.HorizontalAlignment = HorizontalAlignment.Right;
        colHeader.AddChild(pctCol);

        container.AddChild(colHeader);

        _meterRows = new VBoxContainer();
        _meterRows.AddThemeConstantOverride("separation", 2);
        container.AddChild(_meterRows);

        return container;
    }

    private ScrollContainer BuildScrollTab(out VBoxContainer rows, int scrollIndex)
    {
        var scroll = new ScrollContainer();
        scroll.CustomMinimumSize = new Vector2(0, _currentHeight);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;

        rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 1);
        rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(rows);

        _scrollContainers[scrollIndex] = scroll;
        return scroll;
    }

    // ===== 푸터 =====
    private void BuildFooter()
    {
        _footerSeparator = new HSeparator();
        _rootContainer.AddChild(_footerSeparator);

        _footerLabel = CreateLabel("턴: 0  |  총합: 0", 11, SubTextColor);
        _footerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _rootContainer.AddChild(_footerLabel);
    }

    // ===== 리사이즈 핸들 =====
    private void BuildResizeHandle()
    {
        _resizeHandleRow = new HBoxContainer();
        var spacer = new Control();
        spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _resizeHandleRow.AddChild(spacer);

        var grip = new ColorRect();
        grip.Color = new Color(0.5f, 0.4f, 0.2f, 0.4f);
        grip.CustomMinimumSize = new Vector2(RESIZE_HANDLE_SIZE, RESIZE_HANDLE_SIZE);
        grip.TooltipText = "드래그하여 크기 조절";
        _resizeHandleRow.AddChild(grip);

        _rootContainer.AddChild(_resizeHandleRow);
    }

    // ===== 이벤트 구독 =====
    private void SubscribeToEvents()
    {
        DamageTracker.Instance.OnDataChanged += () => _needsUpdate = true;
        DamageTracker.Instance.OnCombatLogChanged += () => _needsLogUpdate = true;
    }

    // ===== 설정 로드/저장 =====
    private void LoadSettings()
    {
        try
        {
            var settings = ModSettings.Current;
            _panel.Position = new Vector2(settings.PanelX, settings.PanelY);
            _isVisible = settings.IsVisible;
            _panel.Visible = _isVisible;

            if (settings.PanelWidth > 0)
                _currentWidth = Mathf.Clamp(settings.PanelWidth, MIN_PANEL_WIDTH, MAX_PANEL_WIDTH);
            if (settings.PanelHeight > 0)
                _currentHeight = Mathf.Clamp(settings.PanelHeight, MIN_CONTENT_HEIGHT, MAX_CONTENT_HEIGHT);

            ApplyResize();

            if (settings.ActiveTab >= 0 && settings.ActiveTab < TabNames.Length)
                SwitchTab(settings.ActiveTab);

            if (settings.PlayerColors.Count > 0)
                DamageTracker.Instance.ColorMap.Import(settings.PlayerColors);
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 설정 로드 중 오류: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = ModSettings.Current;
            settings.PanelX = _panel.Position.X;
            settings.PanelY = _panel.Position.Y;
            settings.PanelWidth = _currentWidth;
            settings.PanelHeight = _currentHeight;
            settings.IsVisible = _isVisible;
            settings.ActiveTab = _activeTab;
            settings.PlayerColors = DamageTracker.Instance.ColorMap.Export();
            settings.Save();
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] 설정 저장 중 오류: {ex.Message}");
        }
    }

    /// <summary>리사이즈 값을 UI에 적용.</summary>
    private void ApplyResize()
    {
        _panel.CustomMinimumSize = new Vector2(_currentWidth, 0);
        foreach (var sc in _scrollContainers)
        {
            if (sc != null)
                sc.CustomMinimumSize = new Vector2(0, _currentHeight);
        }
    }

    // ===== 프레임 업데이트 =====
    public override void _Process(double delta)
    {
        if (!_isVisible) return;

        _updateTimer += delta;
        if (_updateTimer >= UPDATE_INTERVAL)
        {
            _updateTimer = 0;

            if (_isMinimized)
            {
                if (_needsUpdate)
                {
                    _needsUpdate = false;
                    RefreshMinimizedTitle();
                }
                return;
            }

            if (_needsUpdate || _needsLogUpdate)
            {
                _needsUpdate = false;
                _needsLogUpdate = false;
                RefreshActiveTab();
            }
        }
    }

    // ===== 입력 처리 =====
    public override void _Input(InputEvent @event)
    {
        // F7/F8: 항상 처리 (패널 숨김 상태에서도)
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.F7)
            {
                ToggleVisibility();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (keyEvent.Keycode == Key.F8)
            {
                ModEntry.SetDebugMode(!ModEntry.DebugMode);
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (!_isVisible) return;

        // 드래그 중
        if (_isDragging)
        {
            if (@event is InputEventMouseMotion motion)
            {
                _panel.Position = motion.Position - _dragOffset;
                GetViewport().SetInputAsHandled();
            }
            else if (@event is InputEventMouseButton mb2 &&
                     mb2.ButtonIndex == MouseButton.Left && !mb2.Pressed)
            {
                _isDragging = false;
                SaveSettings();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // 리사이즈 중
        if (_isResizing)
        {
            if (@event is InputEventMouseMotion resizeMotion)
            {
                var diff = resizeMotion.Position - _resizeStartMouse;
                _currentWidth = Mathf.Clamp(_resizeStartSize.X + diff.X, MIN_PANEL_WIDTH, MAX_PANEL_WIDTH);
                _currentHeight = Mathf.Clamp(_resizeStartSize.Y + diff.Y, MIN_CONTENT_HEIGHT, MAX_CONTENT_HEIGHT);
                ApplyResize();
                GetViewport().SetInputAsHandled();
            }
            else if (@event is InputEventMouseButton rmb &&
                     rmb.ButtonIndex == MouseButton.Left && !rmb.Pressed)
            {
                _isResizing = false;
                SaveSettings();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // 클릭 시작
        if (@event is InputEventMouseButton mb &&
            mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            // 리사이즈 체크 (우하단) — 최소화 상태에선 비활성
            if (!_isMinimized && IsInResizeArea(mb.Position))
            {
                _isResizing = true;
                _resizeStartMouse = mb.Position;
                _resizeStartSize = new Vector2(_currentWidth, _currentHeight);
                GetViewport().SetInputAsHandled();
                return;
            }

            // 드래그 체크 (헤더)
            if (IsInDragArea(mb.Position))
            {
                _isDragging = true;
                _dragOffset = mb.Position - _panel.Position;
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>헤더의 드래그 가능 영역 (우측 버튼 60px 제외).</summary>
    private bool IsInDragArea(Vector2 mousePos)
    {
        var panelPos = _panel.Position;
        var panelWidth = _panel.Size.X;
        var dragRect = new Rect2(panelPos.X, panelPos.Y, panelWidth - 60, HEADER_HEIGHT);
        return dragRect.HasPoint(mousePos);
    }

    /// <summary>우하단 리사이즈 영역.</summary>
    private bool IsInResizeArea(Vector2 mousePos)
    {
        var panelPos = _panel.Position;
        var panelSize = _panel.Size;
        var handleRect = new Rect2(
            panelPos.X + panelSize.X - RESIZE_HANDLE_SIZE - PADDING,
            panelPos.Y + panelSize.Y - RESIZE_HANDLE_SIZE - PADDING,
            RESIZE_HANDLE_SIZE + PADDING,
            RESIZE_HANDLE_SIZE + PADDING);
        return handleRect.HasPoint(mousePos);
    }

    // ===== 탭 전환 =====
    private void SwitchTab(int tabIndex)
    {
        _activeTab = tabIndex;

        for (int i = 0; i < _tabContents.Length; i++)
        {
            _tabContents[i].Visible = (i == _activeTab);
            UpdateTabButtonStyle(_tabButtons[i], i == _activeTab);
        }

        _needsUpdate = true;
        _needsLogUpdate = true;

        // 전투기록 탭은 탭 전환 시에만 로드
        if (tabIndex == 3)
            RefreshHistoryTab();
    }

    private void UpdateTabButtonStyle(Button btn, bool active)
    {
        var style = new StyleBoxFlat
        {
            BgColor = active ? TabActiveBg : TabInactiveBg,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 0,
        };
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeColorOverride("font_color", active ? TextColor : SubTextColor);
    }

    // ===== 최소화 시 타이틀 갱신 =====
    private void RefreshMinimizedTitle()
    {
        var snapshot = DamageTracker.Instance.GetSnapshot();
        int grandTotal = 0;
        int turnTotal = 0;
        foreach (var s in snapshot)
        {
            grandTotal += s.TotalDamage;
            turnTotal += s.CurrentTurnDamage;
        }

        string pctText;
        if (snapshot.Count <= 1)
        {
            pctText = "100%";
        }
        else
        {
            var parts = new List<string>();
            foreach (var s in snapshot)
                parts.Add($"{s.Percentage:F0}%");
            pctText = string.Join("/", parts);
        }

        _titleLabel.Text = $"총:{grandTotal:N0} | 턴:{turnTotal:N0} | {pctText}";
    }

    // ===== 탭별 리프레시 =====
    private void RefreshActiveTab()
    {
        switch (_activeTab)
        {
            case 0: RefreshMeterTab(); break;
            case 1: RefreshCardLogTab(); break;
            case 2: RefreshReceivedTab(); break;
            case 3: break; // 전투기록은 탭 전환 시에만 로드 (디스크 I/O)
        }

        // 푸터 업데이트
        int turn = DamageTracker.Instance.CombatTurn;
        var snapshot = DamageTracker.Instance.GetSnapshot();
        int grandTotal = 0;
        foreach (var s in snapshot) grandTotal += s.TotalDamage;

        _footerLabel.Text = $"턴: {turn}  |  총합: {grandTotal:N0}";

        if (!DamageTracker.Instance.IsActive && grandTotal > 0)
            _titleLabel.Text = "데미지 미터 (완료)";
        else
            _titleLabel.Text = "데미지 미터";
    }

    // ----- 탭 0: 미터 -----
    private void RefreshMeterTab()
    {
        var snapshot = DamageTracker.Instance.GetSnapshot();
        ClearChildren(_meterRows);

        if (snapshot.Count == 0)
        {
            var emptyLabel = CreateLabel("전투 대기중...", 11, SubTextColor);
            emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _meterRows.AddChild(emptyLabel);
            return;
        }

        var colorMap = DamageTracker.Instance.ColorMap;
        foreach (var entry in snapshot)
        {
            var color = colorMap.GetColor(entry.PlayerId);
            CreateMeterRow(entry, color);
        }
    }

    private void CreateMeterRow(PlayerDamageSnapshot entry, Color barColor)
    {
        var rowContainer = new VBoxContainer();
        rowContainer.AddThemeConstantOverride("separation", 1);

        // 이름 + 데미지 + 비율
        var dataRow = new HBoxContainer();
        dataRow.AddThemeConstantOverride("separation", 4);

        var nameLabel = CreateLabel(entry.DisplayName, 12, TextColor);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dataRow.AddChild(nameLabel);

        var dmgLabel = CreateLabel(entry.TotalDamage.ToString("N0"), 12, TextColor);
        dmgLabel.CustomMinimumSize = new Vector2(70, 0);
        dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dataRow.AddChild(dmgLabel);

        var pctLabel = CreateLabel($"{entry.Percentage:F1}%", 12, barColor);
        pctLabel.CustomMinimumSize = new Vector2(45, 0);
        pctLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dataRow.AddChild(pctLabel);

        rowContainer.AddChild(dataRow);

        // 비율 바
        var barBg = new ColorRect();
        barBg.CustomMinimumSize = new Vector2(0, 4);
        barBg.Color = new Color(0.2f, 0.2f, 0.25f);
        barBg.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rowContainer.AddChild(barBg);

        var bar = new ColorRect();
        bar.Color = barColor;
        float barWidth = (_currentWidth - PADDING * 2) * (entry.Percentage / 100f);
        bar.CustomMinimumSize = new Vector2(Mathf.Max(barWidth, 2), 4);
        bar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        rowContainer.AddChild(bar);

        // 상세 통계
        var parts = new List<string>();
        if (entry.CurrentTurnDamage > 0)
            parts.Add($"이번턴: {entry.CurrentTurnDamage:N0}");
        if (entry.DamagePerTurn > 0)
            parts.Add($"턴당: {entry.DamagePerTurn:F1}");
        if (entry.MaxSingleHit > 0)
            parts.Add($"최대: {entry.MaxSingleHit:N0}");
        if (entry.PoisonDamage > 0)
            parts.Add($"독: {entry.PoisonDamage:N0}");

        if (parts.Count > 0)
        {
            var detailLabel = CreateLabel("  " + string.Join("  |  ", parts), 9, SubTextColor);
            rowContainer.AddChild(detailLabel);
        }

        _meterRows.AddChild(rowContainer);
    }

    // ----- 탭 1: 카드 로그 -----
    private void RefreshCardLogTab()
    {
        var allLog = DamageTracker.Instance.GetCombatLogSnapshot();
        ClearChildren(_cardLogRows);

        // 카드 관련 이벤트 필터링
        // DamageDealt, BlockGained 키 수집 (CardPlayed 중복 제거용)
        var handledCardKeys = new HashSet<string>();
        foreach (var evt in allLog)
        {
            if (evt.EventType == CombatEventType.DamageDealt ||
                evt.EventType == CombatEventType.BlockGained)
            {
                handledCardKeys.Add($"{evt.Turn}_{evt.PlayerId}_{evt.CardName}");
            }
        }

        var cardEvents = new List<CombatEvent>();
        foreach (var evt in allLog)
        {
            switch (evt.EventType)
            {
                case CombatEventType.DamageDealt:
                case CombatEventType.BlockGained:
                case CombatEventType.PoisonDamage:
                    cardEvents.Add(evt);
                    break;
                case CombatEventType.CardPlayed:
                    // DamageDealt나 BlockGained에서 이미 표시되는 카드는 건너뜀
                    var key = $"{evt.Turn}_{evt.PlayerId}_{evt.CardName}";
                    if (!handledCardKeys.Contains(key))
                        cardEvents.Add(evt);
                    break;
            }
        }

        if (cardEvents.Count == 0)
        {
            var emptyLabel = CreateLabel("카드 로그가 없습니다.", 11, SubTextColor);
            emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _cardLogRows.AddChild(emptyLabel);
            return;
        }

        var colorMap = DamageTracker.Instance.ColorMap;

        // 최신 100개, 역순 (최신이 위)
        int start = Math.Max(0, cardEvents.Count - 100);
        for (int i = cardEvents.Count - 1; i >= start; i--)
        {
            var evt = cardEvents[i];
            var color = colorMap.GetColor(evt.PlayerId);
            CreateCardEventRow(evt, color);
        }
    }

    private void CreateCardEventRow(CombatEvent evt, Color playerColor)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);

        // 턴 번호
        var turnLabel = CreateLabel($"[T{evt.Turn}]", 10, SubTextColor);
        turnLabel.CustomMinimumSize = new Vector2(32, 0);
        row.AddChild(turnLabel);

        // 플레이어 이름
        var nameLabel = CreateLabel(evt.PlayerName, 10, playerColor);
        nameLabel.CustomMinimumSize = new Vector2(55, 0);
        nameLabel.ClipText = true;
        row.AddChild(nameLabel);

        // 이벤트 타입별 다른 표시
        switch (evt.EventType)
        {
            case CombatEventType.DamageDealt:
            {
                var killText = evt.WasKill ? " 처치!" : "";
                var infoLabel = CreateLabel($"{evt.CardName} → {evt.TargetName}", 10, TextColor);
                infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                infoLabel.ClipText = true;
                row.AddChild(infoLabel);

                var dmgColor = evt.WasKill ? new Color(1f, 0.4f, 0.4f) : TextColor;
                var dmgLabel = CreateLabel($"{evt.Damage:N0}{killText}", 10, dmgColor);
                dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
                dmgLabel.CustomMinimumSize = new Vector2(65, 0);
                row.AddChild(dmgLabel);
                break;
            }
            case CombatEventType.BlockGained:
            {
                var infoLabel = CreateLabel(evt.CardName, 10, BlockColor);
                infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                infoLabel.ClipText = true;
                row.AddChild(infoLabel);

                var blockLabel = CreateLabel($"+{evt.Damage}블록", 10, BlockColor);
                blockLabel.HorizontalAlignment = HorizontalAlignment.Right;
                blockLabel.CustomMinimumSize = new Vector2(65, 0);
                row.AddChild(blockLabel);
                break;
            }
            case CombatEventType.PoisonDamage:
            {
                var infoLabel = CreateLabel($"독 → {evt.TargetName}", 10, PoisonColor);
                infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                infoLabel.ClipText = true;
                row.AddChild(infoLabel);

                var dmgLabel = CreateLabel($"{evt.Damage:N0}", 10, PoisonColor);
                dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
                dmgLabel.CustomMinimumSize = new Vector2(65, 0);
                row.AddChild(dmgLabel);
                break;
            }
            case CombatEventType.CardPlayed:
            {
                // TargetName에 cardType이 저장됨
                var typeStr = !string.IsNullOrEmpty(evt.TargetName) ? $" ({evt.TargetName})" : "";
                var infoLabel = CreateLabel($"{evt.CardName}{typeStr}", 10, CardPlayedColor);
                infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                infoLabel.ClipText = true;
                row.AddChild(infoLabel);

                // 빈 공간 (데미지/블록 없음)
                var spacer = new Control();
                spacer.CustomMinimumSize = new Vector2(65, 0);
                row.AddChild(spacer);
                break;
            }
        }

        _cardLogRows.AddChild(row);
    }

    // ----- 탭 2: 받은 피해 -----
    private void RefreshReceivedTab()
    {
        ClearChildren(_receivedRows);

        var snapshot = DamageTracker.Instance.GetSnapshot();
        var colorMap = DamageTracker.Instance.ColorMap;

        // 상단: 플레이어별 요약
        if (snapshot.Count > 0)
        {
            var summaryHeader = CreateLabel("── 플레이어별 받은 피해 ──", 10, SubTextColor);
            summaryHeader.HorizontalAlignment = HorizontalAlignment.Center;
            _receivedRows.AddChild(summaryHeader);

            foreach (var player in snapshot)
            {
                var color = colorMap.GetColor(player.PlayerId);
                var container = new VBoxContainer();
                container.AddThemeConstantOverride("separation", 0);

                // 이름 + 사망 횟수
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 4);

                var nameLabel = CreateLabel(player.DisplayName, 11, color);
                nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(nameLabel);

                if (player.DeathCount > 0)
                {
                    var deathLabel = CreateLabel($"사망:{player.DeathCount}", 10, new Color(1f, 0.3f, 0.3f));
                    row.AddChild(deathLabel);
                }

                container.AddChild(row);

                // 받은피해 / 막은피해 / 실제피해
                int unblocked = player.TotalDamageReceived - player.TotalBlockedReceived;
                var detailText = $"  받은피해: {player.TotalDamageReceived:N0}  |  막은피해: {player.TotalBlockedReceived:N0}  |  실제: {unblocked:N0}";
                var detailLabel = CreateLabel(detailText, 9, SubTextColor);
                container.AddChild(detailLabel);

                _receivedRows.AddChild(container);
            }

            var sep = new HSeparator();
            _receivedRows.AddChild(sep);
        }

        // 하단: 받은 피해 로그
        var receivedLog = DamageTracker.Instance.GetCombatLogSnapshot()
            .Where(e => e.EventType == CombatEventType.DamageReceived || e.EventType == CombatEventType.Death)
            .ToList();

        if (receivedLog.Count == 0)
        {
            var emptyLabel = CreateLabel("받은 피해 기록이 없습니다.", 11, SubTextColor);
            emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _receivedRows.AddChild(emptyLabel);
            return;
        }

        int start = Math.Max(0, receivedLog.Count - 100);
        for (int i = receivedLog.Count - 1; i >= start; i--)
        {
            var evt = receivedLog[i];
            CreateReceivedLogRow(evt);
        }
    }

    private void CreateReceivedLogRow(CombatEvent evt)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);

        var turnLabel = CreateLabel($"[T{evt.Turn}]", 10, SubTextColor);
        turnLabel.CustomMinimumSize = new Vector2(32, 0);
        row.AddChild(turnLabel);

        var isDeath = evt.EventType == CombatEventType.Death;
        var deathColor = new Color(1f, 0.3f, 0.3f);

        var sourceLabel = CreateLabel($"{evt.SourceName} → {evt.PlayerName}", 10,
            isDeath ? deathColor : TextColor);
        sourceLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sourceLabel.ClipText = true;
        row.AddChild(sourceLabel);

        // 피해 표시: 총피해 (막힘:X)
        var deathText = isDeath ? " 사망!" : "";
        string dmgText;
        if (evt.BlockedDamage > 0)
            dmgText = $"{evt.Damage:N0} (막힘:{evt.BlockedDamage}){deathText}";
        else
            dmgText = $"{evt.Damage:N0}{deathText}";

        var dmgLabel = CreateLabel(dmgText, 10, isDeath ? deathColor : TextColor);
        dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dmgLabel.CustomMinimumSize = new Vector2(95, 0);
        row.AddChild(dmgLabel);

        _receivedRows.AddChild(row);
    }

    // ----- 탭 3: 전투 기록 -----
    private void RefreshHistoryTab()
    {
        ClearChildren(_historyRows);

        var store = new CombatHistoryStore();
        var historyList = store.LoadHistoryList();

        if (historyList.Count == 0)
        {
            var emptyLabel = CreateLabel("저장된 전투 기록이 없습니다.", 11, SubTextColor);
            emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _historyRows.AddChild(emptyLabel);
            return;
        }

        // 새로고침 버튼
        var refreshBtn = new Button();
        refreshBtn.Text = "새로고침";
        refreshBtn.AddThemeFontSizeOverride("font_size", 10);
        refreshBtn.CustomMinimumSize = new Vector2(0, 24);
        refreshBtn.Pressed += () =>
        {
            ClearChildren(_historyRows);
            RefreshHistoryTab();
        };
        ApplyButtonStyle(refreshBtn);
        _historyRows.AddChild(refreshBtn);

        foreach (var meta in historyList.Take(20))
        {
            var summary = store.LoadCombat(meta.FilePath);
            if (summary == null) continue;

            CreateHistoryRow(summary);
        }
    }

    private void CreateHistoryRow(CombatSummary summary)
    {
        var container = new VBoxContainer();
        container.AddThemeConstantOverride("separation", 1);

        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 4);

        var dateLabel = CreateLabel(summary.StartTime.ToLocalTime().ToString("MM/dd HH:mm"), 10, SubTextColor);
        headerRow.AddChild(dateLabel);

        var turnsLabel = CreateLabel($"{summary.TotalTurns}턴", 10, TextColor);
        headerRow.AddChild(turnsLabel);

        var totalLabel = CreateLabel($"총 {summary.TotalDamageDealt:N0} 데미지", 10, TextColor);
        totalLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        totalLabel.HorizontalAlignment = HorizontalAlignment.Right;
        headerRow.AddChild(totalLabel);

        container.AddChild(headerRow);

        // 플레이어 통계
        foreach (var player in summary.Players)
        {
            var playerRow = new HBoxContainer();
            var nameLabel = CreateLabel($"  {player.DisplayName}", 9, SubTextColor);
            nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            playerRow.AddChild(nameLabel);

            var statLabel = CreateLabel($"{player.TotalDamage:N0} ({player.DamagePercentage:F0}%)", 9, SubTextColor);
            playerRow.AddChild(statLabel);

            container.AddChild(playerRow);
        }

        var sep = new HSeparator();
        container.AddChild(sep);

        _historyRows.AddChild(container);
    }

    // ===== 버튼 이벤트 =====
    private void OnMinimizePressed()
    {
        _isMinimized = !_isMinimized;

        // 최소화: 탭바, 콘텐츠, 푸터, 리사이즈 핸들 숨기기
        _tabBar.Visible = !_isMinimized;
        _contentArea.Visible = !_isMinimized;
        _footerSeparator.Visible = !_isMinimized;
        _footerLabel.Visible = !_isMinimized;
        _resizeHandleRow.Visible = !_isMinimized;

        if (_isMinimized)
        {
            _minBtn.Text = "+";
            RefreshMinimizedTitle();
        }
        else
        {
            _minBtn.Text = "−";
            _titleLabel.Text = "데미지 미터";
            _needsUpdate = true;
            _needsLogUpdate = true;
        }
    }

    private void OnClosePressed() => ToggleVisibility();

    public void ToggleVisibility()
    {
        _isVisible = !_isVisible;
        _panel.Visible = _isVisible;
        SaveSettings();
    }

    // ===== 유틸리티 =====
    private static Label CreateLabel(string text, int fontSize, Color color)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Button CreateHeaderButton(string text)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(24, 24);
        ApplyButtonStyle(btn);
        return btn;
    }

    private static void ApplyButtonStyle(Button button)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.2f, 0.18f, 0.25f, 0.9f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        button.AddThemeStyleboxOverride("normal", style);

        var hoverStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.3f, 0.28f, 0.35f, 0.9f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        button.AddThemeStyleboxOverride("hover", hoverStyle);
        button.AddThemeFontSizeOverride("font_size", 14);
    }

    private static void ClearChildren(Control parent)
    {
        foreach (var child in parent.GetChildren())
            child.QueueFree();
    }
}
