// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// The header of a PGX file — the one-component raw format ISO/IEC 15444-4 ships its reference
/// decodes in: <c>PG ML|LM [+|-]depth width height</c> on one line, then the samples. The sign is
/// optional, and may be attached to the depth (<c>-4</c>) or stand alone (<c>+ 8</c>); both spellings
/// occur in the conformance set.
/// </summary>
public sealed class PgxHeader
{
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public bool IsSigned { get; }
    public bool BigEndian { get; }
    /// <summary>Where the samples start: the byte after the header line's newline.</summary>
    public int DataOffset { get; }

    private PgxHeader(int width, int height, int depth, bool isSigned, bool bigEndian, int dataOffset)
    {
        Width = width; Height = height; Depth = depth; IsSigned = isSigned; BigEndian = bigEndian; DataOffset = dataOffset;
    }

    public static bool TryRead(byte[] data, [NotNullWhen(true)] out PgxHeader? header)
    {
        header = null;
        int newline = Array.IndexOf(data, (byte)'\n');
        if (newline < 0) return false;
        string line = System.Text.Encoding.ASCII.GetString(data, 0, newline).TrimEnd('\r');
        string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5 || tokens[0] != "PG") return false;
        bool bigEndian;
        if (tokens[1] == "ML") bigEndian = true;
        else if (tokens[1] == "LM") bigEndian = false;
        else return false;

        int i = 2;
        bool signed = false;
        string depthToken = tokens[i];
        if (depthToken == "+" || depthToken == "-")
        {
            signed = depthToken == "-";
            depthToken = tokens[++i];
        }
        else if (depthToken.Length > 1 && (depthToken[0] == '+' || depthToken[0] == '-'))
        {
            signed = depthToken[0] == '-';
            depthToken = depthToken.Substring(1);
        }
        if (i + 2 >= tokens.Length) return false;
        if (!TryParseInt(depthToken, out int depth) || !TryParseInt(tokens[i + 1], out int width)
            || !TryParseInt(tokens[i + 2], out int height))
            return false;
        header = new PgxHeader(width, height, depth, signed, bigEndian, newline + 1);
        return true;
    }

    /// <summary>
    /// The samples, in raster order: one byte each up to 8 bits, two (in the header's byte order)
    /// above; a signed file holds two's-complement bytes or shorts, whatever its depth.
    /// </summary>
    public int[] ReadSamples(byte[] data)
    {
        int count = Width * Height;
        var samples = new int[count];
        int bytes = Depth > 8 ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            int at = DataOffset + i * bytes;
            int raw = bytes == 1 ? data[at] : BigEndian ? (data[at] << 8) | data[at + 1] : (data[at + 1] << 8) | data[at];
            if (IsSigned) raw = bytes == 1 ? (sbyte)raw : (short)raw;
            samples[i] = raw;
        }
        return samples;
    }

    private static bool TryParseInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// The header of a PNM file — greyscale <c>P5</c> or RGB <c>P6</c> with binary samples, <c>P2</c> or
/// <c>P3</c> with ASCII ones: magic, width, height and maximum value as whitespace-separated tokens,
/// with <c>#</c> comments to end of line anywhere between them. The conformance set has all four
/// kinds, opening with a copyright comment.
/// </summary>
public sealed class PnmHeader
{
    public int Width { get; }
    public int Height { get; }
    public int MaxValue { get; }
    /// <summary>1 for <c>P2</c>/<c>P5</c>, 3 for <c>P3</c>/<c>P6</c>.</summary>
    public int Channels { get; }
    /// <summary>True for <c>P2</c>/<c>P3</c>, whose samples are decimal text.</summary>
    public bool IsAscii { get; }
    /// <summary>Where the samples start: the byte after the single whitespace that ends the header.</summary>
    public int DataOffset { get; }

    private PnmHeader(int width, int height, int maxValue, int channels, bool isAscii, int dataOffset)
    {
        Width = width; Height = height; MaxValue = maxValue; Channels = channels; IsAscii = isAscii; DataOffset = dataOffset;
    }

    public static bool TryRead(byte[] data, [NotNullWhen(true)] out PnmHeader? header)
    {
        header = null;
        if (data.Length < 2 || data[0] != (byte)'P') return false;
        int channels;
        bool ascii;
        switch ((char)data[1])
        {
            case '2': channels = 1; ascii = true; break;
            case '3': channels = 3; ascii = true; break;
            case '5': channels = 1; ascii = false; break;
            case '6': channels = 3; ascii = false; break;
            default: return false;
        }

        int pos = 2;
        var values = new List<int>(3);
        while (values.Count < 3)
        {
            if (!TryReadToken(data, ref pos, out int value)) return false;
            values.Add(value);
        }
        // The header ends with exactly one whitespace byte before the samples.
        if (pos >= data.Length || !IsWhitespace(data[pos])) return false;
        header = new PnmHeader(values[0], values[1], values[2], channels, ascii, pos + 1);
        return true;
    }

    /// <summary>
    /// The samples of one channel, in raster order: bytes (or big-endian pairs when the maximum
    /// value exceeds 255) interleaved by channel for binary files, decimal tokens for ASCII ones.
    /// </summary>
    public int[] ReadSamples(byte[] data, int channel)
    {
        int count = Width * Height;
        var samples = new int[count];
        if (IsAscii)
        {
            int pos = DataOffset;
            for (int i = 0; i < count * Channels; i++)
            {
                if (!TryReadToken(data, ref pos, out int value)) throw new InvalidDataException("the PNM ends before its samples do");
                if (i % Channels == channel) samples[i / Channels] = value;
            }
            return samples;
        }
        int bytes = MaxValue > 255 ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            int at = DataOffset + (i * Channels + channel) * bytes;
            samples[i] = bytes == 1 ? data[at] : (data[at] << 8) | data[at + 1];
        }
        return samples;
    }

    private static bool TryReadToken(byte[] data, ref int pos, out int value)
    {
        value = 0;
        while (pos < data.Length)
        {
            byte b = data[pos];
            if (b == (byte)'#') { while (pos < data.Length && data[pos] != (byte)'\n') pos++; }
            else if (IsWhitespace(b)) pos++;
            else break;
        }
        int start = pos;
        while (pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9') pos++;
        if (pos == start) return false;
        return TryParseInt(System.Text.Encoding.ASCII.GetString(data, start, pos - start), out value);
    }

    private static bool IsWhitespace(byte b) => b == (byte)' ' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\t';

    private static bool TryParseInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
