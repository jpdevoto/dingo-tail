// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DingoTail;

internal static class InitialLineCount
{
    public const int Minimum = 1;
    public const int Maximum = LogBuffer.MaxRows;
    public const int Default = 200;

    public static void Validate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, Minimum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Maximum);
    }
}

internal sealed class InitialLineCountJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException("initial_lines must be an integer.");

        var count = reader.GetDouble();
        if (count < InitialLineCount.Minimum)
            return InitialLineCount.Minimum;
        if (count > InitialLineCount.Maximum)
            return InitialLineCount.Maximum;
        if (count != Math.Truncate(count))
            throw new JsonException("initial_lines must be an integer.");
        return (int)count;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
