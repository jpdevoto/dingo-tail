// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;

namespace DingoTail;

internal sealed partial class WorkspaceForm : Form
{
    private static Color Surface => Chrome.Frame;
    private static Color TextColor => Chrome.Text;
    private static Color Accent => Chrome.Accent;

    private readonly WorkspaceState state;
    private Button sourceToggle = null!;
    private readonly Dictionary<string, SourceSession> sessions = [];
    private readonly SplitContainer split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private readonly ListBox sources = new() { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly TableLayoutPanel grid = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel tabs = new() { Dock = DockStyle.Top, Height = 39, AutoScroll = true, WrapContents = false };
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolTip chromeTips = new();
    private readonly List<Chrome.IconButton> layoutButtons = [];
    private readonly Pane[] panes = new Pane[4];
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 100 };
    private readonly HashSet<SourceSession> observed = [];
    private int wakePending;
    private bool updating;
    private bool closing;
    private bool saveEnabled = true;

    public WorkspaceForm()
    {
        Text = "DingoTail";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1200, 780);
        MinimumSize = new Size(800, 500);
        Chrome.StyleForm(this);
        string? loadError = null;
        try
        {
            state = WorkspaceState.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            state = new();
            saveEnabled = false;
            loadError = "Workspace could not be loaded. " + error.Message;
        }
        foreach (var profile in state.Servers)
            AddServerProfile(profile);
        foreach (var source in state.Sources)
            sessions.Add(source.Id, CreateSession(source));

        var top = Bar(DockStyle.Top, 46, Chrome.Toolbar);
        top.Controls.Add(IconAction("Toggle workspace sidebar", 0xe5d2, () => split.Panel1Collapsed = !split.Panel1Collapsed));
        using var wordmark = typeof(WorkspaceForm).Assembly.GetManifestResourceStream("DingoTail.Wordmark.png");
        if (wordmark is not null)
        {
            using var image = Image.FromStream(wordmark);
            top.Controls.Add(new PictureBox
            {
                Image = new Bitmap(image), SizeMode = PictureBoxSizeMode.Zoom, Width = 135, Height = 32,
                AccessibleName = "DingoTail"
            });
        }
        serverMenuTrigger = IconAction("SSH servers", 0xe875, ShowServers);
        top.Controls.Add(serverMenuTrigger);
        top.Controls.Add(IconAction("Appearance & highlighting", 0xe8b8, ShowHighlighting));
        top.Controls.Add(IconAction("About DingoTail", 0xe88e, ShowAbout));

