// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using Tmds.Ssh;

namespace DingoTail;

internal interface ISourceReader
{
    void Start(int initialLines);
    void Stop();
}

internal sealed record ServerProfile(string Id, string Name, string Host, int Port,
    string User, string KeyPath, string Fingerprint)
{
    public override string ToString() => Name;
}

internal enum ServerConnectionState { Disconnected, Connecting, AwaitingTrust, Connected, Reconnecting, Failed }

internal enum SshAuthMethod { Password, PrivateKey }

internal sealed class RemoteServer(ServerProfile profile) : IDisposable
{
    private readonly object gate = new();
    private Connection? connection;
    private ServerConnectionState state;
    private string? failure;
    private string fingerprint = profile.Fingerprint;

    public ServerProfile Profile { get; private set; } = profile;

    public void UpdateProfile(ServerProfile updated)
    {
        lock (gate)
        {
            Dispose();
            Profile = updated;
            fingerprint = updated.Fingerprint;
        }
        Changed?.Invoke();
    }
    public event Action? Changed;

    public (ServerConnectionState State, string? Failure) Status
    {
        get { lock (gate) return (state, failure); }
    }

    public bool HasSession
    {
        get { lock (gate) return connection is not null; }
    }

    public CancellationToken SessionToken
    {
        get { lock (gate) return connection?.Token ?? new CancellationToken(true); }
    }

