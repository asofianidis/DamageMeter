using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FishNet;
using FishNet.Object;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NevarisDamageMeter;

// Pure meter state/UI helper. Every entry point is gated on world readiness.
internal static class MeterController
{
    private const float PanelWidth = 360f;
    private const float PanelHeight = 244f;
    private const float MinimumUserScale = 0.55f;
    private const float MaximumUserScale = 1.25f;
    private const float MinimumAutoFitScale = 0.35f;
    private const int DetailVisibleEntries = 8;
    private static readonly Dictionary<int, MeterRow> Rows = new();
    private static readonly Dictionary<string, string> DamageSourceNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<int> DisplayedPlayerIds = new();
    private static GameObject _canvasObject;
    private static GameObject _panelObject;
    private static Canvas _canvas;
    private static RectTransform _panelRect;
    private static RectTransform _dragHandleRect;
    private static RectTransform _resizeHandleRect;
    private static Image _background;
    private static TextMeshProUGUI _titleText;
    private static TextMeshProUGUI _dragHintText;
    private static TextMeshProUGUI _text;
    private static PlayerController _localPlayer;
    private static bool _visible;
    private static bool _worldReady;
    private static bool _dragging;
    private static bool _resizing;
    private static bool _gameStyleApplied;
    private static Vector2 _dragStartMouse;
    private static Vector2 _dragStartPosition;
    private static Vector2 _resizeStartMouse;
    private static float _resizeStartScale;
    private static float _preferredUiScale = 1f;
    private static int _lastScreenWidth = -1;
    private static int _lastScreenHeight = -1;
    private static double _startedAt = -1d;
    private static double _lastDamageAt = -1d;
    private static float _nextUiRefresh;
    private static int _lastTickFrame = -1;
    private static bool _loggedFirstNetworkDamageCapture;
    private static bool _loggedUnresolvedAttacker;
    private static bool _loggedNonPlayerAttacker;
    private static bool _loggedOutOfScopeAttacker;
    private static bool _loggedInvalidDamage;
    private static bool _loggedFirstAcceptedDamage;
    private static int? _selectedPlayerId;
    private static int _detailScrollOffset;

    internal static void Initialize() => _visible = Plugin.Settings.StartVisible.Value;

    internal static void ResetEncounter() => Reset();

    internal static void ApplyConfiguredScale()
    {
        _preferredUiScale = Mathf.Clamp(
            Plugin.Settings.UiScale.Value, MinimumUserScale, MaximumUserScale);
        if (_panelRect is null)
            return;
        ApplyScaleForScreen();
        ClampPanelToScreen();
    }

    internal static void ResetLayout()
    {
        Plugin.Settings.PositionX.Value = (float)Plugin.Settings.PositionX.DefaultValue;
        Plugin.Settings.PositionY.Value = (float)Plugin.Settings.PositionY.DefaultValue;
        Plugin.Settings.UiScale.Value = (float)Plugin.Settings.UiScale.DefaultValue;
        ApplyConfiguredScale();
        if (_panelRect is null)
            return;
        _panelRect.anchoredPosition = new Vector2(
            Plugin.Settings.PositionX.Value, Plugin.Settings.PositionY.Value);
        ClampPanelToScreen();
    }

    internal static void Shutdown()
    {
        _worldReady = false;
        if (_canvasObject is not null)
            Object.Destroy(_canvasObject);
        _canvasObject = null;
        _panelObject = null;
        _canvas = null;
        _panelRect = null;
        _dragHandleRect = null;
        _resizeHandleRect = null;
        _background = null;
        _titleText = null;
        _dragHintText = null;
        _text = null;
        _localPlayer = null;
        _dragging = false;
        _resizing = false;
        _gameStyleApplied = false;
        _selectedPlayerId = null;
        _detailScrollOffset = 0;
        DisplayedPlayerIds.Clear();
        Rows.Clear();
    }

    internal static void EnterWorld(PlayerController localPlayer)
    {
        if (_worldReady)
            return;

        _localPlayer = localPlayer;
        _worldReady = true;
        ResetCaptureDiagnostics();
        Reset();
        EnsureUi();
        _panelObject?.SetActive(_visible && !IsMenuOpen());
    }

    internal static void LeaveWorld()
    {
        if (!_worldReady && _canvasObject is null)
            return;

        _worldReady = false;
        if (_canvasObject is not null)
            Object.Destroy(_canvasObject);
        _canvasObject = null;
        _panelObject = null;
        _canvas = null;
        _panelRect = null;
        _dragHandleRect = null;
        _resizeHandleRect = null;
        _background = null;
        _titleText = null;
        _dragHintText = null;
        _text = null;
        _localPlayer = null;
        _dragging = false;
        _resizing = false;
        _gameStyleApplied = false;
        Reset();
    }

