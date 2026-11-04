// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;

namespace DingoTail;

internal sealed record FilterSpec(string Include = "", string Exclude = "", bool Regex = false)
{
    public bool Active => Include.Length > 0 || Exclude.Length > 0;
}

internal sealed class FilteredLog : IDisposable
{
    private readonly LogBuffer log;
    private readonly object gate = new();
    private FilterSpec draft = new(), accepted = new();
    private LogBuffer.Row[] rows = [];
    private long revision;
    private long edit;
    private bool working, dirty, disposed;
    private string error = "";
    private CancellationTokenSource edits = new();
    public event Action? Changed;
    public FilteredLog(LogBuffer log) { this.log = log; log.Changed += Refresh; Refresh(); }
    public (FilterSpec Draft, FilterSpec Accepted, string Error, long Revision, LogBuffer.Row[] Rows) Snapshot()
    { lock (gate) return (draft, accepted, error, revision, rows); }
    public void Set(FilterSpec spec)
    {
        lock (gate) { draft = spec; edit++; edits.Cancel(); edits.Dispose(); edits = new(); }
        Refresh();
    }
    private void Refresh()
    {
        lock (gate) { if (disposed) return; dirty = true; if (working) return; working = true; }
        _ = Task.Run(WorkAsync);
    }
    private async Task WorkAsync()
    {
        var compiled = new Dictionary<FilterSpec, (Regex? Include, Regex? Exclude)>();
        long through = -1;
        long rejectedEdit = -1;
        while (true)
        {
            FilterSpec wanted, prior;
            long generation;
            CancellationToken token;
            lock (gate)
            {
                if (disposed || !dirty) { working = false; return; }
                dirty = false; wanted = draft; prior = accepted; generation = edit; token = edits.Token;
            }
            try
            {
                await Task.Delay(wanted != prior ? 200 : 100, token);
                var snapshot = log.Snapshot(0, LogBuffer.MaxRows);
                string problem = rejectedEdit == generation ? error : "";
                if (rejectedEdit == generation) wanted = prior;
                LogBuffer.Row[] result;
                try
                {
                    if (wanted == prior && error.Length == 0 && through >= 0)
                    {
                        var first = snapshot.Length > 0 ? snapshot[0].Id : long.MaxValue;
                        result = rows.Where(row => row.Id >= first).Concat(Match(snapshot.Where(row => row.Id > through).ToArray(), wanted, token, compiled)).ToArray();
                    }
                    else result = Match(snapshot, wanted, token, compiled);
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    rejectedEdit = generation;
                    problem = ex is RegexMatchTimeoutException ? "Regex exceeded its time limit. The previous filter remains active." : ex.Message;
                    wanted = prior;
                    try { result = Match(snapshot, prior, token, compiled); }
                    catch (RegexMatchTimeoutException)
                    { wanted = new(); result = snapshot; problem = "The active regex exceeded its time limit and was disabled. All retained rows are shown."; }
                }
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (disposed || generation != edit) continue;
                    accepted = wanted; rows = result; through = snapshot.Length == 0 ? -1 : snapshot[^1].Id; error = problem; revision++;
                }
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { }
        }
    }
    private static LogBuffer.Row[] Match(LogBuffer.Row[] source, FilterSpec spec, CancellationToken token, Dictionary<FilterSpec, (Regex? Include, Regex? Exclude)> compiled)
    {
        Regex? Compile(string pattern) => pattern.Length == 0 ? null : new Regex(pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));
        if (!compiled.TryGetValue(spec, out var matchers))
        {
            matchers = (spec.Regex ? Compile(spec.Include) : null, spec.Regex ? Compile(spec.Exclude) : null);
            if (compiled.Count > 4) compiled.Clear();
            compiled[spec] = matchers;
        }
        var (include, exclude) = matchers;
        var result = new List<LogBuffer.Row>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var row in source)
        {
            token.ThrowIfCancellationRequested();
            if (spec.Regex && clock.ElapsedMilliseconds > 500) throw new RegexMatchTimeoutException();
            bool Test(string pattern, Regex? regex) => spec.Regex ? regex!.IsMatch(row.Text) : row.Text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if ((spec.Include.Length == 0 || Test(spec.Include, include)) && (spec.Exclude.Length == 0 || !Test(spec.Exclude, exclude))) result.Add(row);
        }
        return result.ToArray();
    }
    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; edits.Cancel(); } log.Changed -= Refresh; }
}