    public Task<SshClient> ConnectAsync(SshAuthMethod method, string secret, CancellationToken token,
        Func<string, CancellationToken, Task<bool>>? confirmHost = null)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (connection is null)
            {
                var next = new Connection(method, secret) { ConfirmHost = confirmHost };
                connection = next;
                SetState(ServerConnectionState.Connecting);
                next.Ready = Task.Run(() => EstablishAsync(next, recovering: false));
            }
            return GetClientAsync(token);
        }
    }

    public async Task<SshClient> GetClientAsync(CancellationToken token, CancellationToken? expectedSession = null)
    {
        Connection current;
        Task<SshClient> pending;
        lock (gate)
        {
            current = connection ?? throw new InvalidOperationException("Connect the server before opening a source.");
            if (expectedSession is { } expected && current.Token != expected)
                throw new InvalidOperationException("The source's SSH session ended.");
            pending = current.Ready;
        }

        var client = await pending.WaitAsync(token).ConfigureAwait(false);
        current.Token.ThrowIfCancellationRequested();
        return client;
    }

    private async Task<SshClient> EstablishAsync(Connection current, bool recovering)
    {
        var delay = 1;
        while (true)
        {
            current.Token.ThrowIfCancellationRequested();
            if (recovering)
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), current.Token).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 30);
            }

            SshClient? candidate = null;
            string? rejected = null;
            var declined = false;
            try
            {
                var secret = current.Secret ?? throw new OperationCanceledException(current.Token);
                var settings = new SshClientSettings
                {
                    HostName = Profile.Host, Port = Profile.Port, UserName = Profile.User,
                    Credentials = current.Method == SshAuthMethod.Password
                        ? [new PasswordCredential(secret)]
                        : [new PrivateKeyCredential(Profile.KeyPath, secret.Length == 0 ? null : secret)],
                    UserKnownHostsFilePaths = [], GlobalKnownHostsFilePaths = [],
                    UpdateKnownHostsFileAfterAuthentication = false,
                    ConnectTimeout = TimeSpan.FromSeconds(fingerprint.Length == 0 ? 120 : 15),
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                    HostAuthentication = async (context, token) =>
                    {
                        var presented = "SHA256:" + context.ConnectionInfo.ServerKey.Key.SHA256FingerPrint;
                        if (fingerprint.Length == 0 && current.ConfirmHost is { } confirm)
                        {
                            lock (gate)
                            {
                                current.Token.ThrowIfCancellationRequested();
                                SetState(ServerConnectionState.AwaitingTrust);
                            }
                            current.ConfirmHost = null;
                            using var trustLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, current.Token);
                            if (!await confirm(presented, trustLifetime.Token).ConfigureAwait(false))
                            {
                                declined = true;
                                return false;
                            }
                            lock (gate)
                            {
                                current.Token.ThrowIfCancellationRequested();
                                fingerprint = presented;
                                SetState(ServerConnectionState.Connecting);
                            }
                            return true;
                        }
                        var accepted = string.Equals(presented, fingerprint, StringComparison.Ordinal);
                        if (!accepted)
                            rejected = presented;
                        return accepted;
                    }
                };
                candidate = new SshClient(settings);
                await candidate.ConnectAsync(current.Token).ConfigureAwait(false);
                lock (gate)
                {
                    current.Token.ThrowIfCancellationRequested();
                    current.Client = candidate;
                    SetState(ServerConnectionState.Connected);
                    _ = WatchTransportAsync(current, candidate, candidate.Disconnected);
                }
                return candidate;
            }
            catch (Exception error)
            {
                candidate?.Dispose();
                current.Token.ThrowIfCancellationRequested();
                if (recovering && rejected is null && IsTransient(error))
                {
                    lock (gate)
                        if (connection == current)
                            SetState(ServerConnectionState.Reconnecting, error.Message);
                    continue;
                }

                var detail = declined ? "Host key was not trusted. The connection was cancelled." :
                    rejected is null ? error.Message :
                    $"Host key mismatch. Expected {fingerprint}. Server presented {rejected}.";
                lock (gate)
                {
                    if (connection == current)
                    {
                        connection = null;
                        current.Secret = null;
                        current.Lifetime.Cancel();
                        current.Lifetime.Dispose();
                        SetState(declined ? ServerConnectionState.Disconnected : ServerConnectionState.Failed, detail);
                    }
                }
                throw new InvalidOperationException(detail, error);
            }
        }
    }

    private async Task WatchTransportAsync(Connection current, SshClient client, CancellationToken disconnected)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(disconnected, current.Token);
        try { await Task.Delay(Timeout.Infinite, wait.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        finally
        {
            lock (gate)
            {
                if (connection == current && current.Client == client)
                {
                    current.Client = null;
                    SetState(ServerConnectionState.Reconnecting);
                    current.Ready = Task.Run(() => EstablishAsync(current, recovering: true));
                    _ = ObserveFailureAsync(current.Ready);
                }
            }
            client.Dispose();
        }
    }

    private static async Task ObserveFailureAsync(Task task) =>
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static bool IsTransient(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is System.Net.Sockets.SocketException or IOException or TimeoutException or SshConnectionClosedException)
                return true;
        return false;
    }

    private void SetState(ServerConnectionState value, string? detail = null)
    {
        state = value;
        failure = detail;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
        {
            var previous = connection;
            connection = null;
            if (previous is not null)
            {
                previous.Secret = null;
                previous.Lifetime.Cancel();
                previous.Lifetime.Dispose();
            }
            SetState(ServerConnectionState.Disconnected);
        }
    }

    private sealed class Connection
    {
        public SshAuthMethod Method { get; }
        public string? Secret;
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationToken Token { get; }
        public SshClient? Client;
        public Func<string, CancellationToken, Task<bool>>? ConfirmHost;
        public Task<SshClient> Ready = null!;

        public Connection(SshAuthMethod method, string secret)
        {
            Method = method;
            Secret = secret;
            Token = Lifetime.Token;
        }
    }

    public sealed record DirectoryEntry(string Path, UnixFileType Type, long Size)
    {
        public string Name => Path.TrimEnd('/').Split('/').Last();
        public bool IsDirectory => Type == UnixFileType.Directory;
    }

    public static async Task<List<DirectoryEntry>> ListEntriesAsync(SshClient client, string directory, CancellationToken token)
    {
        using var sftp = await client.OpenSftpClientAsync(token);
        var options = new Tmds.Ssh.EnumerationOptions { FollowFileLinks = false, FollowDirectoryLinks = false };
        var entries = new List<DirectoryEntry>();
        await foreach (var entry in sftp.GetDirectoryEntriesAsync(directory, options).WithCancellation(token))
        {
            var item = new DirectoryEntry(entry.Path, entry.Attributes.FileType, entry.Attributes.Length);
            if (item.Name is not "." and not "..") entries.Add(item);
            if (entries.Count == 10_000) break;
        }
        return entries;
    }
    public static async Task<List<string>> ListPathsAsync(SshClient client, string directory, CancellationToken token)
    {
        using var sftp = await client.OpenSftpClientAsync(token);
        // Listing a readable directory must also work when a symlink points at a protected path.
        var options = new Tmds.Ssh.EnumerationOptions { FollowFileLinks = false, FollowDirectoryLinks = false };
        var paths = new List<string>();
        await foreach (var entry in sftp.GetDirectoryEntriesAsync(directory, options).WithCancellation(token))
        {
            paths.Add(entry.Path);
            if (paths.Count == 10_000)
                break;
        }
        return paths;
    }
}

internal static class RemoteCommand
{
    public static string Build(LogSource source, string? cursor, int initialLines)
    {
        InitialLineCount.Validate(initialLines);
        var count = initialLines.ToString(CultureInfo.InvariantCulture);
        var sudo = source.Sudo ? "sudo -n -- " : "";
        if (!source.Journal)
        {
            if (!source.Path.StartsWith('/'))
                throw new ArgumentException("Remote log paths must be absolute.");
            return sudo + $"tail -n {count} -F -- " + Quote(source.Path);
        }

        var start = cursor is null ? $"-n {count}" : "--after-cursor=" + Quote(cursor);
        var unit = source.Path.Length == 0 ? "" : " --unit=" + Quote(source.Path);
        return sudo + "journalctl --no-pager -f -o json " + start + unit;
    }

