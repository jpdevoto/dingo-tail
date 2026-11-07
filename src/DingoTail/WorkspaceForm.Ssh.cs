// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using Tmds.Ssh;

namespace DingoTail;

internal sealed partial class WorkspaceForm
{
    private readonly Dictionary<string, RemoteServer> servers = [];
    private readonly HashSet<string> credentialPrompts = [];
    private readonly CancellationTokenSource remoteShutdown = new();
    private ContextMenuStrip? serverMenuStrip;
    private Control? serverMenuTrigger;

    private sealed class AuthenticationChoices : FlowLayoutPanel
    {
        private readonly RadioButton password = new() { Text = "Password", AutoSize = true };
        private readonly RadioButton key = new() { Text = "Public key", AutoSize = true };

        public AuthenticationChoices(bool preferKey)
        {
            WrapContents = false;
            Controls.AddRange([password, key]);
            key.Checked = preferKey;
            password.Checked = !preferKey;
            key.CheckedChanged += (_, _) => MethodChanged?.Invoke();
        }

        public bool UseKey => key.Checked;
        public event Action? MethodChanged;
    }
    private readonly ListBox serverList = new()
    {
        Dock = DockStyle.Top, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None, AccessibleName = "SSH server statuses", ItemHeight = 25,
    };
    private readonly Dictionary<ServerConnectionState, Bitmap> statusDots = [];

    internal static Color ConnectionColor(ServerConnectionState state) => state switch
    {
        ServerConnectionState.Connected => Color.FromArgb(95, 214, 145),
        ServerConnectionState.Connecting or ServerConnectionState.AwaitingTrust or ServerConnectionState.Reconnecting => Color.FromArgb(255, 190, 90),
        ServerConnectionState.Failed => Color.FromArgb(255, 113, 123),
        _ => Color.FromArgb(155, 163, 177),
    };

    private static string ConnectionLabel(ServerConnectionState state) =>
        state == ServerConnectionState.AwaitingTrust ? "Awaiting trust" : state.ToString();

    private sealed record ServerChoice(RemoteServer Server, ServerConnectionState State)
    {
        public override string ToString() => $"{Server.Profile.Name} · {ConnectionLabel(State)}";
    }

    private void AddServerProfile(ServerProfile profile)
    {
        var server = new RemoteServer(profile);
        server.Changed += ScheduleRefresh;
        servers.Add(profile.Id, server);
    }

    private static string ConnectionDetail(RemoteServer server)
    {
        var (state, failure) = server.Status;
        return $"{server.Profile.User}@{server.Profile.Host}:{server.Profile.Port} · {ConnectionLabel(state)}" +
            (failure is null ? "" : Environment.NewLine + failure);
    }

