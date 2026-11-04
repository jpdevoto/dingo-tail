// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
using Tmds.Ssh;
namespace DingoTail;

internal static class SudoSftp
{
    internal const string Command = "for p in /usr/lib/openssh/sftp-server /usr/libexec/openssh/sftp-server /usr/lib/ssh/sftp-server /usr/libexec/sftp-server; do if test -x \"$p\"; then exec sudo -n -- \"$p\" -R; fi; done; echo 'No supported external OpenSSH sftp-server found' >&2; exit 127";
    internal static async Task<List<RemoteServer.DirectoryEntry>> ListAsync(SshClient client, string path, CancellationToken token)
    {
        using var process = await client.ExecuteAsync(Command, token);
        var transport = new Protocol(async (data, ct) => await process.WriteAsync(data, ct), async (output, error, ct) => await process.ReadAsync(output, error, ct));
        try { return await transport.ListAsync(path, token); }
        catch (Exception error) when (error is IOException or SshException or ArgumentException)
        { await transport.DrainErrorAsync(token); throw new IOException($"Sudo browsing failed. {transport.Error} {error.Message} Requires existing passwordless permission for external sftp-server -R.", error); }
    }
    internal sealed class Packet(byte[] bytes)
    {
        private int offset;
        public byte Byte() { Need(1); return bytes[offset++]; }
        public uint UInt() { Need(4); var value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)); offset += 4; return value; }
        public ulong Long() => ((ulong)UInt() << 32) | UInt();
        private void Need(int count) { if (count < 0 || count > bytes.Length - offset) throw new IOException("Malformed SFTP packet."); }
        public byte[] String() { var length = UInt(); if (length > 1024 * 1024) throw new IOException("Oversized SFTP string."); Need((int)length); var value = bytes.AsSpan(offset, (int)length).ToArray(); offset += (int)length; return value; }
        public string Text() => Encoding.UTF8.GetString(String());
    }
    internal sealed class Protocol(Func<byte[], CancellationToken, Task> write, Func<byte[], byte[], CancellationToken, Task<(bool IsError, int Count)>> readOutput)
    {
        private readonly byte[] stdout = new byte[16384], stderr = new byte[8192];
        private readonly Queue<byte> pending = new();
        private readonly MemoryStream errors = new();
        private uint request;
        public string Error => Encoding.UTF8.GetString(errors.ToArray()).Trim();
        public async Task DrainErrorAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                while (true)
                {
                    var (isError, count) = await readOutput(stdout, stderr, timeout.Token);
                    if (count == 0) break;
                    if (isError) errors.Write(stderr, 0, Math.Min(count, 8192 - (int)errors.Length));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SshException or InvalidOperationException) { }
        }
        private async Task<byte[]> Exact(int count, CancellationToken token)
        {
            var result = new byte[count];
            for (var index = 0; index < count;)
            {
                if (pending.TryDequeue(out var next)) { result[index++] = next; continue; }
                var (isError, read) = await readOutput(stdout, stderr, token);
                if (read == 0) throw new IOException("SFTP channel closed before completing the request.");
                if (isError) errors.Write(stderr, 0, Math.Min(read, 8192 - (int)errors.Length));
                else foreach (var value in stdout.AsSpan(0, read).ToArray()) pending.Enqueue(value);
            }
            return result;
        }
        private async Task<Packet> Receive(CancellationToken token)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(await Exact(4, token));
            if (length is < 1 or > 1048576) throw new IOException("Invalid SFTP packet length.");
            return new(await Exact((int)length, token));
        }
        private static void UInt(Stream stream, uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
        private async Task Send(byte type, uint id, byte[]? value, CancellationToken token)
        {
            using var payload = new MemoryStream(); payload.WriteByte(type); UInt(payload, id);
            if (value is not null) { UInt(payload, (uint)value.Length); payload.Write(value); }
            using var frame = new MemoryStream(); UInt(frame, (uint)payload.Length); payload.Position = 0; payload.CopyTo(frame);
            await write(frame.ToArray(), token);
        }
        private async Task<(byte Type, Packet Data)> Request(byte type, byte[] value, CancellationToken token)
        {
            var id = ++request; await Send(type, id, value, token);
            var packet = await Receive(token); var kind = packet.Byte();
            if (packet.UInt() != id) throw new IOException("Unexpected SFTP request ID.");
            return (kind, packet);
        }
        private static uint Status(Packet packet, params uint[] allowed)
        {
            var code = packet.UInt(); var message = packet.Text(); packet.Text();
            if (!allowed.Contains(code)) throw new IOException($"SFTP status {code}: {message}");
            return code;
        }
        public async Task<List<RemoteServer.DirectoryEntry>> ListAsync(string path, CancellationToken token)
        {
            await Send(1, 3, null, token);
            var hello = await Receive(token);
            if (hello.Byte() != 2 || hello.UInt() != 3) throw new IOException("SFTP version 3 required.");
            var opened = await Request(11, Encoding.UTF8.GetBytes(path), token);
            if (opened.Type == 101) Status(opened.Data);
            if (opened.Type != 102) throw new IOException("Expected directory handle.");
            var handle = opened.Data.String(); var rows = new List<RemoteServer.DirectoryEntry>();
            try
            {
                while (rows.Count < 10000)
                {
                    var response = await Request(12, handle, token);
                    if (response.Type == 101) { Status(response.Data, 1); break; }
                    if (response.Type != 104) throw new IOException("Expected directory entries.");
                    var count = response.Data.UInt();
                    if (count > 10000) throw new IOException("Too many entries in SFTP packet.");
                    for (var i = 0; i < count; i++)
                    {
                        var name = response.Data.Text(); response.Data.Text();
                        var flags = response.Data.UInt();
                        if ((flags & ~0x8000000fu) != 0) throw new IOException("Unknown SFTP attributes.");
                        var size = (flags & 1) != 0 ? response.Data.Long() : 0;
                        if ((flags & 2) != 0) { response.Data.UInt(); response.Data.UInt(); }
                        var mode = (flags & 4) != 0 ? response.Data.UInt() : 0;
                        if ((flags & 8) != 0) { response.Data.UInt(); response.Data.UInt(); }
                        if ((flags & 0x80000000) != 0)
                        {
                            var extensions = response.Data.UInt(); if (extensions > 10000) throw new IOException("Too many attributes.");
                            for (var n = 0; n < extensions; n++) { response.Data.String(); response.Data.String(); }
                        }
                        if (name is "." or ".." || name.Contains('/') || name.Contains('\0') || rows.Count == 10000) continue;
                        var type = (mode & 0xf000) switch { 0x4000 => UnixFileType.Directory, 0x8000 => UnixFileType.RegularFile, 0xa000 => UnixFileType.SymbolicLink, _ => UnixFileType.WeirdFile };
                        rows.Add(new(path.TrimEnd('/') + "/" + name, type, (long)Math.Min(size, (ulong)long.MaxValue)));
                    }
                }
            }
            finally
            {
                if (!token.IsCancellationRequested)
                { var closed = await Request(4, handle, token); if (closed.Type != 101) throw new IOException("Expected close status."); Status(closed.Data, 0); }
            }
            return rows;
        }
    }
}