    private static string Quote(string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Remote arguments cannot contain NUL.");
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}

internal sealed class RemoteTail(LogSource source, LogBuffer log, RemoteServer? server) : ISourceReader
{
    private readonly object gate = new();
    private CancellationTokenSource? running;

    public void Start(int initialLines)
    {
        InitialLineCount.Validate(initialLines);
        lock (gate)
        {
            if (running is not null)
                return;
            if (server?.HasSession != true)
            {
                log.SetStatus("Stopped", "Connect the source's server before starting.");
                return;
            }
            var run = new CancellationTokenSource();
            var session = server.SessionToken;
            running = run;
            log.SetStatus("Opening SSH stream");
            _ = Task.Run(() => RunAsync(run, session, initialLines));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            running?.Cancel();
            running = null;
            log.SetStatus("Stopped");
        }
    }

    private void Publish(CancellationTokenSource run, Action change)
    {
        lock (gate)
        {
            if (running == run)
                change();
        }
    }

    private async Task RunAsync(CancellationTokenSource run, CancellationToken session, int initialLines)
    {
        var token = run.Token;
        var delay = 1;
        var recovering = false;
        string? cursor = null;
        var stdout = new byte[16 * 1024];
        var stderr = new byte[4096];
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var errorText = new byte[4096];
                var errorLength = 0;
                try
                {
                    var client = await server!.GetClientAsync(token, session);
                    using var process = await client.ExecuteAsync(RemoteCommand.Build(source, cursor, initialLines), token);
                    var decoder = new LineDecoder(Encoding.UTF8);
                    Publish(run, () =>
                    {
                        if (recovering)
                            log.Append([source.Journal && cursor is not null
                                ? "[SSH journal stream resumed from cursor]"
                                : $"[SSH {(source.Journal ? "journal" : "file")} stream reopened with up to {initialLines} recent lines]"]);
                        log.SetStatus("Live · SSH");
                    });
                    var batch = new List<string>(128);
                    while (true)
                    {
                        var (isError, count) = await process.ReadAsync(stdout, stderr, token);
                        if (count == 0)
                            break;
                        delay = 1;
                        if (isError)
                        {
                            var copy = Math.Min(errorText.Length - errorLength, count);
                            stderr.AsSpan(0, copy).CopyTo(errorText.AsSpan(errorLength));
                            errorLength += copy;
                            continue;
                        }

                        decoder.Feed(stdout.AsSpan(0, count), line =>
                        {
                            batch.Add(source.Journal ? FormatJournal(line, ref cursor) : line);
                            if (batch.Count == 128)
                            {
                                Publish(run, () => log.Append(batch));
                                batch.Clear();
                            }
                        });
                        Publish(run, () => log.Append(batch));
                        batch.Clear();
                    }

                    var exit = await process.GetExitStatusAsync(token);
                    var detail = Encoding.UTF8.GetString(errorText, 0, errorLength).Trim();
                    Publish(run, () =>
                    {
                        var result = exit.ExitSignal is { Length: > 0 } signal ? $"signal {signal}" : $"status {exit.ExitCode}";
                        log.Append([$"[Remote command exited with {result}]"]);
                        log.SetStatus("Stopped", detail);
                    });
                    return;
                }
                catch (Exception error) when ((error is SshConnectionException or SshChannelClosedException or ObjectDisposedException) && server?.HasSession == true)
                {
                    Publish(run, () =>
                    {
                        if (!recovering)
                            log.Append(["[SSH stream interrupted]"]);
                        log.SetStatus("Reconnecting · SSH", error.Message);
                    });
                    recovering = true;
                    await Task.Delay(TimeSpan.FromSeconds(delay), token);
                    delay = Math.Min(delay * 2, 30);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is SshException or IOException or ArgumentException or InvalidOperationException)
        {
            Publish(run, () => log.SetStatus("Stopped", error.Message));
        }
        finally
        {
            lock (gate)
            {
                if (running == run)
                    running = null;
                run.Dispose();
            }
        }
    }

    internal static string FormatJournal(string line, ref string? cursor)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            if (row.ValueKind != JsonValueKind.Object)
                return line;
            static string? ReadString(JsonElement row, string key) =>
                row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            cursor = ReadString(row, "__CURSOR") ?? cursor;
            var timestamp = ReadString(row, "__REALTIME_TIMESTAMP") ?? "";
            var priority = ReadString(row, "PRIORITY") ?? "?";
            var message = ReadString(row, "MESSAGE") ?? (row.TryGetProperty("MESSAGE", out var raw) ? raw.GetRawText() : "null");
            return $"{timestamp} [{priority}] {message.Replace("\n", " ↵ ", StringComparison.Ordinal)}";
        }
        catch (JsonException)
        {
            return line;
        }
    }
}







