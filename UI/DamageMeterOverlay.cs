using System.Reflection;
using Godot;
using HarmonyLib;
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
    private static readonly string[] TabNames = { L10N.TabMeter, L10N.TabCardLog, L10N.TabReceived };

    // 탭별 컨텐츠
    private VBoxContainer _meterRows = null!;
    private VBoxContainer _cardLogRows = null!;
    private VBoxContainer _receivedRows = null!;
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

    // 누적 모드
    private bool _showRunTotal;
    private Button _segCombatBtn = null!;
    private Button _segRunBtn = null!;
    private HBoxContainer _meterToggleBar = null!;

    // 카드로그 다중타격 펼침 상태
    private readonly HashSet<string> _expandedMultiHits = new();

    // 카드로그 필터 상태
    private int _cardLogFilterTurn = -1;           // -1 = 전체
    private string _cardLogFilterPlayerId = "";     // "" = 전체
    private OptionButton _turnFilterOption = null!;
    private OptionButton _playerFilterOption = null!;
    private HBoxContainer _cardLogFilterBar = null!;

    // 카드로그 가상 스크롤
    private const float VIRTUAL_ROW_HEIGHT = 22f;
    private const int VIRTUAL_BUFFER_ROWS = 3;
    private List<List<CombatEvent>> _cachedGroups = new();
    private Control _topSpacer = null!;
    private Control _bottomSpacer = null!;
    private int _virtualRangeStart = -1;
    private int _virtualRangeEnd = -1;
    private int _cachedGroupsHash;

    // 카드 호버 팁 (게임 내장 시스템)
    private Control? _activeHoverTipSet;
    private static Type? _hoverTipSetType;
    private static MethodInfo? _createAndShowMethod;   // CreateAndShow(Control, IHoverTip, alignment)
    private static MethodInfo? _fromCardMethod;        // HoverTipFactory.FromCard(CardModel, bool)
    private static MethodInfo? _clearMethod;           // NHoverTipSet.Clear()
    private static object? _alignRight;                // HoverTipAlignment.Right
    private static bool _hoverTipSystemChecked;

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

        _titleLabel = CreateLabel(L10N.Title, 13, TextColor);
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
        var cardLogTab = BuildCardLogTab();
        var receivedTab = BuildScrollTab(out _receivedRows, 1);

        _tabContents = new Control[] { meterTab, cardLogTab, receivedTab };
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

        var nameCol = CreateLabel(L10N.HeaderPlayer, 10, SubTextColor);
        nameCol.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        colHeader.AddChild(nameCol);

        var dmgCol = CreateLabel(L10N.HeaderDamage, 10, SubTextColor);
        dmgCol.CustomMinimumSize = new Vector2(70, 0);
        dmgCol.HorizontalAlignment = HorizontalAlignment.Right;
        colHeader.AddChild(dmgCol);

        var pctCol = CreateLabel("%", 10, SubTextColor);
        pctCol.CustomMinimumSize = new Vector2(45, 0);
        pctCol.HorizontalAlignment = HorizontalAlignment.Right;
        colHeader.AddChild(pctCol);

        container.AddChild(colHeader);

        // 세그먼트 토글 바: [이번 전투 | 누적]
        _meterToggleBar = new HBoxContainer();
        _meterToggleBar.AddThemeConstantOverride("separation", 0);
        _meterToggleBar.CustomMinimumSize = new Vector2(0, 22);

        _segCombatBtn = new Button();
        _segCombatBtn.Text = L10N.ToggleCombat;
        _segCombatBtn.AddThemeFontSizeOverride("font_size", 9);
        _segCombatBtn.CustomMinimumSize = new Vector2(0, 20);
        _segCombatBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _segCombatBtn.Pressed += () => { _showRunTotal = false; UpdateSegmentStyle(); _needsUpdate = true; };
        _meterToggleBar.AddChild(_segCombatBtn);

        _segRunBtn = new Button();
        _segRunBtn.Text = L10N.ToggleRun;
        _segRunBtn.AddThemeFontSizeOverride("font_size", 9);
        _segRunBtn.CustomMinimumSize = new Vector2(0, 20);
        _segRunBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _segRunBtn.Pressed += () => { _showRunTotal = true; UpdateSegmentStyle(); _needsUpdate = true; };
        _meterToggleBar.AddChild(_segRunBtn);

        container.AddChild(_meterToggleBar);
        UpdateSegmentStyle();

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

    // ===== 카드로그 탭 (필터 포함) =====
    private VBoxContainer BuildCardLogTab()
    {
        var container = new VBoxContainer();
        container.AddThemeConstantOverride("separation", 2);

        // 필터 바
        _cardLogFilterBar = new HBoxContainer();
        _cardLogFilterBar.AddThemeConstantOverride("separation", 4);
        _cardLogFilterBar.CustomMinimumSize = new Vector2(0, 24);

        // 턴 필터
        var turnLabel = CreateLabel(L10N.FilterTurnLabel, 9, SubTextColor);
        _cardLogFilterBar.AddChild(turnLabel);

        _turnFilterOption = new OptionButton();
        _turnFilterOption.AddThemeFontSizeOverride("font_size", 9);
        _turnFilterOption.CustomMinimumSize = new Vector2(65, 22);
        _turnFilterOption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _turnFilterOption.AddItem(L10N.FilterAll, 0);
        _turnFilterOption.ItemSelected += OnTurnFilterChanged;
        ApplyOptionButtonStyle(_turnFilterOption);
        _cardLogFilterBar.AddChild(_turnFilterOption);

        // 플레이어 필터
        var playerLabel = CreateLabel(L10N.FilterPlayerLabel, 9, SubTextColor);
        _cardLogFilterBar.AddChild(playerLabel);

        _playerFilterOption = new OptionButton();
        _playerFilterOption.AddThemeFontSizeOverride("font_size", 9);
        _playerFilterOption.CustomMinimumSize = new Vector2(75, 22);
        _playerFilterOption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _playerFilterOption.AddItem(L10N.FilterAll, 0);
        _playerFilterOption.ItemSelected += OnPlayerFilterChanged;
        ApplyOptionButtonStyle(_playerFilterOption);
        _cardLogFilterBar.AddChild(_playerFilterOption);

        container.AddChild(_cardLogFilterBar);

        // 스크롤 + 로그 행 (가상 스크롤)
        var scroll = new ScrollContainer();
        scroll.CustomMinimumSize = new Vector2(0, _currentHeight - 28);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;

        var outerBox = new VBoxContainer();
        outerBox.AddThemeConstantOverride("separation", 0);
        outerBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        _topSpacer = new Control();
        _topSpacer.CustomMinimumSize = new Vector2(0, 0);
        outerBox.AddChild(_topSpacer);

        _cardLogRows = new VBoxContainer();
        _cardLogRows.AddThemeConstantOverride("separation", 1);
        _cardLogRows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        outerBox.AddChild(_cardLogRows);

        _bottomSpacer = new Control();
        _bottomSpacer.CustomMinimumSize = new Vector2(0, 0);
        outerBox.AddChild(_bottomSpacer);

        scroll.AddChild(outerBox);
        scroll.GetVScrollBar().ValueChanged += _ => RenderVisibleCardLogRows();

        _scrollContainers[0] = scroll;
        container.AddChild(scroll);

        return container;
    }

    private void OnTurnFilterChanged(long index)
    {
        if (index == 0)
            _cardLogFilterTurn = -1; // 전체
        else
        {
            var text = _turnFilterOption.GetItemText((int)index);
            if (text.StartsWith("T") && int.TryParse(text[1..], out int turn))
                _cardLogFilterTurn = turn;
            else
                _cardLogFilterTurn = -1;
        }
        _needsLogUpdate = true;
    }

    private void OnPlayerFilterChanged(long index)
    {
        if (index == 0)
            _cardLogFilterPlayerId = ""; // 전체
        else
            _cardLogFilterPlayerId = _playerFilterOption.GetItemMetadata((int)index).AsString();
        _needsLogUpdate = true;
    }

    private static void ApplyOptionButtonStyle(OptionButton optBtn)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.15f, 0.2f, 0.9f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 4, ContentMarginRight = 4,
        };
        optBtn.AddThemeStyleboxOverride("normal", style);

        var hoverStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.22f, 0.22f, 0.3f, 0.9f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 4, ContentMarginRight = 4,
        };
        optBtn.AddThemeStyleboxOverride("hover", hoverStyle);
        optBtn.AddThemeColorOverride("font_color", TextColor);
    }

    /// <summary>필터 드롭다운의 선택지를 현재 전투 로그 기반으로 갱신.</summary>
    private void UpdateCardLogFilterOptions(IReadOnlyList<CombatEvent> cardEvents)
    {
        // 턴 목록 수집
        var turns = new SortedSet<int>();
        var players = new Dictionary<string, string>(); // id → name

        foreach (var evt in cardEvents)
        {
            turns.Add(evt.Turn);
            if (!players.ContainsKey(evt.PlayerId))
                players[evt.PlayerId] = evt.PlayerName;
        }

        // 턴 필터 갱신 (선택값 유지)
        int prevTurn = _cardLogFilterTurn;
        _turnFilterOption.Clear();
        _turnFilterOption.AddItem(L10N.FilterAll);
        _turnFilterOption.SetItemMetadata(0, "");
        int selectTurnIdx = 0;

        int idx = 1;
        foreach (var t in turns)
        {
            _turnFilterOption.AddItem($"T{t}");
            _turnFilterOption.SetItemMetadata(idx, t.ToString());
            if (prevTurn == t) selectTurnIdx = idx;
            idx++;
        }
        _turnFilterOption.Selected = selectTurnIdx;

        // 플레이어 필터 갱신 (선택값 유지)
        string prevPlayer = _cardLogFilterPlayerId;
        _playerFilterOption.Clear();
        _playerFilterOption.AddItem(L10N.FilterAll);
        _playerFilterOption.SetItemMetadata(0, "");
        int selectPlayerIdx = 0;

        idx = 1;
        foreach (var (pid, pname) in players)
        {
            _playerFilterOption.AddItem(pname);
            _playerFilterOption.SetItemMetadata(idx, pid);
            if (prevPlayer == pid) selectPlayerIdx = idx;
            idx++;
        }
        _playerFilterOption.Selected = selectPlayerIdx;
    }

    // ===== 푸터 =====
    private void BuildFooter()
    {
        _footerSeparator = new HSeparator();
        _rootContainer.AddChild(_footerSeparator);

        _footerLabel = CreateLabel(L10N.FooterInitial, 11, SubTextColor);
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
        grip.TooltipText = L10N.ResizeTooltip;
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

            _showRunTotal = settings.ShowRunTotal;
            UpdateSegmentStyle();

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
            settings.ShowRunTotal = _showRunTotal;
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

    private void UpdateSegmentStyle()
    {
        var activeStyle = new StyleBoxFlat
        {
            BgColor = TabActiveBg,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 0,
        };
        var inactiveStyleLeft = new StyleBoxFlat
        {
            BgColor = TabInactiveBg,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 0,
        };
        var activeStyleRight = new StyleBoxFlat
        {
            BgColor = TabActiveBg,
            CornerRadiusTopLeft = 0, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 3,
        };
        var inactiveStyleRight = new StyleBoxFlat
        {
            BgColor = TabInactiveBg,
            CornerRadiusTopLeft = 0, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 3,
        };

        var combatStyle = _showRunTotal ? inactiveStyleLeft : activeStyle;
        _segCombatBtn.AddThemeStyleboxOverride("normal", combatStyle);
        _segCombatBtn.AddThemeStyleboxOverride("hover", combatStyle);
        _segCombatBtn.AddThemeColorOverride("font_color", _showRunTotal ? SubTextColor : TextColor);

        var runStyle = _showRunTotal ? activeStyleRight : inactiveStyleRight;
        _segRunBtn.AddThemeStyleboxOverride("normal", runStyle);
        _segRunBtn.AddThemeStyleboxOverride("hover", runStyle);
        _segRunBtn.AddThemeColorOverride("font_color", _showRunTotal ? TextColor : SubTextColor);
    }

    // ===== 최소화 시 타이틀 갱신 =====
    private void RefreshMinimizedTitle()
    {
        var snapshot = DamageTracker.Instance.GetSnapshot();
        var localId = DamageTracker.Instance.LocalPlayerId;
        int turn = DamageTracker.Instance.CombatTurn;

        if (snapshot.Count == 0)
        {
            _titleLabel.Text = L10N.TitleWaiting;
            return;
        }

        // 내 플레이어 찾기 (여러 방법으로 시도)
        PlayerDamageSnapshot? me = null;

        // 1) LocalPlayerId로 정확히 매칭
        if (!string.IsNullOrEmpty(localId))
        {
            foreach (var s in snapshot)
            {
                if (s.PlayerId == localId)
                {
                    me = s;
                    break;
                }
            }
        }

        // 2) 매칭 실패 시 → 솔로(1인)이면 유일한 플레이어 사용
        if (!me.HasValue && snapshot.Count == 1)
        {
            me = snapshot[0];
        }

        // 3) 그래도 매칭 실패 시 → 첫 번째 플레이어 사용 (fallback)
        if (!me.HasValue && snapshot.Count > 0)
        {
            me = snapshot[0];
            ModEntry.LogDebug($"[DamageMeter] Minimized: LocalPlayerId '{localId}' not matched, using first player '{snapshot[0].PlayerId}'");
        }

        if (me.HasValue)
        {
            var m = me.Value;
            // 내 이름 | 내 데미지 (내 %) | T{턴번호} 턴데미지
            _titleLabel.Text = $"{m.DisplayName} {m.TotalDamage:N0} ({m.Percentage:F0}%) T{turn} +{m.CurrentTurnDamage:N0}";
        }
        else
        {
            _titleLabel.Text = L10N.TitleTurn(turn);
        }
    }

    // ===== 탭별 리프레시 =====
    private void RefreshActiveTab()
    {
        switch (_activeTab)
        {
            case 0: RefreshMeterTab(); break;
            case 1: RefreshCardLogTab(); break;
            case 2: RefreshReceivedTab(); break;
        }

        // 푸터 업데이트
        if (_activeTab == 0 && _showRunTotal)
        {
            var runSnapshot = DamageTracker.Instance.GetRunSnapshot();
            int runGrandTotal = 0;
            foreach (var s in runSnapshot) runGrandTotal += s.TotalDamage;
            int combats = DamageTracker.Instance.RunCombatCount;
            _footerLabel.Text = L10N.FooterRun(combats, runGrandTotal.ToString("N0"));
        }
        else
        {
            int turn = DamageTracker.Instance.CombatTurn;
            var snapshot = DamageTracker.Instance.GetSnapshot();
            int grandTotal = 0;
            foreach (var s in snapshot) grandTotal += s.TotalDamage;
            _footerLabel.Text = L10N.Footer(turn, grandTotal.ToString("N0"));
        }

        if (!DamageTracker.Instance.IsActive)
        {
            var currentSnapshot = DamageTracker.Instance.GetSnapshot();
            int currentTotal = 0;
            foreach (var s in currentSnapshot) currentTotal += s.TotalDamage;
            if (currentTotal > 0)
                _titleLabel.Text = L10N.TitleDone;
            else
                _titleLabel.Text = L10N.Title;
        }
        else
        {
            _titleLabel.Text = L10N.Title;
        }
    }

    // ----- 탭 0: 미터 -----
    private void RefreshMeterTab()
    {
        var snapshot = _showRunTotal
            ? DamageTracker.Instance.GetRunSnapshot()
            : DamageTracker.Instance.GetSnapshot();
        ClearChildren(_meterRows);

        if (snapshot.Count == 0)
        {
            var emptyLabel = CreateLabel(L10N.EmptyWaiting, 11, SubTextColor);
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
        if (_showRunTotal)
        {
            // 누적 모드: 전투당 데미지 + 최대 단일 타격
            if (entry.DamagePerTurn > 0)
                parts.Add(L10N.StatPerCombat(entry.DamagePerTurn.ToString("F0")));
            if (entry.MaxSingleHit > 0)
                parts.Add(L10N.StatMax(entry.MaxSingleHit.ToString("N0")));
            if (entry.PoisonDamage > 0)
                parts.Add(L10N.StatPoison(entry.PoisonDamage.ToString("N0")));
        }
        else
        {
            // 이번 전투 모드: 이번턴 + 턴당 + 최대 + 독
            if (entry.CurrentTurnDamage > 0)
                parts.Add(L10N.StatThisTurn(entry.CurrentTurnDamage.ToString("N0")));
            if (entry.DamagePerTurn > 0)
                parts.Add(L10N.StatPerTurn(entry.DamagePerTurn.ToString("F1")));
            if (entry.MaxSingleHit > 0)
                parts.Add(L10N.StatMax(entry.MaxSingleHit.ToString("N0")));
            if (entry.PoisonDamage > 0)
                parts.Add(L10N.StatPoison(entry.PoisonDamage.ToString("N0")));
        }

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

        // 카드 관련 이벤트 필터링
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
                    var key = $"{evt.Turn}_{evt.PlayerId}_{evt.CardName}";
                    if (!handledCardKeys.Contains(key))
                        cardEvents.Add(evt);
                    break;
            }
        }

        UpdateCardLogFilterOptions(cardEvents);

        // 필터 적용
        var filtered = cardEvents.AsEnumerable();
        if (_cardLogFilterTurn >= 0)
            filtered = filtered.Where(e => e.Turn == _cardLogFilterTurn);
        if (!string.IsNullOrEmpty(_cardLogFilterPlayerId))
            filtered = filtered.Where(e => e.PlayerId == _cardLogFilterPlayerId);
        var filteredList = filtered.ToList();

        // 그룹핑 후 역순 (최신이 위)
        var groups = GroupConsecutiveHits(filteredList);
        groups.Reverse();

        // 데이터 해시로 변경 여부 판단
        int newHash = allLog.Count ^ filteredList.Count ^ _cardLogFilterTurn ^ _cardLogFilterPlayerId.GetHashCode();
        bool dataChanged = newHash != _cachedGroupsHash;
        _cachedGroupsHash = newHash;
        _cachedGroups = groups;

        if (dataChanged)
        {
            // 데이터 변경 시 스크롤 범위 리셋
            _virtualRangeStart = -1;
            _virtualRangeEnd = -1;
        }

        RenderVisibleCardLogRows();
    }

    /// <summary>
    /// 가상 스크롤: 현재 스크롤 위치에서 보이는 행만 렌더링.
    /// </summary>
    private void RenderVisibleCardLogRows()
    {
        if (_cachedGroups.Count == 0)
        {
            _topSpacer.CustomMinimumSize = new Vector2(0, 0);
            _bottomSpacer.CustomMinimumSize = new Vector2(0, 0);
            if (_cardLogRows.GetChildCount() == 0 ||
                _cardLogRows.GetChild(0) is not Label)
            {
                ClearChildren(_cardLogRows);
                var emptyLabel = CreateLabel(L10N.EmptyCardLog, 11, SubTextColor);
                emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
                _cardLogRows.AddChild(emptyLabel);
            }
            return;
        }

        var scroll = _scrollContainers[0];
        if (scroll == null) return;

        float scrollY = (float)scroll.GetVScrollBar().Value;
        float viewHeight = scroll.Size.Y;
        int totalGroups = _cachedGroups.Count;
        float totalHeight = totalGroups * VIRTUAL_ROW_HEIGHT;

        // 보이는 범위 계산 (버퍼 포함)
        int firstVisible = Math.Max(0, (int)(scrollY / VIRTUAL_ROW_HEIGHT) - VIRTUAL_BUFFER_ROWS);
        int lastVisible = Math.Min(totalGroups - 1,
            (int)((scrollY + viewHeight) / VIRTUAL_ROW_HEIGHT) + VIRTUAL_BUFFER_ROWS);

        // 범위가 동일하면 재렌더링 불필요
        if (firstVisible == _virtualRangeStart && lastVisible == _virtualRangeEnd)
            return;

        _virtualRangeStart = firstVisible;
        _virtualRangeEnd = lastVisible;

        // 스페이서 설정
        _topSpacer.CustomMinimumSize = new Vector2(0, firstVisible * VIRTUAL_ROW_HEIGHT);
        _bottomSpacer.CustomMinimumSize = new Vector2(0,
            Math.Max(0, (totalGroups - lastVisible - 1) * VIRTUAL_ROW_HEIGHT));

        // 보이는 행만 렌더링
        ClearChildren(_cardLogRows);

        // 필터 요약 (첫 행에만)
        if (firstVisible == 0 &&
            (_cardLogFilterTurn >= 0 || !string.IsNullOrEmpty(_cardLogFilterPlayerId)))
        {
            int totalDmg = 0, totalBlock = 0, count = 0;
            foreach (var group in _cachedGroups)
            {
                foreach (var evt in group)
                {
                    count++;
                    if (evt.EventType == CombatEventType.DamageDealt || evt.EventType == CombatEventType.PoisonDamage)
                        totalDmg += evt.Damage;
                    else if (evt.EventType == CombatEventType.BlockGained)
                        totalBlock += evt.Damage;
                }
            }
            var summaryParts = new List<string> { L10N.CountItems(count) };
            if (totalDmg > 0) summaryParts.Add(L10N.DamageValue(totalDmg.ToString("N0")));
            if (totalBlock > 0) summaryParts.Add(L10N.BlockValue(totalBlock.ToString("N0")));
            var summaryLabel = CreateLabel(string.Join(" | ", summaryParts), 9, SubTextColor);
            summaryLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _cardLogRows.AddChild(summaryLabel);
        }

        var colorMap = DamageTracker.Instance.ColorMap;
        for (int i = firstVisible; i <= lastVisible && i < totalGroups; i++)
        {
            var group = _cachedGroups[i];
            var color = colorMap.GetColor(group[0].PlayerId);

            if (group.Count > 1 && group[0].EventType == CombatEventType.DamageDealt)
                CreateMultiHitRow(group, color);
            else
                CreateCardEventRow(group[0], color);
        }
    }

    /// <summary>
    /// 같은 턴+플레이어+카드이름의 연속 DamageDealt 이벤트를 묶어서 그룹 리스트로 반환.
    /// 다중타격 카드(칼날 춤, 수리검 등)가 여러 개로 나오는 걸 하나로 합침.
    /// </summary>
    private static List<List<CombatEvent>> GroupConsecutiveHits(List<CombatEvent> events)
    {
        var result = new List<List<CombatEvent>>();
        if (events.Count == 0) return result;

        var currentGroup = new List<CombatEvent> { events[0] };

        for (int i = 1; i < events.Count; i++)
        {
            var prev = events[i - 1];
            var cur = events[i];

            // 같은 턴, 같은 플레이어, 같은 카드, 둘 다 DamageDealt면 그룹에 추가
            bool canGroup = cur.EventType == CombatEventType.DamageDealt
                && prev.EventType == CombatEventType.DamageDealt
                && cur.Turn == prev.Turn
                && cur.PlayerId == prev.PlayerId
                && cur.CardName == prev.CardName;

            if (canGroup)
            {
                currentGroup.Add(cur);
            }
            else
            {
                result.Add(currentGroup);
                currentGroup = new List<CombatEvent> { cur };
            }
        }

        result.Add(currentGroup);
        return result;
    }

    /// <summary>다중타격 그룹을 하나의 요약 행 + 펼침 가능한 상세로 표시.</summary>
    private void CreateMultiHitRow(List<CombatEvent> hits, Color playerColor)
    {
        var first = hits[0];
        int totalDmg = 0;
        bool anyKill = false;
        foreach (var h in hits)
        {
            totalDmg += h.Damage;
            if (h.WasKill) anyKill = true;
        }

        // 그룹 고유 키 (펼침 상태 추적용)
        var groupKey = $"{first.Turn}_{first.PlayerId}_{first.CardName}_{first.TimestampTicks}";
        bool isExpanded = _expandedMultiHits.Contains(groupKey);

        var container = new VBoxContainer();
        container.AddThemeConstantOverride("separation", 0);

        // === 요약 행 (클릭하여 펼치기/접기) ===
        var summaryRow = new HBoxContainer();
        summaryRow.AddThemeConstantOverride("separation", 4);

        // 턴 번호
        var turnLabel = CreateLabel($"[T{first.Turn}]", 10, SubTextColor);
        turnLabel.CustomMinimumSize = new Vector2(32, 0);
        summaryRow.AddChild(turnLabel);

        // 플레이어 이름
        var nameLabel = CreateLabel(first.PlayerName, 10, playerColor);
        nameLabel.CustomMinimumSize = new Vector2(55, 0);
        nameLabel.ClipText = true;
        summaryRow.AddChild(nameLabel);

        // 카드이름 → 대상 (히트수)
        var targetNames = new HashSet<string>();
        foreach (var h in hits)
        {
            if (!string.IsNullOrEmpty(h.TargetName))
                targetNames.Add(h.TargetName);
        }
        var targetText = targetNames.Count <= 2
            ? string.Join(",", targetNames)
            : $"{targetNames.First()} 외 {targetNames.Count - 1}";

        var toggleMark = isExpanded ? "▼" : "▶";
        var infoLabel = CreateLabel($"{toggleMark} {first.CardName} → {targetText} x{hits.Count}", 10, TextColor);
        infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        infoLabel.ClipText = true;
        infoLabel.MouseFilter = Control.MouseFilterEnum.Pass;
        summaryRow.AddChild(infoLabel);

        // 총 데미지
        var killText = anyKill ? L10N.Kill : "";
        var dmgColor = anyKill ? new Color(1f, 0.4f, 0.4f) : TextColor;
        var dmgLabel = CreateLabel($"{totalDmg:N0}{killText}", 10, dmgColor);
        dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dmgLabel.CustomMinimumSize = new Vector2(65, 0);
        summaryRow.AddChild(dmgLabel);

        container.AddChild(summaryRow);

        // === 상세 히트 목록 (펼침 시만 표시) ===
        var detailContainer = new VBoxContainer();
        detailContainer.AddThemeConstantOverride("separation", 0);
        detailContainer.Visible = isExpanded;

        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var hitRow = new HBoxContainer();
            hitRow.AddThemeConstantOverride("separation", 4);

            // 인덱스
            var idxLabel = CreateLabel($"  #{i + 1}", 9, SubTextColor);
            idxLabel.CustomMinimumSize = new Vector2(87, 0);
            hitRow.AddChild(idxLabel);

            // 대상
            var tgtLabel = CreateLabel($"→ {hit.TargetName}", 9, SubTextColor);
            tgtLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            tgtLabel.ClipText = true;
            hitRow.AddChild(tgtLabel);

            // 개별 데미지
            var hitKill = hit.WasKill ? L10N.Kill : "";
            var hitColor = hit.WasKill ? new Color(1f, 0.4f, 0.4f) : SubTextColor;
            var hitDmgLabel = CreateLabel($"{hit.Damage:N0}{hitKill}", 9, hitColor);
            hitDmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
            hitDmgLabel.CustomMinimumSize = new Vector2(65, 0);
            hitRow.AddChild(hitDmgLabel);

            detailContainer.AddChild(hitRow);
        }

        container.AddChild(detailContainer);

        // === 클릭으로 펼침/접기 ===
        var toggleBtn = new Button();
        toggleBtn.Flat = true;
        toggleBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        toggleBtn.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        toggleBtn.Modulate = new Color(1, 1, 1, 0); // 투명 버튼
        toggleBtn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;

        // 요약 행 위에 투명 버튼 오버레이
        // 대신 summaryRow 자체를 버튼처럼 동작시키기 위해 요약 행 클릭 이벤트 사용
        summaryRow.GuiInput += (InputEvent @event) =>
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                if (_expandedMultiHits.Contains(groupKey))
                {
                    _expandedMultiHits.Remove(groupKey);
                    infoLabel.Text = $"▶ {first.CardName} → {targetText} x{hits.Count}";
                    detailContainer.Visible = false;
                }
                else
                {
                    _expandedMultiHits.Add(groupKey);
                    infoLabel.Text = $"▼ {first.CardName} → {targetText} x{hits.Count}";
                    detailContainer.Visible = true;
                }
            }
        };

        // 요약 행 호버 시 게임 내장 카드 툴팁 표시
        AttachCardHoverTip(summaryRow, first.CardName);

        _cardLogRows.AddChild(container);
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
                var killText = evt.WasKill ? L10N.Kill : "";
                var infoLabel = CreateLabel($"{evt.CardName} → {evt.TargetName}", 10, TextColor);
                infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                infoLabel.ClipText = true;
                infoLabel.MouseFilter = Control.MouseFilterEnum.Pass;
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
                infoLabel.MouseFilter = Control.MouseFilterEnum.Pass;
                row.AddChild(infoLabel);

                var blockLabel = CreateLabel($"+{evt.Damage}", 10, BlockColor);
                blockLabel.HorizontalAlignment = HorizontalAlignment.Right;
                blockLabel.CustomMinimumSize = new Vector2(65, 0);
                row.AddChild(blockLabel);
                break;
            }
            case CombatEventType.PoisonDamage:
            {
                var infoLabel = CreateLabel(L10N.PoisonTarget(evt.TargetName), 10, PoisonColor);
                infoLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                infoLabel.ClipText = true;
                infoLabel.MouseFilter = Control.MouseFilterEnum.Pass;
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
                infoLabel.MouseFilter = Control.MouseFilterEnum.Pass;
                row.AddChild(infoLabel);

                // 빈 공간 (데미지/블록 없음)
                var spacer = new Control();
                spacer.CustomMinimumSize = new Vector2(65, 0);
                row.AddChild(spacer);
                break;
            }
        }

        // 행 호버 시 게임 내장 카드 툴팁 표시
        AttachCardHoverTip(row, evt.CardName);

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
            var summaryHeader = CreateLabel(L10N.ReceivedHeader, 10, SubTextColor);
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
                    var deathLabel = CreateLabel(L10N.DeathCount(player.DeathCount), 10, new Color(1f, 0.3f, 0.3f));
                    row.AddChild(deathLabel);
                }

                container.AddChild(row);

                // 받은피해 / 막은피해 / 실제피해
                int unblocked = player.TotalDamageReceived - player.TotalBlockedReceived;
                var detailText = L10N.ReceivedDetail(player.TotalDamageReceived.ToString("N0"), player.TotalBlockedReceived.ToString("N0"), unblocked.ToString("N0"));
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
            var emptyLabel = CreateLabel(L10N.EmptyReceived, 11, SubTextColor);
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
        var deathText = isDeath ? L10N.DeathBang : "";
        string dmgText;
        if (evt.BlockedDamage > 0)
            dmgText = $"{evt.Damage:N0} {L10N.BlockedSuffix(evt.BlockedDamage)}{deathText}";
        else
            dmgText = $"{evt.Damage:N0}{deathText}";

        var dmgLabel = CreateLabel(dmgText, 10, isDeath ? deathColor : TextColor);
        dmgLabel.HorizontalAlignment = HorizontalAlignment.Right;
        dmgLabel.CustomMinimumSize = new Vector2(95, 0);
        row.AddChild(dmgLabel);

        _receivedRows.AddChild(row);
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
            _titleLabel.Text = L10N.Title;
            _needsUpdate = true;
            _needsLogUpdate = true;
        }

        // CanvasLayer 자식은 부모 컨테이너가 없어 자동 크기 조절 안됨
        // Deferred로 실행하여 Visibility 변경 후 최소 크기 재계산
        CallDeferred(nameof(FitPanelToContent));
    }

    /// <summary>패널 크기를 콘텐츠 최소 크기에 맞춤.</summary>
    private void FitPanelToContent()
    {
        if (_isMinimized)
        {
            // 최소화: 헤더만 표시되도록 축소
            _panel.Size = new Vector2(_currentWidth, _panel.GetCombinedMinimumSize().Y);
        }
        else
        {
            // 확장: 원래 크기로 복원
            ApplyResize();
            _panel.Size = _panel.GetCombinedMinimumSize();
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

    // ===== 카드 호버 팁 시스템 =====

    /// <summary>
    /// 게임 내장 호버 팁 시스템을 초기화.
    /// HoverTipFactory.FromCard, NHoverTipSet.CreateAndShow, Clear 메서드를 리플렉션으로 찾음.
    /// </summary>
    private static void InitHoverTipSystem()
    {
        if (_hoverTipSystemChecked) return;
        _hoverTipSystemChecked = true;

        try
        {
            // 1. NHoverTipSet 타입 & CreateAndShow(Control, IHoverTip, alignment)
            _hoverTipSetType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet");
            if (_hoverTipSetType == null)
            {
                ModEntry.LogWarning("[DamageMeter] NHoverTipSet type not found");
                return;
            }

            var iHoverTipType = AccessTools.TypeByName("MegaCrit.Sts2.Core.HoverTips.IHoverTip");

            foreach (var method in _hoverTipSetType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "CreateAndShow")
                {
                    var pars = method.GetParameters();
                    // 단일 IHoverTip 오버로드 선택 (IEnumerable 아님)
                    if (pars.Length == 3 && pars[0].ParameterType == typeof(Control)
                        && iHoverTipType != null && pars[1].ParameterType == iHoverTipType)
                    {
                        _createAndShowMethod = method;
                    }
                }
                else if (method.Name == "Clear" && method.GetParameters().Length == 0)
                {
                    _clearMethod = method;
                }
            }

            // 2. HoverTipFactory.FromCard(CardModel, bool)
            var factoryType = AccessTools.TypeByName("MegaCrit.Sts2.Core.HoverTips.HoverTipFactory");
            if (factoryType != null)
            {
                var cardModelType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.CardModel");
                foreach (var method in factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "FromCard") continue;
                    var pars = method.GetParameters();
                    if (pars.Length == 2 && pars[0].ParameterType == cardModelType
                        && pars[1].ParameterType == typeof(bool))
                    {
                        _fromCardMethod = method;
                        break;
                    }
                }
            }

            // 3. HoverTipAlignment.Right (enum value 1)
            var alignType = AccessTools.TypeByName("MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment");
            if (alignType != null)
            {
                _alignRight = Enum.ToObject(alignType, 1);
            }

            ModEntry.Log($"[DamageMeter] HoverTip init: CreateAndShow={_createAndShowMethod != null}, FromCard={_fromCardMethod != null}, Clear={_clearMethod != null}, Align={_alignRight != null}");
        }
        catch (Exception ex)
        {
            ModEntry.LogError($"[DamageMeter] HoverTip init error: {ex.Message}");
        }
    }

    /// <summary>
    /// 카드 로그 행에 호버 이벤트를 연결.
    /// 마우스 호버 시 게임 내장 카드 툴팁을 표시.
    /// </summary>
    private void AttachCardHoverTip(Control row, string cardName)
    {
        InitHoverTipSystem();
        if (_createAndShowMethod == null || _fromCardMethod == null || _alignRight == null) return;

        // HBoxContainer 기본 MouseFilter는 Ignore → 마우스 이벤트 수신 불가
        // Pass로 변경하여 MouseEntered/MouseExited 이벤트를 받을 수 있게 함
        row.MouseFilter = Control.MouseFilterEnum.Pass;

        row.MouseEntered += () => ShowCardHoverTip(row, cardName);
        row.MouseExited += () => HideCardHoverTip();
    }

    private void ShowCardHoverTip(Control ownerControl, string cardName)
    {
        // 기존 팁 제거
        HideCardHoverTip();

        try
        {
            var cardModel = DamageTracker.Instance.GetCachedCardModel(cardName);
            if (cardModel == null)
            {
                ModEntry.LogDebug($"[DamageMeter] HoverTip: no cached CardModel for '{cardName}'");
                return;
            }

            // HoverTipFactory.FromCard(cardModel, false) → IHoverTip
            var hoverTip = _fromCardMethod!.Invoke(null, new object[] { cardModel, false });
            if (hoverTip == null)
            {
                ModEntry.LogDebug($"[DamageMeter] HoverTip: FromCard returned null for '{cardName}'");
                return;
            }

            // NHoverTipSet.CreateAndShow(ownerControl, hoverTip, HoverTipAlignment.Right)
            var result = _createAndShowMethod!.Invoke(null, new object[] { ownerControl, hoverTip, _alignRight! });
            _activeHoverTipSet = result as Control;
        }
        catch (Exception ex)
        {
            ModEntry.LogDebug($"[DamageMeter] ShowCardHoverTip error for '{cardName}': {ex.Message}");
            if (ex.InnerException != null)
                ModEntry.LogDebug($"  Inner: {ex.InnerException.Message}");
        }
    }

    private void HideCardHoverTip()
    {
        try
        {
            // NHoverTipSet.Clear() 사용 (게임 내장 정리 로직)
            _clearMethod?.Invoke(null, null);
        }
        catch { }
        _activeHoverTipSet = null;
    }
}
