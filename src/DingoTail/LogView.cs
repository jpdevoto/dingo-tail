// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

namespace DingoTail;

internal sealed class LogView : Control
{
    private readonly VScrollBar vertical = new() { Dock = DockStyle.Right, TabStop = false, Visible = false };
    private readonly HScrollBar horizontal = new() { Dock = DockStyle.Bottom, TabStop = false, Visible = false };
    private SourceSession? source;
    private long firstVisible;
    private long revision = -1;
    private int lineHeight;
    private int characterWidth;
    private bool updating;
    private LogBuffer.Row[] visibleRows = [];

    private HighlightPalette? palette;
    private readonly Dictionary<long, HighlightPalette.RowStyle?> rowStyles = [];
    private bool styling;

    public event Action? FollowChanged;
    public event Action? SelectionChanged;

    public void SetHighlightPalette(HighlightPalette value)
    {
        palette = value;
        rowStyles.Clear();
        QueueStyles();
        Invalidate();
    }

    private async void QueueStyles()
    {
        var current = palette;
        var session = source;
        if (styling || current is null || session is null || !IsHandleCreated || IsDisposed || !Visible)
            return;

        var visibleIds = visibleRows.Select(row => row.Id).ToHashSet();
        foreach (var id in rowStyles.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
            rowStyles.Remove(id);
        var pending = visibleRows.Where(row => !rowStyles.ContainsKey(row.Id)).ToArray();
        if (pending.Length == 0)
            return;

        styling = true;
        try
        {
            var computed = await Task.Run(() => pending.Select(row => (row.Id, Style: current.Match(row.Text))).ToArray());
            if (!IsDisposed && palette == current && source == session)
            {
                var latestIds = visibleRows.Select(row => row.Id).ToHashSet();
                foreach (var (id, style) in computed)
                    if (latestIds.Contains(id))
                        rowStyles[id] = style;
                Invalidate();
            }
        }
        finally
        {
            styling = false;
        }
        if (!IsDisposed)
            QueueStyles();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        QueueStyles();
    }

    public LogView()
    {
        AccessibleName = "Log output";
        AccessibleRole = AccessibleRole.Pane;
        BackColor = Chrome.Log;
        ForeColor = Chrome.Text;
        Font = new Font("Consolas", 10.5f);
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(vertical);
        Controls.Add(horizontal);
        vertical.ValueChanged += (_, _) =>
        {
            if (!updating && source is not null)
                ScrollIndex(vertical.Value);
        };
        horizontal.ValueChanged += (_, _) =>
        {
            if (!updating && source is not null)
            {
                source.HorizontalOffset = horizontal.Value;
                Invalidate();
            }
        };
    }

    public void Bind(SourceSession? session)
    {
        if (source == session)
            return;
        source = session;
        rowStyles.Clear();
        RefreshRows(force: true);
    }

    public void RefreshRows(bool force = false)
    {
        var state = source?.Log.GetState() ?? default;
        var filtered = source?.Filter.Snapshot() ?? default;
        var matches = filtered.Rows ?? [];
        if (!force && revision == filtered.Revision)
            return;
        revision = filtered.Revision;
        lineHeight = Math.Max(1, TextRenderer.MeasureText("Mg", Font).Height);
        characterWidth = Math.Max(1, TextRenderer.MeasureText("0", Font, Size.Empty, TextFormatFlags.NoPadding).Width);
        var page = Math.Max(1, (ClientSize.Height - horizontal.Height) / lineHeight);
        var latest = Math.Max(0, matches.Length - page);
        var anchorIndex = source is null ? 0 : Array.FindIndex(matches, row => row.Id >= source.Anchor);
        if (anchorIndex < 0) anchorIndex = Math.Max(0, matches.Length - 1);
        var offset = source?.Follow != false ? latest : Math.Min(anchorIndex, latest);
        var rows = matches.Skip(offset).Take(page).ToArray();
        firstVisible = rows.Length > 0 ? rows[0].Id : 0;        var columns = Math.Max(1, (ClientSize.Width - vertical.Width) / characterWidth);
        var width = Math.Max(columns, rows.Length == 0 ? 0 : rows.Max(row => row.Text.Length));
        var horizontalOffset = Math.Clamp(source?.HorizontalOffset ?? 0, 0, width - columns);
        updating = true;
        try
        {
            vertical.Visible = matches.Length > page;
            horizontal.Visible = width > columns;
            vertical.LargeChange = page;
            vertical.Maximum = Math.Max(page, matches.Length) - 1;
            vertical.Value = offset;
            horizontal.LargeChange = columns;
            horizontal.Maximum = width - 1;
            horizontal.Value = horizontalOffset;
        }
        finally
        {
            updating = false;
        }

        if (force || !rows.AsSpan().SequenceEqual(visibleRows))
        {
            visibleRows = rows;
            QueueStyles();
            Invalidate();
        }
    }

    private void ScrollIndex(int index)
    {
        var rows = source?.Filter.Snapshot().Rows ?? [];
        if (rows.Length > 0) ScrollTo(rows[Math.Clamp(index, 0, rows.Length - 1)].Id);
    }

    private void ScrollTo(long id)
    {
        if (source is null)
            return;
        var wasFollowing = source.Follow;
        source.Follow = false;
        source.Anchor = id;
        if (wasFollowing)
            FollowChanged?.Invoke();
        RefreshRows(force: true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RefreshRows(force: true);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollIndex(vertical.Value - e.Delta / 120 * 3);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var row = e.Y / Math.Max(1, lineHeight);
        if (source is not null && row < visibleRows.Length)
        {
            source.Selected = visibleRows[row].Id;
            SelectionChanged?.Invoke();
            Invalidate();
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (source is null)
            return;
        var page = Math.Max(1, vertical.LargeChange - 1);
        switch (e.KeyCode)
        {
            case Keys.Up: ScrollIndex(vertical.Value - 1); break;
            case Keys.Down: ScrollIndex(vertical.Value + 1); break;
            case Keys.PageUp: ScrollIndex(vertical.Value - page); break;
            case Keys.PageDown: ScrollIndex(vertical.Value + page); break;
            case Keys.Home: ScrollIndex(0); break;
            case Keys.End:
                source.Follow = true;
                FollowChanged?.Invoke();
                RefreshRows(force: true);
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (source is not null && visibleRows.Length == 0)
            TextRenderer.DrawText(e.Graphics, "No matching retained lines", Font, new Point(8, 8), Chrome.Muted);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var width = Math.Max(0, ClientSize.Width - vertical.Width);
        var columns = width / Math.Max(1, characterWidth) + 2;
        var highContrast = SystemInformation.HighContrast;
        for (var index = 0; index < visibleRows.Length; index++)
        {
            var row = visibleRows[index];
            var bounds = new Rectangle(0, index * lineHeight, width, lineHeight);
            if (!e.ClipRectangle.IntersectsWith(bounds))
                continue;
            var selected = source?.Selected == row.Id;
            var style = rowStyles.GetValueOrDefault(row.Id);
            if (selected)
                e.Graphics.FillRectangle(SystemBrushes.Highlight, bounds);
            else if (style is { } match && !highContrast)
            {
                using var brush = new SolidBrush(match.Background);
                e.Graphics.FillRectangle(brush, bounds);
            }
            var start = Math.Min(horizontal.Value, row.Text.Length);
            if (start < row.Text.Length && char.IsLowSurrogate(row.Text[start]) && start > 0)
                start--;
            var end = Math.Min(start + columns, row.Text.Length);
            if (end < row.Text.Length && char.IsLowSurrogate(row.Text[end]))
                end++;
            TextRenderer.DrawText(e.Graphics, row.Text[start..end], Font, bounds,
                selected ? SystemColors.HighlightText : highContrast ? ForeColor : style?.Foreground ?? ForeColor, flags);
        }
    }
}






