// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only
using Tmds.Ssh;

namespace DingoTail;

internal sealed class RemoteBrowser : Form
{
    private readonly TextBox address = new() { Dock = DockStyle.Fill, AccessibleName = "Remote folder", Text = "/" };
    private readonly TextBox filter = new() { Dock = DockStyle.Fill, AccessibleName = "Filter entries", PlaceholderText = "Filter this folder…" };
    private readonly DataGridView entries = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoGenerateColumns = false, BackgroundColor = Chrome.Log, BorderStyle = BorderStyle.None,
        AccessibleName = "Remote files and folders", EnableHeadersVisualStyles = false,
    };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Browser status" };
    private readonly Button open = Chrome.TextButton("Open log", true);
    private readonly Button back = Chrome.TextButton("Back");
    private readonly Button up = Chrome.TextButton("Up");
    private readonly Func<string, bool, CancellationToken, Task<List<RemoteServer.DirectoryEntry>>> list;
    private readonly CheckBox sudo = new() { Text = "Browse with sudo (read-only)", AutoSize = true };
    private CancellationTokenSource? operation;
    private long generation;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration disconnect;
    private readonly Stack<string> history = new();
    private List<RemoteServer.DirectoryEntry> rows = [];
    private string current = "/";
    private bool loading;
    private bool disposed;
    public string? SelectedPath { get; private set; }

    public RemoteBrowser(string server, Func<string, CancellationToken, Task<List<RemoteServer.DirectoryEntry>>> list, CancellationToken session)
        : this(server, (path, _, token) => list(path, token), session) { }
    public RemoteBrowser(string server, Func<string, bool, CancellationToken, Task<List<RemoteServer.DirectoryEntry>>> list, CancellationToken session)
    {
        this.list = list;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(session);
        Text = $"Browse {server}";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(620, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Chrome.StyleForm(this);
        Chrome.StyleField(address);
        Chrome.StyleField(filter);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        foreach (var height in new[] { 38f, 34f, 0f, 58f, 34f, 40f })
            layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        var go = Chrome.TextButton("Go");
        navigation.Controls.Add(back, 0, 0); navigation.Controls.Add(up, 1, 0);
        navigation.Controls.Add(address, 2, 0); navigation.Controls.Add(go, 3, 0);
        entries.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Chrome.Log, ForeColor = Chrome.Text, SelectionBackColor = Chrome.Hover, SelectionForeColor = Chrome.Text, Padding = new Padding(5) };
        entries.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Chrome.Panel, ForeColor = Chrome.Text, Padding = new Padding(5) };
        entries.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        entries.ColumnHeadersHeight = 32;
        entries.GridColor = Chrome.Edge;
        entries.RowTemplate.Height = 30;
        entries.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        entries.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Kind", Width = 110 });
        entries.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Bytes", Width = 110 });
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = Chrome.TextButton("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([cancel, open]);
        layout.Controls.Add(navigation, 0, 0); var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        search.Controls.Add(new Label { Text = "Filter", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        search.Controls.Add(filter, 1, 0);
        layout.Controls.Add(search, 0, 1); layout.Controls.Add(entries, 0, 2);
        var elevation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        Chrome.StyleField(sudo);
        elevation.Controls.Add(sudo);
        elevation.Controls.Add(new Label { Text = "Browsing only. Enable log-reading sudo separately when opening a file.", AutoSize = true });
        layout.Controls.Add(elevation, 0, 3);
        layout.Controls.Add(feedback, 0, 4); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
        CancelButton = cancel;
        sudo.CheckedChanged += async (_, _) => await NavigateAsync(address.Text, false);
        go.Click += async (_, _) => await NavigateAsync(address.Text);
        up.Click += async (_, _) => await NavigateAsync(ParentPath(current));
        back.Click += async (_, _) => { if (history.TryPeek(out var previous) && await NavigateAsync(previous, false)) history.Pop(); UpdateActions(); };
        address.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await NavigateAsync(address.Text); } };
        entries.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ActivateAsync(); } };
        entries.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ActivateAsync(); };
        entries.SelectionChanged += (_, _) => UpdateActions();
        open.Click += async (_, _) => await ActivateAsync();
        filter.TextChanged += (_, _) => RenderRows();
        Shown += async (_, _) => await NavigateAsync("/", false);
        FormClosed += (_, _) => lifetime.Cancel();
        disconnect = session.Register(() => { if (IsHandleCreated && !IsDisposed) { try { BeginInvoke(() => Close()); } catch (InvalidOperationException) { } } });
        UpdateActions();
    }

    private static string ParentPath(string path) => path.LastIndexOf('/') > 0 ? path[..path.LastIndexOf('/')] : "/";
    private RemoteServer.DirectoryEntry? Selection => entries.CurrentRow?.Tag as RemoteServer.DirectoryEntry;
    private void UpdateActions()
    {
        back.Enabled = !loading && history.Count > 0;
        up.Enabled = !loading && current != "/";
        open.Enabled = !loading && Selection is { Type: UnixFileType.RegularFile or UnixFileType.SymbolicLink or UnixFileType.Directory };
        open.Text = Selection?.IsDirectory == true ? "Open folder" : "Open log";
    }
    private async Task ActivateAsync()
    {
        if (loading || Selection is not { } item) return;
        if (item.IsDirectory) await NavigateAsync(item.Path);
        else if (item.Type is UnixFileType.RegularFile or UnixFileType.SymbolicLink)
        { SelectedPath = item.Path; DialogResult = DialogResult.OK; }
    }
    internal async Task<bool> NavigateAsync(string path, bool remember = true)
    {
        if (lifetime.IsCancellationRequested) return false;
        operation?.Cancel();
        var requestId = ++generation;
        if (!path.StartsWith('/') || path.Contains('\0')) { feedback.Text = "Enter an absolute remote folder, such as /var/log."; return false; }
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); } else if (part != ".") parts.Add(part);
        path = "/" + string.Join('/', parts);
        address.Text = path;
        loading = true; entries.Enabled = false; feedback.Text = $"Loading {path}…"; UpdateActions();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = timeout;
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var result = await list(path, sudo.Checked, timeout.Token);
            if (lifetime.IsCancellationRequested || requestId != generation) return false;
            if (remember && current != path) history.Push(current);
            current = path; address.Text = path; rows = result; RenderRows();
            return true;
        }
        catch (OperationCanceledException) { if (!IsDisposed && requestId == generation) feedback.Text = lifetime.IsCancellationRequested ? "Disconnected. Close this browser and reconnect." : "Folder request timed out. Choose Go to retry."; }
        catch (Exception error) when (error is SftpException or SshException or IOException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed && requestId == generation) feedback.Text = $"Cannot open {path}. {error.Message}"; }
        finally { if (!IsDisposed && requestId == generation) { operation = null; loading = false; entries.Enabled = !lifetime.IsCancellationRequested; UpdateActions(); } }
        return false;
    }
    private void RenderRows()
    {
        if (IsDisposed) return;
        entries.Rows.Clear();
        foreach (var item in rows.Where(item => item.Name.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var kind = item.IsDirectory ? "Folder" : item.Type == UnixFileType.SymbolicLink ? "Symbolic link" : item.Type == UnixFileType.RegularFile ? "File" : "Special file";
            var index = entries.Rows.Add(item.Name, kind, item.IsDirectory ? "" : item.Size.ToString("N0"));
            entries.Rows[index].Tag = item;
        }
        feedback.Text = rows.Count == 0 ? "This folder is empty. Use Up or enter another path." : $"{entries.Rows.Count:N0} entries · Double-click a folder to browse or a file to open" + (rows.Count >= 10_000 ? " · First 10,000 entries only" : "");
        UpdateActions();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; disconnect.Dispose(); lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}







