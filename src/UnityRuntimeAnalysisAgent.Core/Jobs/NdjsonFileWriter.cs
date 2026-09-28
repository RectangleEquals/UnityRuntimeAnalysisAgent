using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Core.Jobs;

/// <summary>
/// Writes an NDJSON output file to a caller-chosen path: a <c>header</c> record, the records, and a <c>footer</c> with the
/// record counts (by <c>rec</c>), the SHA-256 of every preceding line and the duration. It writes to
/// <c>&lt;path&gt;.partial</c>, flushes to disk and renames on <see cref="Complete"/>; <see cref="Abort"/> (or disposing
/// before completing) deletes the partial file, so a failed or cancelled job never leaves a file that looks finished.
/// </summary>
public sealed class NdjsonFileWriter : IDisposable
{
    private static readonly byte[] Newline = { (byte)'\n' };

    private readonly string _path;
    private readonly string _partialPath;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly SortedDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _redactions = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _finished;

    /// <summary>Starts a file (and writes its header).</summary>
    /// <param name="path">The final path, chosen by the caller (absolute).</param>
    /// <param name="schema">The file schema name (e.g. <c>survey</c>).</param>
    /// <param name="schemaVersion">The file schema's version.</param>
    /// <param name="agentVersion">The agent's version.</param>
    /// <param name="source">What the file was made from.</param>
    public NdjsonFileWriter(string path, string schema, string schemaVersion, string agentVersion, JsonObject? source = null)
    {
        if (!Path.IsPathRooted(path))
        {
            throw ProtocolException.InvalidParams("params.outFile", "outFile must be an absolute path.");
        }

        _path = path;
        _partialPath = path + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _stream = new FileStream(_partialPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException(ErrorCodes.IoFailed, $"Can't write {path}: {e.Message}", null, e);
        }

        var header = new JsonObject();
        header.Add("rec", new JsonString("header"));
        header.Add("schema", new JsonString(schema));
        header.Add("schemaVersion", new JsonString(schemaVersion));
        header.Add("agentVersion", new JsonString(agentVersion));
        header.Add("createdAt", new JsonString(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
        if (source is not null)
        {
            header.Add("source", source);
        }

        WriteLine(header, hashed: true);
    }

    /// <summary>Records written so far, by <c>rec</c>.</summary>
    public IReadOnlyDictionary<string, long> Counts => _counts;

    /// <summary>Writes a record (it needs a string <c>rec</c>).</summary>
    public void Write(JsonObject record)
    {
        if (record["rec"] is not JsonString rec || rec.Value.Length == 0 || rec.Value is "header" or "footer")
        {
            throw new ArgumentException("A record needs a string 'rec' other than header and footer.", nameof(record));
        }

        WriteLine(record, hashed: true);
        _counts[rec.Value] = (_counts.TryGetValue(rec.Value, out var n) ? n : 0) + 1;
    }

    /// <summary>Counts a redaction stub written inside a record (reported in the footer, by reason).</summary>
    public void CountRedaction(string reason) => _redactions[reason] = (_redactions.TryGetValue(reason, out var n) ? n : 0) + 1;

    /// <summary>Writes the footer, flushes to disk and renames the file into place. Returns the file's description.</summary>
    public OutputFile Complete()
    {
        var sha256 = ToHex(_hash.GetHashAndReset());
        var counts = new JsonObject();
        foreach (var pair in _counts)
        {
            counts.Add(pair.Key, new JsonNumber(pair.Value));
        }

        var footer = new JsonObject();
        footer.Add("rec", new JsonString("footer"));
        footer.Add("counts", counts);
        footer.Add("sha256", new JsonString(sha256));
        footer.Add("durationMs", new JsonNumber(_clock.ElapsedMilliseconds));
        if (_redactions.Count > 0)
        {
            var redactions = new JsonObject();
            foreach (var pair in _redactions)
            {
                redactions.Add(pair.Key, new JsonNumber(pair.Value));
            }

            footer.Add("redactions", redactions);
        }

        try
        {
            WriteLine(footer, hashed: false);
            _stream.Flush(true);
            var bytes = _stream.Length;
            _stream.Dispose();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            File.Move(_partialPath, _path);
            _finished = true;
            return new OutputFile { Path = _path, Bytes = bytes, Sha256 = sha256, Counts = counts };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Abort();
            throw new ProtocolException(ErrorCodes.IoFailed, $"Can't finish {_path}: {e.Message}", null, e);
        }
    }

    /// <summary>Deletes the partial file (on failure or cancellation).</summary>
    public void Abort()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _stream.Dispose();
        try
        {
            File.Delete(_partialPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Abort();
        _hash.Dispose();
    }

    private void WriteLine(JsonObject record, bool hashed)
    {
        var bytes = record.ToUtf8Bytes();
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Write(Newline, 0, 1);
        if (hashed)
        {
            _hash.AppendData(bytes);
            _hash.AppendData(Newline);
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