        var bottom = new StatusStrip { BackColor = Chrome.Toolbar, ForeColor = Chrome.Muted };
        Chrome.StyleStrip(bottom);
        bottom.Items.Add(status);
        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(bottom);
        split.SplitterDistance = 250;
        split.Panel1MinSize = 230;
        var local = Bar(DockStyle.Top, 42, Chrome.Panel);
        sourceToggle = Button(state.LocalExpanded ? "Hide sources" : "Show sources", ToggleLocal);
        local.Controls.Add(sourceToggle);
        local.Controls.Add(IconAction("Open local files", 0xe2c8, ChooseFiles));
        local.Controls.Add(IconAction("Enter local path", 0xe3c9, EnterPath));
        sources.BackColor = Chrome.Panel;
        sources.BorderStyle = BorderStyle.None;
        sources.ForeColor = TextColor;
        sources.AccessibleName = "Workspace sources";
        sources.ItemHeight = 25;
        sources.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || sources.Items[e.Index] is not SourceChoice choice)
                return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Chrome.Hover : Chrome.Panel);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, choice.Label, sources.Font,
                Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(8), 0), selected ? Accent : TextColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0)
                e.DrawFocusRectangle();
        };
        sources.MouseMove += (_, e) =>
        {
            var index = sources.IndexFromPoint(e.Location);
            var label = index >= 0 && sources.Items[index] is SourceChoice choice ? SourceLocation(choice.Source) : "";
            if (chromeTips.GetToolTip(sources) != label)
                chromeTips.SetToolTip(sources, label);
        };

        sources.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && sources.SelectedItem is SourceChoice choice)
                Assign(state.ActivePane, choice.Source.Id);
        };
        InitializeServerList();
        split.Panel1.Controls.Add(sources);
        split.Panel1.Controls.Add(serverList);
        RefreshServerIndicators();
        split.Panel1.Controls.Add(local);
        var layouts = Bar(DockStyle.Top, 40);
        foreach (var (count, label, glyph) in new[] { (1, "Single pane", 0xe3c6), (2, "Two columns", 0xe8ec), (4, "Four panes", 0xe9b0) })
        {
            var button = IconAction(label, glyph, () => SetLayout(count));
            layoutButtons.Add(button);
            layouts.Controls.Add(button);
        }
        split.Panel2.Controls.Add(grid);
        split.Panel2.Controls.Add(tabs);
        split.Panel2.Controls.Add(layouts);
        for (var index = 0; index < panes.Length; index++)
            panes[index] = new(this, index);

        refresh.Tick += (_, _) =>
        {
            refresh.Stop();
            Interlocked.Exchange(ref wakePending, 0);
            RefreshServerIndicators();
            RefreshVisible();
        };
        Resize += (_, _) => ObserveVisible();
        FormClosing += (_, _) =>
        {
            closing = true;
            ObserveVisible();
            refresh.Dispose();
            StopRemoteConnections();
            foreach (var session in sessions.Values)
            { session.Reader.Stop(); session.Filter.Dispose(); }
            Save();
        };
        RebuildGrid();
        UpdateSources();
        Shown += async (_, _) =>
        {
            ObserveVisible();
            await LoadHighlightPaletteAsync();
        };
        Disposed += (_, _) =>
        {
            serverMenuStrip?.Dispose();
            foreach (var dot in statusDots.Values)
                dot.Dispose();
            chromeTips.Dispose();
            foreach (var pane in panes)
                pane.Dispose();
        };
        status.Text = loadError ?? "Ready";
    }

    private void ObserveVisible()
    {
        var visible = new HashSet<SourceSession>();
        if (!closing && WindowState != FormWindowState.Minimized)
            for (var index = 0; index < state.Layout; index++)
                if (panes[index]?.Session is { } session)
                    visible.Add(session);
        foreach (var session in observed.Except(visible))
        { session.Log.Changed -= ScheduleRefresh; session.Filter.Changed -= ScheduleRefresh; }
        foreach (var session in visible.Except(observed))
        { session.Log.Changed += ScheduleRefresh; session.Filter.Changed += ScheduleRefresh; }
        observed.Clear();
        observed.UnionWith(visible);
        RefreshVisible();
    }

    private void ScheduleRefresh()
    {
        if (!IsHandleCreated || Interlocked.Exchange(ref wakePending, 1) != 0)
            return;
        try
        {
            BeginInvoke(() =>
            {
                if (!closing)
                    refresh.Start();
            });
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref wakePending, 0);
        }
    }

    private void RefreshVisible()
    {
        if (closing || WindowState == FormWindowState.Minimized)
            return;
        for (var index = 0; index < state.Layout; index++)
            panes[index]?.RefreshSession();
    }

    private void RebuildGrid()
    {
        layoutButtons[0].Selected = state.Layout == 1;
        layoutButtons[1].Selected = state.Layout == 2;
        layoutButtons[2].Selected = state.Layout == 4;
        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();
        grid.ColumnCount = state.Layout == 1 ? 1 : 2;
        grid.RowCount = state.Layout == 4 ? 2 : 1;
        for (var column = 0; column < grid.ColumnCount; column++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / grid.ColumnCount));
        for (var row = 0; row < grid.RowCount; row++)
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / grid.RowCount));
        for (var index = 0; index < state.Layout; index++)
            grid.Controls.Add(panes[index], index % grid.ColumnCount, index / grid.ColumnCount);
        grid.ResumeLayout();
    }

    private void SetLayout(int layout)
    {
        state.Layout = layout;
        state.ActivePane = Math.Min(state.ActivePane, layout - 1);
        RebuildGrid();
        UpdateSources();
        Save();
    }

    private void UpdateSources()
    {
        updating = true;
        try
        {
            sources.Visible = state.LocalExpanded;
            sources.Items.Clear();
            foreach (var source in state.Sources)
                sources.Items.Add(new SourceChoice(source, SourceLabel(source)));
            var active = state.Panes[state.ActivePane];
            sources.SelectedIndex = active is not null ? state.Sources.IndexOf(sessions[active].Source) : -1;
            while (tabs.Controls.Count > 0)
                tabs.Controls[0].Dispose();
            tabs.Visible = state.Sources.Count > 0;
            foreach (var source in state.Sources)
            {
                var tab = Button(SourceLabel(source), () => Assign(state.ActivePane, source.Id));
                tab.AccessibleName = SourceLocation(source);
                chromeTips.SetToolTip(tab, tab.AccessibleName);
                tab.MaximumSize = new Size(200, 0);
                tab.ForeColor = source.Id == active ? Accent : TextColor;
                tabs.Controls.Add(tab);
            }
            for (var index = 0; index < panes.Length; index++)
                panes[index].UpdateSources(state.Sources,
                    state.Panes[index] is { } id ? sessions[id] : null, index == state.ActivePane);
        }
        finally
        {
            updating = false;
        }
        ObserveVisible();
    }

    private void ActivatePane(int index)
    {
        if (updating || state.ActivePane == index)
            return;
        state.ActivePane = index;
        UpdateSources();
        Save();
    }

    private void Assign(int index, string? id)
    {
        if (updating)
            return;
        var existing = id is null ? -1 : Array.IndexOf(state.Panes, id);
        if (existing >= 0 && existing < state.Layout)
        {
            state.ActivePane = existing;
        }
        else
        {
            if (existing >= 0)
                state.Panes[existing] = null;
            state.Panes[index] = id;
            state.ActivePane = index;
        }
        UpdateSources();
        Save();
    }

    private void ToggleLocal()
    {
        state.LocalExpanded = !state.LocalExpanded;
        sourceToggle.Text = state.LocalExpanded ? "Hide sources" : "Show sources";
        sources.Visible = state.LocalExpanded;
        Save();
    }

    private void ChooseFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Open local logs" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            foreach (var path in dialog.FileNames)
                OpenSource(path);
    }

    private void EnterPath()
    {
        var input = new TextBox();
        using var dialog = RemoteDialog("Open local log", "Open", ("Absolute Windows path", input));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenSource(input.Text.Trim());
    }

    private void OpenSource(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            status.Text = "Enter an absolute Windows file path.";
            return;
        }
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            status.Text = error.Message;
            return;
        }
        var existing = state.Sources.Find(source => source.ServerId is null && string.Equals(source.Path, path, StringComparison.Ordinal));
        if (existing is not null)
        {
            Assign(state.ActivePane, existing.Id);
            return;
        }

        var source = new LogSource(Guid.NewGuid().ToString("N"), path);
        var session = new SourceSession(source);
        state.Sources.Add(source);
        sessions.Add(source.Id, session);
        Assign(state.ActivePane, source.Id);
        session.Reader.Start(state.InitialLines);
    }

    private void CloseSource(SourceSession session)
    {
        session.Reader.Stop();
        var id = session.Source.Id;
        session.Filter.Dispose();
        sessions.Remove(id);
        state.Sources.Remove(session.Source);
        for (var index = 0; index < state.Panes.Length; index++)
            if (state.Panes[index] == id)
                state.Panes[index] = null;
        UpdateSources();
        Save();
    }

    private bool Save()
    {
        if (!saveEnabled)
            return false;
        try
        {
            state.Save();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = "Could not save workspace. " + error.Message;
            return false;
        }
    }

    private static FlowLayoutPanel Bar(DockStyle dock, int height, Color? color = null) =>
        new() { Dock = dock, Height = height, Padding = new Padding(6, 4, 6, 4), BackColor = color ?? Surface };

    private static Button Button(string title, Action action)
    {
        var button = Chrome.TextButton(title);

        button.Click += (_, _) => action();
        return button;
    }

    private Chrome.IconButton IconAction(string title, int glyph, Action action) => new(title, glyph, chromeTips, action);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { panes[state.ActivePane].ShowFilters(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private sealed class Pane : Panel
    {
        private readonly WorkspaceForm owner;
        private readonly int index;
        private readonly DarkPicker picker = new() { Dock = DockStyle.Top };
        private readonly Label header = new() { Dock = DockStyle.Top, Height = 24, AutoEllipsis = true };
        private readonly Label details = new() { Dock = DockStyle.Bottom, Height = 23, AutoEllipsis = true };
        private readonly LogView view = new() { Dock = DockStyle.Fill };
        private readonly ToolTip tooltip = new();
        private readonly Button start;
        private readonly Button stop;
        private readonly Chrome.IconButton follow;
        private readonly Chrome.IconButton clear;
        private readonly Chrome.IconButton copy;
        private readonly Chrome.IconButton export;
        private readonly Chrome.IconButton more;
        private readonly ContextMenuStrip moreMenu = Chrome.Menu();
        private readonly FlowLayoutPanel actions;
        private readonly Chrome.IconButton search;
        private readonly TableLayoutPanel filters = new() { Dock = DockStyle.Top, Height = 126, ColumnCount = 2 };
        private readonly Panel filterHost = new() { Dock = DockStyle.Top, Height = 126, Visible = false, AutoScroll = true };
        private readonly TextBox include = new() { Dock = DockStyle.Fill, AccessibleName = "Find / include" };
        private readonly TextBox exclude = new() { Dock = DockStyle.Fill, AccessibleName = "Exclude" };
        private readonly CheckBox regex = new() { Text = "Regular expression", AutoSize = true };
        private readonly Label filterError = new() { Dock = DockStyle.Fill, ForeColor = Color.Salmon, AutoEllipsis = true };
        private bool binding;
        public void ShowFilters() { filterHost.Visible = true; ResizeFilters(); include.Focus(); }
        private void ResizeFilters()
        {
            filterHost.Height = LogicalToDeviceUnits(126);
            AutoScrollMinSize = new Size(0, LogicalToDeviceUnits(filterHost.Visible ? 340 : 180));
        }
        private void ChangeFilter()
        {
            if (!binding) Session?.Filter.Set(new(include.Text, exclude.Text, regex.Checked));
        }
        public void SetLogFont(int size)
        {
            if (Math.Abs(view.Font.SizeInPoints - size * .75f) < .01f) return;
            var old = view.Font;
            view.Font = new Font("Consolas", size * .75f, GraphicsUnit.Point);
            old.Dispose();
            view.RefreshRows(true);
        }

        public SourceSession? Session { get; private set; }

        public Pane(WorkspaceForm owner, int index)
        {
            this.owner = owner;
            this.index = index;
            Dock = DockStyle.Fill;
            AutoScroll = true;
            AutoScrollMinSize = new Size(0, LogicalToDeviceUnits(180));
            Padding = new Padding(6);
            BackColor = Chrome.Panel;
            header.Padding = new Padding(6, 4, 6, 0);
            details.Padding = new Padding(6, 3, 6, 0);
            details.ForeColor = Chrome.Muted;

            picker.AccessibleName = $"Pane {index + 1} source";
            picker.SelectedIndexChanged += (_, _) =>
            {
                if (!binding)
                    owner.Assign(index, (picker.SelectedItem as SourceChoice)?.Source.Id);
            };
            view.Enter += (_, _) => owner.ActivatePane(index);
            header.Click += (_, _) => owner.ActivatePane(index);
            actions = Bar(DockStyle.Top, 40, Chrome.Panel);
            actions.WrapContents = false;

            start = owner.IconAction("Start", 0xe037, () => Session?.Reader.Start(owner.state.InitialLines));
            stop = owner.IconAction("Stop", 0xe047, () => Session?.Reader.Stop());
            follow = owner.IconAction("Resume live follow", 0xe258, () =>
            {
                if (Session is { } session)
                {
                    session.Follow = true;
                    view.RefreshRows(force: true);
                    RefreshSession();
                }
            });
            view.SelectionChanged += RefreshSession;
            view.FollowChanged += () => follow.Selected = Session?.Follow == true;
            clear = owner.IconAction("Clear retained lines", 0xe92e, () => Session?.Log.Clear());
            copy = owner.IconAction("Copy selected row", 0xf08a, Copy);
            export = owner.IconAction("Export matching retained lines", 0xe2c4, Export);
            more = owner.IconAction("More log actions", 0xeae1, ShowMore);
            moreMenu.Items.Add("Clear retained lines", null, (_, _) => Session?.Log.Clear());
            moreMenu.Items.Add("Copy selected row", null, (_, _) => Copy());
            moreMenu.Items.Add("Export matching retained lines", null, (_, _) => Export());
            search = owner.IconAction("Search & filters", 0xe8b6, () => { filterHost.Visible = !filterHost.Visible; ResizeFilters(); if (filterHost.Visible) include.Focus(); });
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Chrome.StyleField(include); Chrome.StyleField(exclude); Chrome.StyleField(regex);
            filters.Controls.Add(new Label { Text = "Find / include", AutoSize = true }, 0, 0);
            filters.Controls.Add(include, 1, 0);
            filters.Controls.Add(new Label { Text = "Exclude", AutoSize = true }, 0, 1);
            filters.Controls.Add(exclude, 1, 1);
            var reset = Chrome.TextButton("Reset filters");
            reset.Click += (_, _) => { include.Clear(); exclude.Clear(); regex.Checked = false; };
            filters.Controls.Add(reset, 0, 2); filters.Controls.Add(regex, 1, 2);
            filters.Controls.Add(filterError, 0, 3); filters.SetColumnSpan(filterError, 2);
            include.TextChanged += (_, _) => ChangeFilter(); exclude.TextChanged += (_, _) => ChangeFilter(); regex.CheckedChanged += (_, _) => ChangeFilter();
            actions.Controls.AddRange([start, stop, follow, search, clear, copy, export, more,
                owner.IconAction("Close source", 0xe5cd, () => { if (Session is { } session) owner.CloseSource(session); })]);
            actions.SizeChanged += (_, _) => UpdateActionLayout();
            Disposed += (_, _) =>
            {
                moreMenu.Dispose();
                tooltip.Dispose();
            };
            UpdateActionLayout();
            Controls.Add(view);
            Controls.Add(details);
            filterHost.Controls.Add(filters);
            SizeChanged += (_, _) => ResizeFilters();
            Controls.Add(filterHost);
            Controls.Add(actions);
            Controls.Add(header);
            Controls.Add(picker);
        }

        private void UpdateActionLayout()
        {
            var compact = actions.Width < LogicalToDeviceUnits(260);
            clear.Visible = copy.Visible = export.Visible = !compact;
            more.Visible = compact;
        }

        private void ShowMore()
        {
            moreMenu.Show(more, new Point(0, more.Height));
        }

        public void SetHighlightPalette(HighlightPalette palette) => view.SetHighlightPalette(palette);

        public void UpdateSources(List<LogSource> sources, SourceSession? session, bool active)
        {
            binding = true;
            try
            {
                picker.Items.Clear();
                picker.Items.Add("Choose a log source");
                foreach (var source in sources)
                    picker.Items.Add(new SourceChoice(source, owner.SourceLabel(source)));
                picker.SelectedIndex = session is null ? 0 : sources.IndexOf(session.Source) + 1;
                picker.Invalidate();
            }
            finally
            {
                binding = false;
            }
            Session = session;
            filters.Enabled = session is not null;
            if (session is null) filterHost.Visible = false;
            ResizeFilters();
            binding = true;
            var draft = session?.Filter.Snapshot().Draft ?? new FilterSpec();
            include.Text = draft.Include; exclude.Text = draft.Exclude; regex.Checked = draft.Regex;
            binding = false;
            SetLogFont(owner.state.LogFontSize);
            header.ForeColor = active ? Accent : TextColor;
            header.Text = session is null ? $"Pane {index + 1}" : owner.SourceLocation(session.Source);
            tooltip.SetToolTip(header, header.Text);
            tooltip.SetToolTip(picker, header.Text);
            actions.Enabled = session is not null;
            view.Bind(session);
            RefreshSession();
        }

        public void RefreshSession()
        {
            if (Session is not { } session)
            {
                details.Text = "";
                return;
            }
            var snapshot = session.Log.GetState();
            var filtered = session.Filter.Snapshot();
            search.Selected = filtered.Draft.Active;
            filterError.Text = filtered.Error;
            tooltip.SetToolTip(filterError, filtered.Error);
            copy.Enabled = session.Selected is { } selected && filtered.Rows.Any(row => row.Id == selected);
            var label = $"{snapshot.Status}  |  {filtered.Rows.Length:N0} matching / {snapshot.Count:N0} retained";
            if (snapshot.Diagnostic.Length > 0)
                label += "  |  " + snapshot.Diagnostic;
            if (details.Text != label)
            {
                details.Text = label;
                tooltip.SetToolTip(details, snapshot.Diagnostic);
            }
            start.Enabled = snapshot.Status == "Stopped";
            stop.Enabled = !start.Enabled;
            follow.Selected = session.Follow;
            view.RefreshRows();
        }

        private void Copy()
        {
            if (Session is not { Selected: long id } session)
                return;
            var selected = session.Filter.Snapshot().Rows.Where(row => row.Id == id).ToArray();
            if (selected.Length > 0 && selected[0].Id == id)
            {
                try
                {
                    Clipboard.SetDataObject(selected[0].Text, true);
                }
                catch (System.Runtime.InteropServices.ExternalException error)
                {
                    owner.status.Text = error.Message;
                }
            }
        }

        private async void Export()
        {
            if (Session is not { } session)
                return;
            using var dialog = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", FileName = "log.txt" };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            var rows = session.Filter.Snapshot().Rows;
            try
            {
                await File.WriteAllLinesAsync(dialog.FileName, rows.Select(row => row.Text));
                owner.status.Text = $"Exported {rows.Length:N0} rows";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                owner.status.Text = error.Message;
            }
        }
    }
}


















