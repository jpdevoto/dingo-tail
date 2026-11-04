// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;

namespace DingoTail;

internal sealed class WorkspaceState
{
    public int LogFontSize { get; set; } = 14;
    public Dictionary<string, string> RetainedTrust { get; set; } = [];
    public int Layout { get; set; } = 1;
    public int ActivePane { get; set; }
    public string?[] Panes { get; set; } = new string?[4];
    public bool LocalExpanded { get; set; } = true;
    public List<HighlightRule> HighlightRules { get; set; } = HighlightRule.Defaults();
    public List<ServerProfile> Servers { get; set; } = [];
    public List<LogSource> Sources { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(
        Environment.GetEnvironmentVariable("DINGOTAIL_WORKSPACE_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DingoTail.WinForms"),
        "workspace.json");

    public static WorkspaceState Load()
    {
        if (!File.Exists(FilePath))
            return new();
        using var file = File.OpenRead(FilePath);
        var state = JsonSerializer.Deserialize<WorkspaceState>(file, Json) ?? throw new JsonException("Workspace is empty.");
        if (state.Layout is not (1 or 2 or 4) || state.Panes is not { Length: 4 } ||
            state.Servers is null || state.Servers.Any(server => server is null || string.IsNullOrWhiteSpace(server.Id) ||
                string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Host) ||
                string.IsNullOrWhiteSpace(server.User) || server.Port is < 1 or > 65535 ||
                server.KeyPath is null || server.Fingerprint is null) ||
            state.Servers.Select(server => server.Id).Distinct().Count() != state.Servers.Count ||
            state.Sources is null || state.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Id) ||
                source.Path is null || (source.ServerId is null && !Path.IsPathFullyQualified(source.Path))) ||
            state.Sources.Select(source => source.Id).Distinct().Count() != state.Sources.Count)
            throw new JsonException("Workspace contains invalid source or pane definitions.");

        state.LogFontSize = Math.Clamp(state.LogFontSize, 10, 24);
        state.RetainedTrust ??= [];
        state.HighlightRules ??= HighlightRule.Defaults();
        state.ActivePane = Math.Clamp(state.ActivePane, 0, state.Layout - 1);
        var assigned = new HashSet<string>();
        for (var index = 0; index < state.Panes.Length; index++)
        {
            var id = state.Panes[index];
            if (id is not null && (!state.Sources.Any(source => source.Id == id) || !assigned.Add(id)))
                state.Panes[index] = null;
        }
        return state;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"workspace-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, this, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

internal sealed record LogSource(string Id, string Path, string? ServerId = null, bool Journal = false, bool Sudo = false)
{
    public override string ToString() => Journal ? (Path.Length == 0 ? "System journal" : Path) : System.IO.Path.GetFileName(Path);
}

internal sealed class SourceSession(LogSource source, RemoteServer? server = null)
{
    public LogSource Source { get; } = source;
    public LogBuffer Log { get; } = new();
    public ISourceReader Reader => reader ??= Source.ServerId is null ? new LocalTail(Source.Path, Log) : new RemoteTail(Source, Log, server);
    private ISourceReader? reader;
    private FilteredLog? filter;
    public FilteredLog Filter => filter ??= new(Log);

    public bool Follow { get; set; } = true;
    public long Anchor { get; set; }
    public long? Selected { get; set; }
    public int HorizontalOffset { get; set; }
}




