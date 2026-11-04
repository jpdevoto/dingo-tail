// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;

namespace DingoTail;

internal sealed class LineDecoder(Encoding encoding)
{
    private readonly byte[] bytes = new byte[encoding == Encoding.UTF8 ? LogBuffer.MaxLineBytes : LogBuffer.MaxLineBytes * 2];
    private int count;
    private int firstByte = -1;
    private bool truncated;

    public void Feed(ReadOnlySpan<byte> input, Action<string> emit)
    {
        foreach (var value in input)
        {
            if (encoding == Encoding.UTF8)
            {
                if (value == 10)
                    Finish(emit);
                else if (count < bytes.Length)
                    bytes[count++] = value;
                else
                    truncated = true;
            }
            else if (firstByte < 0)
            {
                firstByte = value;
            }
            else
            {
                var unit = encoding == Encoding.Unicode ? firstByte | (value << 8) : (firstByte << 8) | value;
                if (unit == 10)
                    Finish(emit);
                else if (count < bytes.Length)
                {
                    bytes[count++] = (byte)firstByte;
                    bytes[count++] = value;
                }
                else
                    truncated = true;
                firstByte = -1;
            }
        }
    }

    private void Finish(Action<string> emit)
    {
        var line = encoding.GetString(bytes, 0, count).TrimEnd('\r');
        if (truncated)
            line += " [line truncated]";
        if (line.Contains('\uFFFD'))
            line += " [decoding replacement]";
        count = 0;
        truncated = false;
        emit(line);
    }
}
