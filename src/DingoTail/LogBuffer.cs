// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;

namespace DingoTail;

internal sealed class LogBuffer
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxRows = 20_000;
    public const int MaxLineBytes = 64 * 1024;

    private readonly object gate = new();
    private readonly Row[] rows = new Row[MaxRows];
    private int head;
    private int count;
    private int bytes;
    private long nextId;
    private long revision;
    private string status = "Stopped";
    private string diagnostic = "";

    public event Action? Changed;

    public readonly record struct Row(long Id, string Text, int Bytes);
    public readonly record struct State(long Revision, long FirstId, int Count, string Status, string Diagnostic);

    public State GetState()
    {
        lock (gate)
            return new(revision, nextId - count, count, status, diagnostic);
    }

    public void SetStatus(string value, string detail = "")
    {
        lock (gate)
        {
            if (status == value && diagnostic == detail)
                return;
            status = value;
            diagnostic = detail;
            revision++;
        }
        Changed?.Invoke();
    }

    public void Append(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        lock (gate)
        {
            foreach (var line in lines)
            {
                var length = Encoding.UTF8.GetByteCount(line);
                var text = line;
                if (length > MaxLineBytes)
                {
                    const string marker = " [line truncated]";
                    var limit = MaxLineBytes - marker.Length;
                    var chars = 0;
                    var used = 0;
                    foreach (var rune in line.EnumerateRunes())
                    {
                        if (used + rune.Utf8SequenceLength > limit)
                            break;
                        chars += rune.Utf16SequenceLength;
                        used += rune.Utf8SequenceLength;
                    }
                    text = line[..chars] + marker;
                    length = used + marker.Length;
                }
                while (count == MaxRows || bytes + length > MaxBytes)
                {
                    bytes -= rows[head].Bytes;
                    rows[head] = default;
                    head = (head + 1) % MaxRows;
                    count--;
                }
                rows[(head + count) % MaxRows] = new(nextId++, text, length);
                bytes += length;
                count++;
            }
            revision++;
        }
        Changed?.Invoke();
    }

    public Row[] Snapshot(long firstId, int maximum)
    {
        lock (gate)
        {
            var offset = (int)Math.Clamp(firstId - (nextId - count), 0, count);
            var result = new Row[Math.Min(maximum, count - offset)];
            for (var index = 0; index < result.Length; index++)
                result[index] = rows[(head + offset + index) % MaxRows];
            return result;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Array.Clear(rows);
            head = count = bytes = 0;
            revision++;
        }
        Changed?.Invoke();
    }
}