    internal static void Tick()
    {
        if (!_worldReady || _localPlayer is null)
            return;

        if (_lastTickFrame == Time.frameCount)
            return;
        _lastTickFrame = Time.frameCount;

        EnsureUi();
        bool menuOpen = IsMenuOpen();
        if (!menuOpen && Plugin.Settings.ToggleHotkey.Value.IsDown())
        {
            _visible = !_visible;
        }
        if (!menuOpen && Plugin.Settings.ResetHotkey.Value.IsDown())
            Reset();

        bool shouldDisplay = _visible && !menuOpen;
        if (_panelObject is not null && _panelObject.activeSelf != shouldDisplay)
            _panelObject.SetActive(shouldDisplay);

        if (shouldDisplay)
            HandlePointerInteractions();
        else
        {
            _dragging = false;
            _resizing = false;
        }

        if (!_gameStyleApplied)
            _gameStyleApplied = TryApplyGameStyle();

        if (_lastScreenWidth != Screen.width || _lastScreenHeight != Screen.height)
            ApplyScaleForScreen();
        ClampPanelToScreen();

        if (_text is not null && Time.unscaledTime >= _nextUiRefresh)
        {
            _nextUiRefresh = Time.unscaledTime + 0.2f;
            PruneOutOfScopeRows();
            _text.text = BuildDisplayText();
        }
    }

