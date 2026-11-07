// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DingoTail;

internal sealed class LocalTail : ISourceReader
{
    private readonly object gate = new();
    private readonly string path;
    private readonly LogBuffer log;
    private CancellationTokenSource? running;

    public LocalTail(string path, LogBuffer log)
    {
        this.path = path;
        this.log = log;
    }

    public void Start(int initialLines)
    {
        InitialLineCount.Validate(initialLines);
        lock (gate)
        {
            if (running is not null)
                return;
            var run = new CancellationTokenSource();
            running = run;
            log.SetStatus("Opening local file");
            _ = Task.Run(() => RunAsync(run, initialLines));
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

    private async Task RunAsync(CancellationTokenSource run, int initialLines)
    {
        var token = run.Token;
        var chunk = new byte[64 * 1024];
        FileStream? current = null;
        FileIdentity identity = default;
        LineDecoder? decoder = null;
        var interrupted = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                FileStream? candidate = null;
                try
                {
                    candidate = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, chunk.Length, FileOptions.Asynchronous);
                    var candidateId = GetIdentity(candidate.SafeFileHandle);
                    var replaced = current is not null && candidateId != identity;
                    var truncated = current is not null && candidate.Length < current.Position;
                    if (current is null || replaced || truncated)
                    {
                        var initial = await ReadInitialAsync(candidate, chunk,
                            replaced || truncated ? LogBuffer.MaxRows : initialLines, token);
                        current?.Dispose();
                        current = candidate;
                        candidate = null;
                        identity = candidateId;
                        decoder = initial.Decoder;
                        var marker = replaced ? "[Local file replaced]"
                            : truncated ? "[Local file truncated]"
                            : interrupted ? "[Local file available again]" : null;
                        Publish(run, () =>
                        {
                            if (marker is not null)
                                log.Append([marker]);
                            foreach (var batch in initial.Lines.Chunk(128))
                                log.Append(batch);
                        });
                    }

                    candidate?.Dispose();
                    candidate = null;
                    for (var budget = 4 * 1024 * 1024; budget > 0;)
                    {
                        var read = await current.ReadAsync(chunk, token);
                        if (read == 0)
                            break;
                        budget -= read;
                        var lines = new List<string>(128);
                        decoder!.Feed(chunk.AsSpan(0, read), line =>
                        {
                            lines.Add(line);
                            if (lines.Count == 128)
                            {
                                Publish(run, () => log.Append(lines));
                                lines.Clear();
                            }
                        });
                        Publish(run, () => log.Append(lines));
                    }

                    Publish(run, () => log.SetStatus("Live · local"));
                    interrupted = false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
                {
                    current?.Dispose();
                    current = null;
                    Publish(run, () =>
                    {
                        if (!interrupted)
                            log.Append([$"[Local read interrupted: {error.Message}]"]);
                        log.SetStatus("Waiting for local file", error.Message);
                    });
                    interrupted = true;
                }
                finally
                {
                    candidate?.Dispose();
                }

                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            current?.Dispose();
            lock (gate)
            {
                if (running == run)
                    running = null;
                run.Dispose();
            }
        }
    }

    private static async Task<(LineDecoder Decoder, Queue<string> Lines)> ReadInitialAsync(
        FileStream file, byte[] chunk, int maxRows, CancellationToken token)
    {
        var length = file.Length;
        var bomLength = await file.ReadAtLeastAsync(chunk.AsMemory(0, 3), 3, false, token);
        var encoding = Encoding.UTF8;
        var skip = 0;
        if (bomLength >= 2 && chunk[0] == 0xff && chunk[1] == 0xfe)
        {
            encoding = Encoding.Unicode;
            skip = 2;
        }
        else if (bomLength >= 2 && chunk[0] == 0xfe && chunk[1] == 0xff)
        {
            encoding = Encoding.BigEndianUnicode;
            skip = 2;
        }
        else if (bomLength == 3 && chunk[0] == 0xef && chunk[1] == 0xbb && chunk[2] == 0xbf)
        {
            skip = 3;
        }

        var start = Math.Max(skip, length - LogBuffer.MaxBytes);
        if (encoding != Encoding.UTF8 && start % 2 != 0)
            start++;
        file.Position = start;
        var discardFirst = start > skip;
        var decoder = new LineDecoder(encoding);
        var recent = new Queue<string>();
        var bytes = 0;
        while (file.Position < length)
        {
            var read = await file.ReadAsync(chunk.AsMemory(0, (int)Math.Min(length - file.Position, chunk.Length)), token);
            if (read == 0)
                break;
            decoder.Feed(chunk.AsSpan(0, read), line =>
            {
                if (discardFirst)
                {
                    discardFirst = false;
                    return;
                }
                recent.Enqueue(line);
                bytes += Encoding.UTF8.GetByteCount(line);
                while (recent.Count > maxRows || bytes > LogBuffer.MaxBytes)
                    bytes -= Encoding.UTF8.GetByteCount(recent.Dequeue());
            });
        }
        return (decoder, recent);
    }

    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow);

    private static FileIdentity GetIdentity(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Volume, info.IndexHigh, info.IndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}

