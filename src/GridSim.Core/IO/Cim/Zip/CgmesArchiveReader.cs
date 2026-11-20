using System.IO.Compression;
using System.Text;
using System.Xml;
using GridSim.Core.IO.Cim.Profiles;

namespace GridSim.Core.IO.Cim.Zip;

/// <summary>One profile document read from a CGMES archive (or directory).</summary>
/// <param name="Path">The entry path, "/"-separated; a document inside a nested zip is "outer.zip/inner.xml".</param>
public sealed record CgmesArchiveEntry(string Path, CgmesProfileKind Kind, IReadOnlyList<string> ProfileUris, string Text, byte[] Bytes);

/// <summary>The documents of one archive in the order they are processed: by kind (EQ, TP, SSH, SV, ...) then path (ordinal).</summary>
public sealed record CgmesArchive(IReadOnlyList<CgmesArchiveEntry> Entries, IReadOnlyList<string> Skipped)
{
    public IEnumerable<CgmesArchiveEntry> Of(CgmesProfileKind k) => Entries.Where(e => e.Kind == k);
}

/// <summary>
/// RC4 R4-11 part B: reads the profile files of a CGMES exchange from a ZIP in SORTED entry order (ordinal, so the result
/// never depends on the order the archive was written in). It never extracts to disk. Refused, naming the entry: a path
/// that is rooted, has a drive, or contains a ".." segment (path traversal); two entries with the same path; two documents
/// of one profile kind (a duplicate profile entry); a document with a DTD; a document that is not well-formed XML;
/// an entry that expands beyond <see cref="MaxEntryBytes"/>, an archive with more than <see cref="MaxEntries"/> entries
/// (zip-bomb limits). One level of nested zip (each profile zipped on its own, as the CGMES exchange does) is read; deeper
/// nesting is refused. Entries that are not .xml (or .zip) are listed in <see cref="CgmesArchive.Skipped"/>.
/// </summary>
public static class CgmesArchiveReader
{
    public const int MaxEntries = 10000;
    public const long MaxEntryBytes = 512L * 1024 * 1024;

    public static CgmesArchive ReadFile(string zipPath)
    {
        using FileStream fs = File.OpenRead(zipPath);
        return Read(fs);
    }

    /// <summary>Read a directory the same way (its .xml files, sorted by relative path), for the unzipped form of a bundle.</summary>
    public static CgmesArchive ReadDirectory(string dir)
    {
        var docs = new List<(string Path, byte[] Bytes)>();
        var skipped = new List<string>();
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => Path.GetRelativePath(dir, x).Replace('\\', '/'), StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) docs.Add((rel, File.ReadAllBytes(f)));
            else skipped.Add(rel);
        }
        return Build(docs, skipped);
    }

    public static CgmesArchive Read(Stream zip)
    {
        var docs = new List<(string Path, byte[] Bytes)>();
        var skipped = new List<string>();
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        Collect(archive, "", 0, docs, skipped);
        return Build(docs, skipped);
    }

    private static void Collect(ZipArchive archive, string prefix, int depth, List<(string, byte[])> docs, List<string> skipped)
    {
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException($"CGMES archive '{prefix}' has {archive.Entries.Count} entries (limit {MaxEntries}).");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Sorted ordinal by the normalised name: the order in the central directory plays no part.
        foreach (ZipArchiveEntry e in archive.Entries.OrderBy(x => x.FullName.Replace('\\', '/'), StringComparer.Ordinal))
        {
            string name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;   // a directory entry
            CheckPath(name);
            if (!seen.Add(name)) throw new InvalidDataException($"CGMES archive: entry '{prefix}{name}' appears twice.");
            string full = prefix + name;
            if (e.Length > MaxEntryBytes) throw new InvalidDataException($"CGMES archive: entry '{full}' is {e.Length} bytes (limit {MaxEntryBytes}).");
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (depth >= 1) throw new InvalidDataException($"CGMES archive: '{full}' is a zip inside a nested zip; only one level of nesting is read.");
                using Stream inner = Bounded(e, full);
                using var ms = new MemoryStream();
                inner.CopyTo(ms);
                ms.Position = 0;
                using var nested = new ZipArchive(ms, ZipArchiveMode.Read);
                Collect(nested, full + "/", depth + 1, docs, skipped);
            }
            else if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                using Stream s = Bounded(e, full);
                using var buffer = new MemoryStream();
                s.CopyTo(buffer);
                docs.Add((full, buffer.ToArray()));
            }
            else skipped.Add(full);
        }
    }

    private static Stream Bounded(ZipArchiveEntry e, string full)
    {
        // The declared length is attacker-controlled: read through a limiter as well.
        return new LimitedStream(e.Open(), MaxEntryBytes, full);
    }

    private static void CheckPath(string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || (name.Length > 1 && name[1] == ':') || name.Contains('\0'))
            throw new InvalidDataException($"CGMES archive: entry '{name}' has a rooted or invalid path (refused).");
        foreach (string seg in name.Split('/'))
            if (seg == "..") throw new InvalidDataException($"CGMES archive: entry '{name}' contains a '..' segment (path traversal, refused).");
    }

    private static CgmesArchive Build(List<(string Path, byte[] Bytes)> docs, List<string> skipped)
    {
        var entries = new List<CgmesArchiveEntry>();
        foreach ((string path, byte[] bytes) in docs)
        {
            string text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
            IReadOnlyList<string> uris = ProfileUrisOf(text, path);
            CgmesProfileKind kind = CgmesVocabulary.KindOf(uris) ?? CgmesVocabulary.KindOfFileName(path);
            entries.Add(new CgmesArchiveEntry(path, kind, uris, text, bytes));
        }
        foreach (var g in entries.Where(e => e.Kind != CgmesProfileKind.Unknown).GroupBy(e => e.Kind))
            if (g.Count() > 1)
                throw new InvalidDataException($"CGMES archive: duplicate profile entries for {g.Key}: {string.Join(", ", g.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal))}.");
        var ordered = entries.OrderBy(e => e.Kind == CgmesProfileKind.Unknown ? int.MaxValue : (int)e.Kind)
            .ThenBy(e => e.Path, StringComparer.Ordinal).ToList();
        return new CgmesArchive(ordered, skipped.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    /// <summary>The md:Model.profile URIs of a document, read with DTD processing prohibited.</summary>
    private static IReadOnlyList<string> ProfileUrisOf(string xml, string path)
    {
        var uris = new List<string>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        try
        {
            using var sr = new StringReader(xml);
            using var r = XmlReader.Create(sr, settings);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Model.profile") uris.Add(r.ReadElementContentAsString().Trim());
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"CGMES archive: '{path}' is not a well-formed XML document without a DTD: {ex.Message}");
        }
        return uris;
    }

    private sealed class LimitedStream(Stream inner, long limit, string name) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > limit) throw new InvalidDataException($"CGMES archive: entry '{name}' expands beyond {limit} bytes (refused).");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