    internal static void Record(Damage damage)
    {
        if (!_worldReady || _localPlayer is null)
            return;

        int attackerId = damage.AttackerId;
        BaseUnitController attacker = attackerId == _localPlayer.ObjectId
            ? _localPlayer
            : ResolveUnit(attackerId);
        if (attacker is null)
        {
            if (!_loggedUnresolvedAttacker)
            {
                _loggedUnresolvedAttacker = true;
                Plugin.Warning($"A visible damage event was received, but attacker object {attackerId} was not present in the client spawn table.");
            }
            return;
        }

        BaseUnitController damageDealer = attacker;
        bool ownedEntityDamage = damage.IsSummon || damage.IsClone;
        if (ownedEntityDamage)
        {
            if (!Plugin.Settings.IncludeSummons.Value)
                return;
            attacker = ResolveSummonOwner(attacker) ?? attacker;
        }

        PlayerController playerAttacker = GetPlayerController(attacker);
        if (playerAttacker is null)
        {
            if (!_loggedNonPlayerAttacker)
            {
                _loggedNonPlayerAttacker = true;
                Plugin.Info("Ignored a visible damage event whose resolved attacker was not a player or player-owned summon.");
            }
            return;
        }
        if (!IsLocalOrPartyMember(playerAttacker))
        {
            if (!_loggedOutOfScopeAttacker)
            {
                _loggedOutOfScopeAttacker = true;
                Plugin.Info("Ignored a visible damage event from a player outside the local party.");
            }
            return;
        }

        // Do not inspect or aggregate the damage payload until its source has
        // passed the local/party scope check above.
        if (damage.Value <= 0 || !damage.IsHit)
        {
            if (!_loggedInvalidDamage)
            {
                _loggedInvalidDamage = true;
                Plugin.Info("Ignored a self/party damage event because it was not a positive hit.");
            }
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        float resetDelay = Plugin.Settings.NewEncounterAfter.Value;
        if (resetDelay > 0f && _lastDamageAt >= 0d && now - _lastDamageAt >= resetDelay)
            Reset();
        if (_startedAt < 0d)
            _startedAt = now;
        _lastDamageAt = now;

        int id = playerAttacker.ObjectId;
        if (!Rows.TryGetValue(id, out MeterRow row))
        {
            row = new MeterRow(SafeDisplayName(playerAttacker), SafePlayerId(playerAttacker));
            Rows.Add(id, row);
        }

        row.Name = SafeDisplayName(playerAttacker, row.Name);
        row.Damage += damage.Value;
        row.Hits += Math.Max(1, damage.Hits);
        if (damage.Hit == HitResult.Critical)
            row.Crits++;
        if (ownedEntityDamage)
            row.SummonDamage += damage.Value;

        DamageOrigin origin = damage.IsClone
            ? DamageOrigin.Clone
            : damage.IsSummon ? DamageOrigin.Summon : DamageOrigin.Player;
        string sourceName = origin switch
        {
            DamageOrigin.Clone => "Clones",
            DamageOrigin.Summon => SafeSummonName(damageDealer),
            _ => "Player"
        };
        string sourceKey = origin == DamageOrigin.Summon
            ? $"summon:{sourceName}"
            : origin.ToString();
        row.RecordSource(sourceKey, sourceName, origin, damage);

        if (!_loggedFirstAcceptedDamage)
        {
            _loggedFirstAcceptedDamage = true;
            Plugin.Info("First self/party outgoing damage event captured.");
        }
    }

    internal static void NoteNetworkDamageCapture()
    {
        if (!_worldReady || _loggedFirstNetworkDamageCapture)
            return;

        _loggedFirstNetworkDamageCapture = true;
        Plugin.Info("First network damage payload reached the capture hook.");
    }

    private static PlayerController GetPlayerController(BaseUnitController unit)
    {
        try { return unit.gameObject.GetComponent<PlayerController>(); }
        catch { return null; }
    }

    private static bool IsLocalOrPartyMember(PlayerController player)
    {
        PlayerController local = _localPlayer;
        if (local is null)
            return false;

        try
        {
            // Self is always tracked. Every other player must pass the game's
            // own current-party membership check. Fail closed on missing or
            // transitioning party state so world bystanders are never added.
            if (local.ObjectId == player.ObjectId)
                return true;
            if (!local.HasParty || local.CurrentParty is null)
                return false;

            string playerId = SafePlayerId(player);
            if (string.IsNullOrWhiteSpace(playerId))
                return false;

            return local.CurrentParty.HasMember(playerId)
                && local.IsInMyParty(player);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneOutOfScopeRows()
    {
        if (_localPlayer is null || Rows.Count == 0)
            return;

        List<int> remove = null;
        foreach (KeyValuePair<int, MeterRow> entry in Rows)
        {
            if (IsTrackedIdentity(entry.Key, entry.Value.PlayerId))
                continue;

            remove ??= new List<int>();
            remove.Add(entry.Key);
        }

        if (remove is null)
            return;
        foreach (int objectId in remove)
        {
            Rows.Remove(objectId);
            if (_selectedPlayerId == objectId)
            {
                _selectedPlayerId = null;
                _detailScrollOffset = 0;
            }
        }
    }

    private static bool IsTrackedIdentity(int objectId, string playerId)
    {
        try
        {
            if (_localPlayer.ObjectId == objectId)
                return true;
            if (!_localPlayer.HasParty || _localPlayer.CurrentParty is null)
                return false;
            if (string.IsNullOrWhiteSpace(playerId))
                return false;
            return _localPlayer.CurrentParty.HasMember(playerId);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureUi()
    {
        if (!_worldReady || _canvasObject is not null)
            return;

        _canvasObject = new GameObject("NevarisDamageMeter.Canvas");
        Object.DontDestroyOnLoad(_canvasObject);
        _canvas = _canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32000;
        CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasObject.AddComponent<GraphicRaycaster>();

        _panelObject = new GameObject("Panel");
        _panelObject.transform.SetParent(_canvasObject.transform, false);
        _panelRect = _panelObject.AddComponent<RectTransform>();
        _panelRect.anchorMin = new Vector2(0f, 1f);
        _panelRect.anchorMax = new Vector2(0f, 1f);
        _panelRect.pivot = new Vector2(0f, 1f);
        _panelRect.anchoredPosition = new Vector2(
            Plugin.Settings.PositionX.Value, Plugin.Settings.PositionY.Value);
        _panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        _preferredUiScale = Mathf.Clamp(
            Plugin.Settings.UiScale.Value, MinimumUserScale, MaximumUserScale);
        _background = _panelObject.AddComponent<Image>();
        _background.color = new Color(0.035f, 0.045f, 0.06f, 0.94f);
        Mask panelMask = _panelObject.AddComponent<Mask>();
        panelMask.showMaskGraphic = true;
        Outline outline = _panelObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.68f, 0.54f, 0.28f, 0.7f);
        outline.effectDistance = new Vector2(1f, -1f);

        GameObject headerObject = new("DragHandle");
        headerObject.transform.SetParent(_panelObject.transform, false);
        _dragHandleRect = headerObject.AddComponent<RectTransform>();
        _dragHandleRect.anchorMin = new Vector2(0f, 1f);
        _dragHandleRect.anchorMax = new Vector2(1f, 1f);
        _dragHandleRect.pivot = new Vector2(0.5f, 1f);
        _dragHandleRect.offsetMin = new Vector2(0f, -37f);
        _dragHandleRect.offsetMax = Vector2.zero;
        Image headerBackground = headerObject.AddComponent<Image>();
        headerBackground.color = new Color(0.055f, 0.07f, 0.08f, 0.96f);

        GameObject accentObject = new("AccentRule");
        accentObject.transform.SetParent(_panelObject.transform, false);
        RectTransform accentRect = accentObject.AddComponent<RectTransform>();
        accentRect.anchorMin = new Vector2(0f, 1f);
        accentRect.anchorMax = new Vector2(1f, 1f);
        accentRect.pivot = new Vector2(0.5f, 1f);
        accentRect.offsetMin = new Vector2(0f, -39f);
        accentRect.offsetMax = new Vector2(0f, -37f);
        Image accent = accentObject.AddComponent<Image>();
        accent.color = new Color(0.73f, 0.58f, 0.3f, 0.9f);

        _titleText = CreateText("Title", headerObject.transform);
        RectTransform titleRect = _titleText.rectTransform;
        titleRect.anchorMin = Vector2.zero;
        titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = new Vector2(13f, 5f);
        titleRect.offsetMax = new Vector2(-58f, -4f);
        _titleText.fontSize = 14f;
        _titleText.fontStyle = FontStyles.Bold;
        _titleText.alignment = TextAlignmentOptions.MidlineLeft;
        _titleText.color = new Color(0.91f, 0.82f, 0.62f, 1f);
        _titleText.text = "DAMAGE REPORT";

        _dragHintText = CreateText("DragHint", headerObject.transform);
        RectTransform hintRect = _dragHintText.rectTransform;
        hintRect.anchorMin = Vector2.zero;
        hintRect.anchorMax = Vector2.one;
        hintRect.offsetMin = new Vector2(284f, 5f);
        hintRect.offsetMax = new Vector2(-13f, -4f);
        _dragHintText.fontSize = 9f;
        _dragHintText.alignment = TextAlignmentOptions.MidlineRight;
        _dragHintText.color = new Color(0.63f, 0.68f, 0.69f, 1f);
        _dragHintText.text = "DRAG";

        GameObject textObject = new("Text");
        textObject.transform.SetParent(_panelObject.transform, false);
        RectTransform textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(13f, 10f);
        textRect.offsetMax = new Vector2(-13f, -49f);
        _text = textObject.AddComponent<TextMeshProUGUI>();
        _text.fontSize = 11.75f;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.color = new Color(0.88f, 0.91f, 0.91f, 1f);
        _text.richText = true;
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.raycastTarget = false;
        _text.lineSpacing = 2.5f;
        _text.text = BuildDisplayText();

        GameObject resizeObject = new("ResizeHandle");
        resizeObject.transform.SetParent(_panelObject.transform, false);
        _resizeHandleRect = resizeObject.AddComponent<RectTransform>();
        _resizeHandleRect.anchorMin = new Vector2(1f, 0f);
        _resizeHandleRect.anchorMax = new Vector2(1f, 0f);
        _resizeHandleRect.pivot = new Vector2(1f, 0f);
        _resizeHandleRect.anchoredPosition = new Vector2(-3f, 3f);
        _resizeHandleRect.sizeDelta = new Vector2(28f, 28f);
        Image resizeHitArea = resizeObject.AddComponent<Image>();
        resizeHitArea.color = new Color(0.73f, 0.58f, 0.3f, 0.04f);
        CreateResizeGripLine(resizeObject.transform, 12f, new Vector2(-7f, 7f));
        CreateResizeGripLine(resizeObject.transform, 8f, new Vector2(-5f, 5f));
        CreateResizeGripLine(resizeObject.transform, 4f, new Vector2(-3f, 3f));

        _gameStyleApplied = TryApplyGameStyle();
        ApplyScaleForScreen();
        ClampPanelToScreen();
        _panelObject.SetActive(_visible && !IsMenuOpen());
    }

    private static string BuildDisplayText()
    {
        long total = Rows.Values.Sum(row => row.Damage);
        double elapsed = _startedAt < 0d ? 0d : Math.Max(0.1d, _lastDamageAt - _startedAt);

        if (total == 0)
        {
            SetHeader("DAMAGE REPORT", "DRAG");
            DisplayedPlayerIds.Clear();
            return "<color=#E8D19B><b>WAITING FOR COMBAT</b></color>\n"
                + "<size=10><color=#9AA8AA>Your damage and party damage will appear here.</color></size>";
        }

        if (_selectedPlayerId.HasValue
            && Rows.TryGetValue(_selectedPlayerId.Value, out MeterRow selected))
            return BuildDetailDisplayText(selected, total);

        _selectedPlayerId = null;
        _detailScrollOffset = 0;
        SetHeader("DAMAGE REPORT", "DRAG");
        DisplayedPlayerIds.Clear();

        var lines = new List<string>
        {
            $"<size=10><color=#9AA8AA>TOTAL</color></size>  <size=18><b>{Format(total)}</b></size>"
                + $"   <size=10><color=#9AA8AA>{elapsed:0.0}s encounter</color></size>",
            "",
            "<size=9><color=#829092>    SELECT PLAYER<pos=150>DAMAGE<pos=218>DPS<pos=286>SHARE</color></size>"
        };

        int rank = 1;
        int maxRows = Math.Clamp(Plugin.Settings.MaxRows.Value, 1, 20);
        foreach (KeyValuePair<int, MeterRow> entry in Rows
            .OrderByDescending(entry => entry.Value.Damage)
            .Take(maxRows))
        {
            MeterRow row = entry.Value;
            DisplayedPlayerIds.Add(entry.Key);
            double share = total > 0 ? (double)row.Damage / total : 0d;
            string name = FormatPlayerName(row.Name);
            lines.Add($"<color=#D6B979>{rank,2}</color>  {name}"
                + $"<pos=150><b>{Format(row.Damage)}</b>"
                + $"<pos=218>{Format((long)(row.Damage / elapsed))}/s"
                + $"<pos=286>{share:P0}");
            rank++;
        }
        return string.Join("\n", lines);
    }

    private static string BuildDetailDisplayText(MeterRow row, long encounterTotal)
    {
        SetHeader($"{FormatLabel(row.Name, 18)} · DETAILS", "BACK");
        DisplayedPlayerIds.Clear();

        double encounterShare = encounterTotal > 0
            ? (double)row.Damage / encounterTotal
            : 0d;
        var lines = new List<string>
        {
            $"<size=10><color=#9AA8AA>TOTAL</color></size>  <size=18><b>{Format(row.Damage)}</b></size>"
                + $"   <size=10><color=#9AA8AA>{encounterShare:P0} of encounter</color></size>",
            "",
            "<size=9><color=#829092>SKILL<pos=190>HITS<pos=242>DAMAGE<pos=310>SHARE</color></size>"
        };

        List<string> entries = BuildDetailEntries(row);
        int visibleCount = entries.Count > DetailVisibleEntries
            ? DetailVisibleEntries
            : entries.Count;
        int maxOffset = Math.Max(0, entries.Count - visibleCount);
        _detailScrollOffset = Math.Clamp(_detailScrollOffset, 0, maxOffset);
        lines.AddRange(entries.Skip(_detailScrollOffset).Take(visibleCount));

        if (entries.Count > visibleCount)
        {
            int first = _detailScrollOffset + 1;
            int last = Math.Min(entries.Count, _detailScrollOffset + visibleCount);
            lines.Add($"<size=9><color=#829092>MOUSE WHEEL   {first}–{last} OF {entries.Count}</color></size>");
        }

        return string.Join("\n", lines);
    }

    private static List<string> BuildDetailEntries(MeterRow row)
    {
        var entries = new List<string>();
        IEnumerable<SourceRow> sources = row.Sources.Values
            .OrderBy(source => source.Origin)
            .ThenByDescending(source => source.Damage)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase);

        foreach (SourceRow source in sources)
        {
            double sourceShare = row.Damage > 0
                ? (double)source.Damage / row.Damage
                : 0d;
            string sourceLabel = source.Origin switch
            {
                DamageOrigin.Clone => "CLONES",
                DamageOrigin.Summon => $"SUMMON · {FormatLabel(source.Name, 13)}",
                _ => "PLAYER"
            };
            entries.Add($"<size=10><color=#D6B979><b>{sourceLabel}</b></color>"
                + $"<pos=242><b>{Format(source.Damage)}</b><pos=310>{sourceShare:P0}</size>");

            foreach (SkillRow skill in source.Skills.Values
                .OrderByDescending(skill => skill.Damage)
                .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase))
            {
                double skillShare = row.Damage > 0
                    ? (double)skill.Damage / row.Damage
                    : 0d;
                entries.Add($"  {FormatLabel(skill.Name, 21)}"
                    + $"<pos=190>{skill.Hits}"
                    + $"<pos=242><b>{Format(skill.Damage)}</b>"
                    + $"<pos=310>{skillShare:P0}");
            }
        }

        return entries;
    }

    private static void SetHeader(string title, string hint)
    {
        if (_titleText is not null)
            _titleText.text = title;
        if (_dragHintText is not null)
            _dragHintText.text = hint;
    }

    private static string FormatPlayerName(string value)
    {
        const int maxLength = 12;
        const int prefixLength = maxLength - 3;
        string name = string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        if (name.Length > maxLength)
            name = name[..prefixLength] + "...";

        // Player names are data, not TextMesh Pro markup.
        return name.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string FormatLabel(string value, int maxLength)
    {
        string label = string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
        if (label.Length > maxLength)
            label = label[..Math.Max(1, maxLength - 3)] + "...";
        return EscapeMarkup(label);
    }

    private static string EscapeMarkup(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static TextMeshProUGUI CreateText(string name, Transform parent)
    {
        GameObject obj = new(name);
        obj.transform.SetParent(parent, false);
        TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        text.richText = true;
        return text;
    }

    private static void CreateResizeGripLine(Transform parent, float width, Vector2 position)
    {
        GameObject lineObject = new($"GripLine{width:0}");
        lineObject.transform.SetParent(parent, false);
        RectTransform lineRect = lineObject.AddComponent<RectTransform>();
        lineRect.anchorMin = new Vector2(1f, 0f);
        lineRect.anchorMax = new Vector2(1f, 0f);
        lineRect.pivot = new Vector2(1f, 0.5f);
        lineRect.anchoredPosition = position;
        lineRect.sizeDelta = new Vector2(width, 1.5f);
        lineRect.localEulerAngles = new Vector3(0f, 0f, -45f);
        Image line = lineObject.AddComponent<Image>();
        line.color = new Color(0.82f, 0.69f, 0.42f, 0.9f);
        line.raycastTarget = false;
    }

    private static bool TryApplyGameStyle()
    {
        if (_background is null || _text is null)
            return false;

        try
        {
            UIGame game = App.UI?.Game;
            UIChat chat = game?.Chat ?? UIChat.Instance;
            if (game is null || chat is null)
                return false;

            Sprite panelSprite = chat.BackgroundSprite;
            if (panelSprite is not null)
            {
                _background.sprite = panelSprite;
                _background.type = Image.Type.Sliced;
                float ppu = chat.BackgroundPpu;
                if (ppu > 0f)
                    _background.pixelsPerUnitMultiplier = ppu;
            }

            Color panelColor = chat.BackgroundColor;
            panelColor.a = Mathf.Max(panelColor.a, 0.92f);
            _background.color = panelColor;

            TMP_Text source = chat.gameObject.GetComponentInChildren<TMP_Text>(true)
                ?? game.gameObject.GetComponentInChildren<TMP_Text>(true);
            if (source is not null && source.font is not null)
            {
                _titleText.font = source.font;
                _dragHintText.font = source.font;
                _text.font = source.font;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMenuOpen()
    {
        try
        {
            UIManager ui = App.UI;
            UIGame game = ui?.Game;
            if (ui is null || game is null)
                return true;
            if (!ui.IsInGame || !ui.IsShowingGameScreen || ui.IsLoading)
                return true;
            if (!game.gameObject.activeInHierarchy || UIPopup.HasPopup)
                return true;
            return game.IsFocused;
        }
        catch
        {
            return true;
        }
    }

    private static void HandlePointerInteractions()
    {
        if (_panelRect is null || _dragHandleRect is null
            || _resizeHandleRect is null || _canvas is null)
            return;

        Vector2 mouse = Input.mousePosition;
        if (_selectedPlayerId.HasValue
            && RectTransformUtility.RectangleContainsScreenPoint(_panelRect, mouse, null))
        {
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                _detailScrollOffset += wheel < 0f ? 3 : -3;
                _detailScrollOffset = Math.Max(0, _detailScrollOffset);
                RefreshTextNow();
            }
            if (Input.GetMouseButtonDown(1))
            {
                ReturnToSummary();
                return;
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(_resizeHandleRect, mouse, null))
            {
                _resizing = true;
                _dragging = false;
                _resizeStartMouse = mouse;
                _resizeStartScale = _preferredUiScale;
            }
            else if (_selectedPlayerId.HasValue
                && RectTransformUtility.RectangleContainsScreenPoint(
                    _dragHintText.rectTransform, mouse, null))
            {
                ReturnToSummary();
                return;
            }
            else if (RectTransformUtility.RectangleContainsScreenPoint(_dragHandleRect, mouse, null))
            {
                _dragging = true;
                _resizing = false;
                _dragStartMouse = mouse;
                _dragStartPosition = _panelRect.anchoredPosition;
            }
            else if (!_selectedPlayerId.HasValue && TrySelectPlayerAt(mouse))
            {
                return;
            }
        }

        if (_dragging && Input.GetMouseButton(0))
        {
            float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
            _panelRect.anchoredPosition = _dragStartPosition + (mouse - _dragStartMouse) / scale;
            ClampPanelToScreen();
        }

        if (_resizing && Input.GetMouseButton(0))
        {
            Vector2 delta = mouse - _resizeStartMouse;
            float canvasScale = Mathf.Max(0.01f, _canvas.scaleFactor);
            float scaleDelta = (delta.x - delta.y) / (300f * canvasScale);
            _preferredUiScale = Mathf.Clamp(
                _resizeStartScale + scaleDelta, MinimumUserScale, MaximumUserScale);
            ApplyScaleForScreen();
            ClampPanelToScreen();
        }

        if (!Input.GetMouseButtonUp(0))
            return;

        if (_dragging)
        {
            _dragging = false;
            Plugin.Settings.PositionX.Value = _panelRect.anchoredPosition.x;
            Plugin.Settings.PositionY.Value = _panelRect.anchoredPosition.y;
        }
        if (_resizing)
        {
            _resizing = false;
            Plugin.Settings.UiScale.Value = _preferredUiScale;
        }
    }

    private static bool TrySelectPlayerAt(Vector2 screenPoint)
    {
        if (_text is null || DisplayedPlayerIds.Count == 0
            || !RectTransformUtility.RectangleContainsScreenPoint(
                _text.rectTransform, screenPoint, null))
            return false;

        _text.ForceMeshUpdate();
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _text.rectTransform, screenPoint, null, out Vector2 localPoint))
            return false;

        const int firstPlayerLine = 3;
        int availableLines = _text.textInfo?.lineCount ?? 0;
        for (int index = 0; index < DisplayedPlayerIds.Count; index++)
        {
            int lineIndex = firstPlayerLine + index;
            if (lineIndex >= availableLines)
                break;

            TMP_LineInfo line = _text.textInfo.lineInfo[lineIndex];
            if (localPoint.y > line.ascender || localPoint.y < line.descender)
                continue;

            _selectedPlayerId = DisplayedPlayerIds[index];
            _detailScrollOffset = 0;
            RefreshTextNow();
            return true;
        }
        return false;
    }

    private static void ReturnToSummary()
    {
        _selectedPlayerId = null;
        _detailScrollOffset = 0;
        RefreshTextNow();
    }

    private static void RefreshTextNow()
    {
        if (_text is null)
            return;
        _text.text = BuildDisplayText();
        _text.ForceMeshUpdate();
        _nextUiRefresh = Time.unscaledTime + 0.2f;
    }

    private static void ApplyScaleForScreen()
    {
        if (_panelRect is null || _canvas is null)
            return;

        float canvasScale = Mathf.Max(0.01f, _canvas.scaleFactor);
        float availableWidth = Mathf.Max(1f, Screen.width / canvasScale - 16f);
        float availableHeight = Mathf.Max(1f, Screen.height / canvasScale - 16f);
        float fitScale = Mathf.Min(availableWidth / PanelWidth, availableHeight / PanelHeight);
        float effectiveScale = Mathf.Min(_preferredUiScale, fitScale);
        effectiveScale = Mathf.Clamp(effectiveScale, MinimumAutoFitScale, MaximumUserScale);
        _panelRect.localScale = new Vector3(effectiveScale, effectiveScale, 1f);
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;
    }

    private static void ClampPanelToScreen()
    {
        if (_panelRect is null || _canvas is null)
            return;

        float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
        float width = Screen.width / scale;
        float height = Screen.height / scale;
        Vector2 size = _panelRect.sizeDelta * _panelRect.localScale.x;
        Vector2 position = _panelRect.anchoredPosition;
        position.x = Mathf.Clamp(position.x, 8f, Mathf.Max(8f, width - size.x - 8f));
        position.y = Mathf.Clamp(position.y, -Mathf.Max(8f, height - size.y - 8f), -8f);
        _panelRect.anchoredPosition = position;
    }

    private static BaseUnitController ResolveUnit(int objectId)
    {
        if (objectId < 0 || !InstanceFinder.IsClientStarted)
            return null;
        var objects = InstanceFinder.ClientManager?.Objects?.Spawned;
        if (objects is null || !objects.TryGetValue(objectId, out NetworkObject networkObject) || networkObject is null)
            return null;
        return networkObject.GetComponent<BaseUnitController>();
    }

    private static void ResetCaptureDiagnostics()
    {
        _loggedFirstNetworkDamageCapture = false;
        _loggedUnresolvedAttacker = false;
        _loggedNonPlayerAttacker = false;
        _loggedOutOfScopeAttacker = false;
        _loggedInvalidDamage = false;
        _loggedFirstAcceptedDamage = false;
    }

    private static BaseUnitController ResolveSummonOwner(BaseUnitController unit)
    {
        BaseUnitController current = unit;
        for (int i = 0; i < 3; i++)
        {
            BaseUnitController owner = current.Summoning?.Summoner;
            if (owner is null || owner == current)
                break;
            current = owner;
        }
        return current;
    }

    private static string SafeSummonName(BaseUnitController unit)
    {
        try
        {
            string summonId = unit?.Summoning?.SummonId;
            SummonConfig config = string.IsNullOrWhiteSpace(summonId)
                ? null
                : App.ServerRuntime?.GetSummon(summonId);
            if (config is not null && !string.IsNullOrWhiteSpace(config.DisplayName))
                return Localize(config.DisplayName);
        }
        catch
        {
            // Fall back to the networked unit name below.
        }
        return SafeDisplayName(unit, "Summon");
    }

    private static string ResolveDamageSourceName(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return "Basic Attack";
        if (DamageSourceNames.TryGetValue(sourceId, out string cached))
            return cached;

        string displayName = null;
        try
        {
            BaseConfig config = App.ServerRuntime?.GetSkill(sourceId);
            config ??= App.ServerRuntime?.GetStatus(sourceId);
            if (config is not null && !string.IsNullOrWhiteSpace(config.DisplayName))
                displayName = Localize(config.DisplayName);
        }
        catch
        {
            // Preserve the source id as a readable fallback.
        }

        displayName = string.IsNullOrWhiteSpace(displayName)
            ? HumanizeIdentifier(sourceId)
            : displayName;
        DamageSourceNames[sourceId] = displayName;
        return displayName;
    }

    private static string Localize(string value)
    {
        try { return Loc.Get(value, value); }
        catch { return value; }
    }

    private static string HumanizeIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        char previous = '\0';
        foreach (char current in value)
        {
            if (current == '_' || current == '-')
            {
                if (result.Length > 0 && result[^1] != ' ')
                    result.Append(' ');
                previous = current;
                continue;
            }
            if (result.Length > 0 && char.IsUpper(current)
                && (char.IsLower(previous) || char.IsDigit(previous)))
                result.Append(' ');
            result.Append(current);
            previous = current;
        }
        return result.ToString();
    }

    private static string SafeDisplayName(BaseUnitController unit, string fallback = "Unknown")
    {
        try
        {
            string value = unit.DisplayName;
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch { return fallback; }
    }

    private static string SafePlayerId(PlayerController player)
    {
        try { return player.PlayerId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void Reset()
    {
        Rows.Clear();
        DisplayedPlayerIds.Clear();
        _selectedPlayerId = null;
        _detailScrollOffset = 0;
        _startedAt = -1d;
        _lastDamageAt = -1d;
        if (_text is not null)
            _text.text = BuildDisplayText();
    }

    private static string Format(long value)
    {
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000d:0.##}b";
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.##}m";
        if (value >= 1_000) return $"{value / 1_000d:0.##}k";
        return value.ToString();
    }

    private sealed class MeterRow
    {
        internal MeterRow(string name, string playerId)
        {
            Name = name;
            PlayerId = playerId;
        }
        internal string Name;
        internal readonly string PlayerId;
        internal long Damage;
        internal int Hits;
        internal int Crits;
        internal long SummonDamage;
        internal readonly Dictionary<string, SourceRow> Sources =
            new(StringComparer.OrdinalIgnoreCase);

        internal void RecordSource(
            string sourceKey, string sourceName, DamageOrigin origin, Damage damage)
        {
            if (!Sources.TryGetValue(sourceKey, out SourceRow source))
            {
                source = new SourceRow(sourceName, origin);
                Sources.Add(sourceKey, source);
            }

            source.Damage += damage.Value;
            string skillId = damage.DamageSourceId ?? string.Empty;
            if (!source.Skills.TryGetValue(skillId, out SkillRow skill))
            {
                skill = new SkillRow(ResolveDamageSourceName(skillId));
                source.Skills.Add(skillId, skill);
            }
            skill.Damage += damage.Value;
            skill.Hits += Math.Max(1, damage.Hits);
            if (damage.Hit == HitResult.Critical)
                skill.Crits++;
        }
    }

    private enum DamageOrigin
    {
        Player,
        Summon,
        Clone
    }

    private sealed class SourceRow
    {
        internal SourceRow(string name, DamageOrigin origin)
        {
            Name = name;
            Origin = origin;
        }
        internal readonly string Name;
        internal readonly DamageOrigin Origin;
        internal long Damage;
        internal readonly Dictionary<string, SkillRow> Skills =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SkillRow
    {
        internal SkillRow(string name) => Name = name;
        internal readonly string Name;
        internal long Damage;
        internal int Hits;
        internal int Crits;
    }
}