    private void InitializeServerList()
    {
        serverList.BackColor = Chrome.Panel;
        serverList.ForeColor = Chrome.Text;
        serverList.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || serverList.Items[e.Index] is not ServerChoice choice)
                return;
            using var background = new SolidBrush(e.State.HasFlag(DrawItemState.Selected) ? Chrome.Hover : Chrome.Panel);
            e.Graphics.FillRectangle(background, e.Bounds);
            var size = LogicalToDeviceUnits(9);
            using var dot = new SolidBrush(ConnectionColor(choice.State));
            e.Graphics.FillEllipse(dot, LogicalToDeviceUnits(8), e.Bounds.Y + (e.Bounds.Height - size) / 2, size, size);
            var left = LogicalToDeviceUnits(24);
            var bounds = new Rectangle(left, e.Bounds.Y, Math.Max(0, e.Bounds.Width - left - 4), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, choice.ToString(), serverList.Font, bounds, Chrome.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (e.State.HasFlag(DrawItemState.Focus))
                e.DrawFocusRectangle();
        };
        serverList.MouseMove += (_, e) =>
        {
            var index = serverList.IndexFromPoint(e.Location);
            var detail = index >= 0 && serverList.Items[index] is ServerChoice choice ? ConnectionDetail(choice.Server) : "";
            if (chromeTips.GetToolTip(serverList) != detail)
                chromeTips.SetToolTip(serverList, detail);
        };
        serverList.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;
            var index = serverList.IndexFromPoint(e.Location);
            if (index < 0)
                return;
            serverList.SelectedIndex = index;
            ShowServersAt(serverList);
        };
        serverList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10))
            {
                e.SuppressKeyPress = true;
                ShowServersAt(serverList);
            }
        };
    }

    private void RefreshServerIndicators()
    {
        var rows = servers.Values.Select(server => new ServerChoice(server, server.Status.State)).ToArray();
        if (!serverList.Items.Cast<ServerChoice>().SequenceEqual(rows))
        {
            var selected = (serverList.SelectedItem as ServerChoice)?.Server;
            serverList.Items.Clear();
            serverList.Items.AddRange(rows);
            serverList.SelectedIndex = Array.FindIndex(rows, row => row.Server == selected);
            serverList.Height = LogicalToDeviceUnits(25 * Math.Min(rows.Length, 4));
            serverList.Visible = rows.Length > 0;
        }

        if (serverMenuStrip is { Visible: true } menu)
            foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
                if (item.Tag is RemoteServer server)
                    UpdateServerMenuItem(item, server);
    }

    private void UpdateServerMenuItem(ToolStripMenuItem item, RemoteServer server)
    {
        var connection = server.Status.State;
        item.Text = $"{server.Profile.Name} · {ConnectionLabel(connection)}";
        item.ToolTipText = ConnectionDetail(server);
        if (!statusDots.TryGetValue(connection, out var dot))
        {
            dot = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(dot);
            using var brush = new SolidBrush(ConnectionColor(connection));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillEllipse(brush, 3, 3, 10, 10);
            statusDots.Add(connection, dot);
        }
        item.Image = dot;
    }

    private sealed record SourceChoice(LogSource Source, string Label)
    {
        public override string ToString() => Label;
    }

    private string SourceLabel(LogSource source) =>
        source.ServerId is { } id ? $"{ServerName(id)} · {source}" : source.ToString();

    private string SourceLocation(LogSource source) =>
        source.ServerId is { } id
            ? $"{ServerName(id)} · {(source.Journal ? source.ToString() : source.Path)}"
            : source.Path;

    private string ServerName(string id) => servers.TryGetValue(id, out var server) ? server.Profile.Name : "Missing server";

    private SourceSession CreateSession(LogSource source) =>
        new(source, source.ServerId is { } id ? servers.GetValueOrDefault(id) : null);

    private void StopRemoteConnections()
    {
        remoteShutdown.Cancel();
        foreach (var server in servers.Values)
            server.Dispose();
        remoteShutdown.Dispose();
    }

    private void ShowServers() => ShowServersAt(serverMenuTrigger ?? this);

    private void ShowServersAt(Control trigger)
    {
        // WinForms can still be processing the item click after the menu closes.
        serverMenuStrip?.Dispose();
        var menu = serverMenuStrip = Chrome.Menu();
        menu.ShowImageMargin = true;
        menu.ShowItemToolTips = true;
        if (trigger == serverList && serverList.SelectedItem is ServerChoice selected)
            AddServerActions(menu.Items, selected.Server);
        else
        {
            menu.Items.Add("Add server…", null, (_, _) => AddServer());
            foreach (var server in servers.Values)
            {
                var serverMenu = new ToolStripMenuItem { Tag = server };
                UpdateServerMenuItem(serverMenu, server);
                AddServerActions(serverMenu.DropDownItems, server);
                Chrome.StyleStrip(serverMenu.DropDown);
                menu.Items.Add(serverMenu);
            }
        }        var anchor = trigger == serverList && serverList.SelectedIndex >= 0
            ? new Point(0, serverList.GetItemRectangle(serverList.SelectedIndex).Bottom)
            : new Point(0, trigger.Height);
        menu.Closed += (_, _) => { if (!trigger.IsDisposed) trigger.Focus(); };
        menu.Show(trigger, anchor);
    }
    private void AddServer() => EditServer(null);

    private void AddServerActions(ToolStripItemCollection items, RemoteServer server)
    {
        items.Add("Connect…", null, async (_, _) => await ConnectAsync(server));
        items.Add("Open remote log…", null, async (_, _) => await OpenRemoteAsync(server));
        items.Add("Browse with SFTP…", null, async (_, _) => await BrowseRemoteAsync(server));
        items.Add("Disconnect", null, (_, _) => DisconnectServer(server));
        items.Add(new ToolStripSeparator());
        items.Add("Edit server…", null, (_, _) => EditServer(server));
        items.Add("Delete server…", null, (_, _) => DeleteServer(server));
    }

    private static string TrustEndpoint(ServerProfile profile) => $"{profile.Host.ToLowerInvariant()}:{profile.Port}";

    private void DeleteServer(RemoteServer server)
    {
        var profile = server.Profile;
        var affected = state.Sources.Where(source => source.ServerId == profile.Id).ToArray();
        using var dialog = RemoteDialog("Delete SSH server", "Delete server",
            ("Server", new Label { Text = $"{profile.Name} ({profile.Host}:{profile.Port})" }),
            ("Sources", new Label { Text = $"Close {affected.Length} source(s) and disconnect this server?" }));
        dialog.AcceptButton = null;
        dialog.Shown += (_, _) => ((Control)dialog.CancelButton!).Focus();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var previousServers = state.Servers.ToList(); var previousSources = state.Sources.ToList();
        var previousPanes = state.Panes.ToArray(); var previousTrust = new Dictionary<string, string>(state.RetainedTrust);
        var saved = state.Servers.Single(item => item.Id == profile.Id);
        if (saved.Fingerprint.Length > 0) state.RetainedTrust[TrustEndpoint(saved)] = saved.Fingerprint;
        var ids = affected.Select(source => source.Id).ToHashSet();
        state.Servers.RemoveAll(item => item.Id == profile.Id);
        state.Sources.RemoveAll(item => ids.Contains(item.Id));
        for (var index = 0; index < state.Panes.Length; index++) if (state.Panes[index] is { } id && ids.Contains(id)) state.Panes[index] = null;
        if (!Save())
        {
            state.Servers = previousServers; state.Sources = previousSources; state.Panes = previousPanes; state.RetainedTrust = previousTrust;
            Chrome.Message(this, "Could not delete server", "Workspace could not be saved. The server and its sources were kept.");
            return;
        }
        DisconnectServer(server);
        foreach (var source in affected)
            if (sessions.Remove(source.Id, out var session)) { session.Filter.Dispose(); session.Log.Changed -= ScheduleRefresh; session.Filter.Changed -= ScheduleRefresh; observed.Remove(session); }
        server.Changed -= ScheduleRefresh;
        servers.Remove(profile.Id);
        RefreshServerIndicators(); UpdateSources();
        status.Text = $"Deleted server {profile.Name}";
    }
    private void DisconnectServer(RemoteServer server)
    {
        foreach (var session in sessions.Values.Where(session => session.Source.ServerId == server.Profile.Id))
            session.Reader.Stop();
        server.Dispose();
        status.Text = $"{server.Profile.Name} disconnected";
    }

    private void EditServer(RemoteServer? existing)
    {
        var previous = existing is null ? null : state.Servers.Single(profile => profile.Id == existing.Profile.Id);
        var name = new TextBox { Text = previous?.Name ?? "" };
        var host = new TextBox { Text = previous?.Host ?? "" };
        var port = new TextBox { Text = previous?.Port.ToString() ?? "22" };
        var user = new TextBox { Text = previous?.User ?? "" };
        var key = new TextBox { Text = previous?.KeyPath ?? "" };
        var fingerprint = new TextBox { Text = previous?.Fingerprint ?? "" };
        var method = new AuthenticationChoices(preferKey: previous?.KeyPath.Length > 0);
        var browse = Chrome.TextButton("Browse…");
        browse.Dock = DockStyle.Right;
        key.AccessibleName = "Private key path";
        Chrome.StyleField(key);
        key.Dock = DockStyle.Fill;
        var keyField = new Panel();
        keyField.Controls.Add(key);
        keyField.Controls.Add(browse);
        var hint = new Label
        {
            Text = "Leave blank to verify the server on first connection.",
            ForeColor = Chrome.Muted,
        };
        var error = new Label { ForeColor = Color.FromArgb(255, 155, 144), TextAlign = ContentAlignment.MiddleLeft };
        using var dialog = RemoteDialog(existing is null ? "Add SSH server" : "Edit SSH server", "Save", ("Name", name), ("Host", host), ("Port", port),
            ("Username", user), ("Authentication method", method), ("Private key path", keyField),
            ("Host fingerprint (optional)", fingerprint), ("", hint),
            ("", error));
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "Choose an SSH private key", CheckFileExists = true };
            var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            if (Directory.Exists(sshDirectory))
                picker.InitialDirectory = sshDirectory;
            if (picker.ShowDialog(dialog) == DialogResult.OK)
                key.Text = picker.FileName;
        };
        var layout = (TableLayoutPanel)dialog.Controls[0];
        void UpdateKeyField()
        {
            var publicKey = method.UseKey;
            keyField.Visible = publicKey;
            layout.GetControlFromPosition(0, 5)!.Visible = publicKey;
            layout.RowStyles[5].Height = publicKey ? 34 : 0;
            error.Text = "";
        }
        method.MethodChanged += UpdateKeyField;
        UpdateKeyField();
        var save = (Button)dialog.AcceptButton!;
        save.DialogResult = DialogResult.None;
        save.Click += (_, _) =>
        {
            void Reject(Control field, string message)
            {
                error.Text = message;
                field.Focus();
            }

            var publicKey = method.UseKey;
            var keyPath = publicKey ? key.Text.Trim() : "";
            if (keyPath.Length >= 2 && keyPath[0] == '"' && keyPath[^1] == '"')
                keyPath = keyPath[1..^1];
            if (string.IsNullOrWhiteSpace(name.Text))
                Reject(name, "Enter a server name.");
            else if (string.IsNullOrWhiteSpace(host.Text))
                Reject(host, "Enter a hostname or IP address.");
            else if (!int.TryParse(port.Text, out var portNumber) || portNumber is < 1 or > 65535)
                Reject(port, "Enter a port from 1 to 65535.");
            else if (string.IsNullOrWhiteSpace(user.Text))
                Reject(user, "Enter the SSH username.");
            else if (publicKey && !Path.IsPathFullyQualified(keyPath))
                Reject(key, "Enter an absolute Windows path to the private key.");
            else if (publicKey && keyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                Reject(key, "Choose the matching private key, usually the file without .pub.");
            else if (!string.IsNullOrWhiteSpace(fingerprint.Text) && NormalizeFingerprint(fingerprint.Text) is null)
                Reject(fingerprint, "Enter a valid SHA-256 host fingerprint or leave it blank.");
            else
            {
                var profile = new ServerProfile(previous?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), host.Text.Trim(),
                    portNumber, user.Text.Trim(), keyPath, NormalizeFingerprint(fingerprint.Text) ?? "");
                if (profile.Fingerprint.Length == 0 && previous is null && state.RetainedTrust.TryGetValue(TrustEndpoint(profile), out var trusted))
                    profile = profile with { Fingerprint = trusted };
                var profileIndex = previous is null ? -1 : state.Servers.FindIndex(item => item.Id == previous.Id);
                if (profileIndex < 0) state.Servers.Add(profile);
                else state.Servers[profileIndex] = profile;
                if (!Save())
                {
                    if (profileIndex < 0) state.Servers.Remove(profile);
                    else state.Servers[profileIndex] = previous!;
                    error.Text = "Could not save the profile. Check workspace access and retry.";
                    return;
                }
                if (existing is null) AddServerProfile(profile);
                else
                {
                    DisconnectServer(existing);
                    existing.UpdateProfile(profile);
                }
                RefreshServerIndicators();
                status.Text = $"Saved SSH server {profile.Name}";
                dialog.DialogResult = DialogResult.OK;
            }
        };
        dialog.ShowDialog(this);
    }

    private static string? NormalizeFingerprint(string value)
    {
        // Accept the digest itself or the fingerprint token from ssh-keygen -lf output.
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var token = tokens.Length == 1 ? tokens[0] :
            tokens.FirstOrDefault(part => part.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase));
        if (token is null)
            return null;
        var digest = token.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? token[7..] : token;
        if (digest.Length == 43)
            digest += "=";
        Span<byte> hash = stackalloc byte[32];
        return Convert.TryFromBase64String(digest, hash, out var length) && length == hash.Length
            ? "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=')
            : null;
    }

    private async Task<bool> ConfirmHostAsync(RemoteServer server, string fingerprint, CancellationToken token)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                if (closing || token.IsCancellationRequested)
                {
                    decision.TrySetResult(false);
                    return;
                }
                try
                {
                    var presented = new TextBox { Text = fingerprint, ReadOnly = true };
                    var instruction = new Label
                    {
                        Text = "Compare with the fingerprint from your server administrator before trusting.",
                        ForeColor = Chrome.Muted,
                    };
                    using var dialog = RemoteDialog("Verify SSH server", "Trust this server",
                        ("Server", new Label { Text = $"{server.Profile.Host}:{server.Profile.Port}" }),
                        ("SHA-256 host fingerprint", presented), ("Before trusting", instruction));
                    dialog.AcceptButton = null;
                    dialog.Shown += (_, _) => ((Control)dialog.CancelButton!).Focus();
                    using var registration = token.Register(() =>
                    {
                        try { BeginInvoke(() => { if (!dialog.IsDisposed) dialog.Close(); }); }
                        catch (InvalidOperationException) { }
                    });
                    if (dialog.ShowDialog(this) != DialogResult.OK || token.IsCancellationRequested || closing)
                    {
                        decision.TrySetResult(false);
                        return;
                    }

                    var index = state.Servers.FindIndex(profile => profile.Id == server.Profile.Id);
                    if (index < 0)
                    {
                        decision.TrySetResult(false);
                        return;
                    }
                    var previous = state.Servers[index];
                    state.Servers[index] = previous with { Fingerprint = fingerprint };
                    if (Save())
                        decision.TrySetResult(true);
                    else
                    {
                        state.Servers[index] = previous;
                        Chrome.Message(this, "Could not trust SSH server", "The fingerprint could not be saved. Check workspace access and retry.");
                        decision.TrySetResult(false);
                    }
                }
                catch (Exception error)
                {
                    decision.TrySetException(error);
                }
            });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        return await decision.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<CancellationToken?> ConnectAsync(RemoteServer server)
    {
        if (closing)
            return null;

        var profile = server.Profile;
        try
        {
            if (server.HasSession)
            {
                var existing = server.SessionToken;
                await server.GetClientAsync(remoteShutdown.Token);
                return !closing && !existing.IsCancellationRequested ? existing : null;
            }

            if (!credentialPrompts.Add(profile.Id))
                return null;
            var method = SshAuthMethod.Password;
            string value;
            try
            {
                var password = new TextBox { UseSystemPasswordChar = true };
                var passphrase = new TextBox { UseSystemPasswordChar = true };
                var selector = new AuthenticationChoices(preferKey: true);
                var error = new Label { ForeColor = Color.FromArgb(255, 155, 144) };
                var fields = new List<(string, Control)>();
                if (profile.KeyPath.Length > 0)
                    fields.Add(("Authentication method", selector));
                fields.Add(("Password", password));
                if (profile.KeyPath.Length > 0)
                    fields.Add(("Private key passphrase (optional)", passphrase));
                fields.Add(("", error));
                using var dialog = RemoteDialog($"Connect to {profile.Name}", "Connect", fields.ToArray());
                if (profile.KeyPath.Length == 0)
                {
                    selector.Dispose();
                    passphrase.Dispose();
                }
                else
                {
                    var layout = (TableLayoutPanel)dialog.Controls[0];
                    void SelectMethod()
                    {
                        password.Clear();
                        passphrase.Clear();
                        error.Text = "";
                        method = selector.UseKey ? SshAuthMethod.PrivateKey : SshAuthMethod.Password;
                        layout.RowStyles[1].Height = method == SshAuthMethod.Password ? 34 : 0;
                        layout.RowStyles[2].Height = method == SshAuthMethod.PrivateKey ? 34 : 0;
                        password.Visible = method == SshAuthMethod.Password;
                        passphrase.Visible = method == SshAuthMethod.PrivateKey;
                        layout.GetControlFromPosition(0, 1)!.Visible = method == SshAuthMethod.Password;
                        layout.GetControlFromPosition(0, 2)!.Visible = method == SshAuthMethod.PrivateKey;
                    }
                    selector.MethodChanged += SelectMethod;
                    SelectMethod();
                }

                var connect = (Button)dialog.AcceptButton!;
                dialog.Shown += (_, _) => (method == SshAuthMethod.Password ? password : passphrase).Focus();
                connect.DialogResult = DialogResult.None;
                connect.Click += (_, _) =>
                {
                    if (method == SshAuthMethod.Password && password.Text.Length == 0)
                    {
                        error.Text = "Enter the SSH password.";
                        password.Focus();
                        return;
                    }
                    dialog.DialogResult = DialogResult.OK;
                };
                if (dialog.ShowDialog(this) != DialogResult.OK || closing)
                    return null;
                value = method == SshAuthMethod.Password ? password.Text : passphrase.Text;
                password.Clear();
                if (!passphrase.IsDisposed)
                    passphrase.Clear();
            }
            finally
            {
                credentialPrompts.Remove(profile.Id);
            }

            if (!servers.TryGetValue(profile.Id, out var registered) || registered != server) return null;
            status.Text = $"Connecting to {profile.Name}…";
            var attempt = server.ConnectAsync(method, value, remoteShutdown.Token,
                (fingerprint, token) => ConfirmHostAsync(server, fingerprint, token));
            var session = server.SessionToken;
            await attempt;
            if (closing || session.IsCancellationRequested)
                return null;
            status.Text = $"{profile.Name} connected";
            return session;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error) when (error is SshException or IOException or ArgumentException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            if (!closing)
                status.Text = $"{profile.Name} connection failed. {error.Message}";
            return null;
        }
    }

    private async Task BrowseRemoteAsync(RemoteServer server)
    {
        if (await ConnectAsync(server) is not { } session || closing)
            return;
        using var picker = new RemoteBrowser(server.Profile.Name, async (path, elevated, token) =>
        {
            var client = await server.GetClientAsync(token);
            return elevated ? await SudoSftp.ListAsync(client, path, token) : await RemoteServer.ListEntriesAsync(client, path, token);
        }, session);
        if (picker.ShowDialog(this) == DialogResult.OK && !session.IsCancellationRequested && picker.SelectedPath is { } path)
            await OpenRemoteAsync(server, path);
    }
    private async Task OpenRemoteAsync(RemoteServer server, string initialPath = "")
    {
        var journal = new CheckBox { Text = "Follow systemd journal" };
        var path = new TextBox { Text = initialPath };
        var sudo = new CheckBox { Text = "Passwordless sudo (log command only)" };
        using var dialog = RemoteDialog($"Open remote log on {server.Profile.Name}", "Open",
            ("Source type", journal), ("Absolute path or systemd unit", path), ("Privileges", sudo));
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var requested = journal.Checked ? path.Text.Trim() : path.Text;
            var source = new LogSource(Guid.NewGuid().ToString("N"), requested, server.Profile.Id, journal.Checked, sudo.Checked);
            try
            {
                RemoteCommand.Build(source, null, state.InitialLines);
            }
            catch (ArgumentException error)
            {
                Chrome.Message(this, "Remote log", error.Message);
                continue;
            }

            if (await ConnectAsync(server) is not { IsCancellationRequested: false } || closing)
                return;

            var existing = state.Sources.Find(item => item.ServerId == source.ServerId &&
                item.Path == source.Path && item.Journal == source.Journal && item.Sudo == source.Sudo);
            if (existing is not null)
            {
                Assign(state.ActivePane, existing.Id);
                sessions[existing.Id].Reader.Start(state.InitialLines);
                return;
            }

            var session = CreateSession(source);
            state.Sources.Add(source);
            sessions.Add(source.Id, session);
            Assign(state.ActivePane, source.Id);
            session.Reader.Start(state.InitialLines);
            return;
        }
    }

    private static Form RemoteDialog(string title, string submit, params (string Label, Control Input)[] fields)
    {
        var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            AutoScaleMode = AutoScaleMode.Dpi, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(590, fields.Length * 34 + 55)
        };
        Chrome.StyleForm(dialog);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < fields.Length; index++)
        {
            var (label, input) = fields[index];
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true }, 0, index);
            if (input is not Label)
            {
                Chrome.StyleField(input);
                input.AccessibleName = label;
            }
            input.Dock = DockStyle.Fill;
            layout.Controls.Add(input, 1, index);
        }
        var ok = Chrome.TextButton(submit, primary: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Chrome.TextButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons, 1, fields.Length);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog;
    }
}










