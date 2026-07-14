using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeMcp;

[McpServerToolType]
internal static class FsTools
{
    public static string Root = "";
    public const long MaxReadBytes = 8_192;
    public const int MaxTreeEntries = 500;

    static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    internal static string Ser(object o) => JsonSerializer.Serialize(o, J);
    internal static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Single exception boundary: McpException passes through; expected BCL failures
    // convert with their message; unexpected exceptions propagate loudly.
    internal static string Guarded(Func<string> body)
    {
        try { return body(); }
        catch (McpException) { throw; }
        catch (FileNotFoundException e) { throw new McpException($"not found: {e.Message}"); }
        catch (DirectoryNotFoundException e) { throw new McpException($"not found: {e.Message}"); }
        catch (UnauthorizedAccessException e) { throw new McpException(e.Message); }
        catch (JsonException e) { throw new McpException($"corrupt metadata: {e.Message}"); }
        catch (IOException e) { throw new McpException(e.Message); }
    }

    internal static string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.Equals(Root, PathCmp) && !full.StartsWith(rootWithSep, PathCmp))
            throw new McpException($"path escapes root: {relativePath}");
        FileSystemInfo info =
            Directory.Exists(full) ? new DirectoryInfo(full) :
            File.Exists(full) ? new FileInfo(full) : null;
        var linkTarget = info?.ResolveLinkTarget(true)?.FullName;
        if (linkTarget is not null && !linkTarget.Equals(Root, PathCmp) &&
            !linkTarget.StartsWith(rootWithSep, PathCmp))
            throw new McpException($"path resolves via symlink outside root: {relativePath}");
        return full;
    }

    static void Sync(string rel, bool isDir = false) { }

    internal static void AtomicWrite(string full, string content)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, full, overwrite: true);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    static bool LooksBinary(string path)
    {
        using var s = File.OpenRead(path);
        var buf = new byte[Math.Min(MaxReadBytes, s.Length)];
        var read = s.Read(buf, 0, buf.Length);
        return buf.AsSpan(0, read).Contains((byte)0);
    }

    static FileStream OpenText(string full, string rel)
    {
        FileStream s;
        try { s = File.OpenRead(full); }
        catch (FileNotFoundException) { throw new McpException($"not found: {rel}"); }
        catch (DirectoryNotFoundException) { throw new McpException($"not found: {rel}"); }
        var probe = new byte[(int)Math.Min(MaxReadBytes, s.Length)];
        var read = s.Read(probe, 0, probe.Length);
        if (probe.AsSpan(0, read).Contains((byte)0))
        {
            s.Dispose();
            throw new McpException($"'{rel}' looks binary; refusing text read. Use Stat to inspect.");
        }
        s.Seek(0, SeekOrigin.Begin);
        return s;
    }

    static FileStream OpenExisting(string full, string rel) =>
        File.Exists(full)
            ? new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : throw new McpException($"not found: {rel} (use Write to create)");

    static void CheckOffset(FileStream s, long offset)
    {
        if (offset < 0 || offset > s.Length)
            throw new McpException($"offset {offset} outside [0, {s.Length}]");
        if (offset == 0 || offset == s.Length) return;
        s.Seek(offset, SeekOrigin.Begin);
        var b = s.ReadByte();
        if (b != -1 && (b & 0xC0) == 0x80)
            throw new McpException($"offset {offset} splits a UTF-8 sequence; Read's returnedBytes values are aligned");
    }

    static void CopyBytes(Stream from, Stream to, long count)
    {
        var buf = new byte[81920];
        while (count > 0)
        {
            var n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n == 0) throw new McpException("file shrank during insert");
            to.Write(buf, 0, n);
            count -= n;
        }
    }

    // ── inspect ──

    [McpServerTool(Name = "file_info", Title = "Check Path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     System.ComponentModel.Description(
        "Check whether a path exists and what it is, before Read/Write/Delete. " +
        "Returns exists:false rather than throwing if absent.")]
    public static string Stat(string relativePath) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        if (Directory.Exists(full))
            return Ser(new { exists = true, isDir = true, isFile = false });
        if (File.Exists(full))
        {
            var fi = new FileInfo(full);
            return Ser(new
            {
                exists = true,
                isDir = false,
                isFile = true,
                bytes = fi.Length,
                modifiedUtc = fi.LastWriteTimeUtc,
                binary = LooksBinary(full),
                readableWithoutTruncation = fi.Length <= MaxReadBytes
            });
        }
        return Ser(new { exists = false });
    });

    [McpServerTool(Name = "directory_list_files", Title = "List Directory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
    System.ComponentModel.Description(
       "List files and directories under a relative path (non-recursive). " +
       "Errors if missing or not a directory — Stat first if unsure.")]
    public static string List(string relativePath = ".") => Guarded(() =>
    {
        var full = Resolve(relativePath);
        if (!Directory.Exists(full)) throw new McpException($"not a directory: {relativePath}");
        var entries = Directory.GetFileSystemEntries(full)
            .Where(e => !Path.GetFileName(e).Equals(".trash", PathCmp))
            .Select(e => new { name = Path.GetFileName(e), isDir = Directory.Exists(e) })
            .ToArray();
        return Ser(new { path = relativePath, entries });
    });

    [McpServerTool(Name = "directory_list_recursive", Title = "List Directory Tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
    System.ComponentModel.Description(
       "Recursively list files, optionally filtered by extension; returns maxTreeEntries per call, " +
       "hasMore:true with nextOffset for paging.")]
    public static string Tree(string relativePath = ".", string extension = null, int offset = 0) => Guarded(() =>
    {
        if (offset < 0) throw new McpException("offset must be >= 0");
        var full = Resolve(relativePath);
        if (!Directory.Exists(full)) throw new McpException($"not a directory: {relativePath}");
        var trashPrefix = TrashDir + Path.DirectorySeparatorChar;
        var page = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(trashPrefix, PathCmp))
            .Where(f => extension is null || f.EndsWith(extension, PathCmp))
            .Skip(offset)
            .Take(MaxTreeEntries + 1)          // +1 = cheap hasMore probe, no full count
            .Select(f => Path.GetRelativePath(Root, f))
            .ToArray();
        var hasMore = page.Length > MaxTreeEntries;
        var files = hasMore ? page[..MaxTreeEntries] : page;
        return Ser(new
        {
            count = files.Length,
            files,
            offset,
            hasMore,
            nextOffset = hasMore ? offset + files.Length : -1
        });
    });

    // ── read ──

    [McpServerTool(Name = "file_read", Title = "Read File", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     System.ComponentModel.Description(
        "Read a whole file as UTF-8 text. Refuses binary (NUL bytes) and files over maxReadBytes — " +
        "Stat.readableWithoutTruncation predicts which; use ReadRange to page larger files.")]
    public static string Read(string relativePath) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        using var s = OpenText(full, relativePath);
        if (s.Length > MaxReadBytes)
            throw new McpException($"'{relativePath}' is {s.Length} bytes (max {MaxReadBytes}); use ReadRange to page");
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return Ser(new { content = r.ReadToEnd(), totalBytes = s.Length });
    });

    [McpServerTool(Name = "file_range_read", Title = "Read File Range", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
   System.ComponentModel.Description(
      "Read a byte range of a UTF-8 file (length -1 = one max-size chunk from offset). Page by " +
      "resuming at offset+returnedBytes until truncated:false. Refuses binary; errs offset>size.")]
    public static string ReadRange(string relativePath, long offset = 0, long length = -1) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        using var s = OpenText(full, relativePath);
        var total = s.Length;
        if ((ulong)offset > (ulong)total)
            throw new McpException($"offset {offset} outside [0, {total}]");
        var eff = length == -1 ? Math.Min(MaxReadBytes, total - offset) : length;
        var buf = new byte[Math.Max(0, Math.Min(eff, total - offset))];
        s.Seek(offset, SeekOrigin.Begin);
        var n = s.Read(buf, 0, buf.Length);
        return Ser(new
        {
            content = System.Text.Encoding.UTF8.GetString(buf, 0, n),
            truncated = offset + n < total,
            totalBytes = total,
            offset,
            returnedBytes = n
        });
    });

    // ── write ──

    [McpServerTool(Name = "file_create", Title = "Create New File", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     System.ComponentModel.Description(
        "CREATE a NEW file only — refuses to run if the file already exists (reports current size in " +
        "the error) and creates parent directories as needed. Write never clobbers, by design. " +
        "To modify an EXISTING file instead: Append, Patch, Insert, or MoveReplace.")]
    public static string Write(string relativePath, string content) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using (var s = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(s)) w.Write(content);
        }
        catch (IOException) when (File.Exists(full))
        {
            throw new McpException(
                $"'{relativePath}' already exists ({new FileInfo(full).Length} bytes); use Append/Patch/Insert");
        }
        Sync(relativePath);
        return Ser(new { written = relativePath, bytes = System.Text.Encoding.UTF8.GetByteCount(content) });
    });

    [McpServerTool(Name = "file_append", Title = "Append to File", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     System.ComponentModel.Description(
        "ADD text to the END of an EXISTING file only — errors if the file doesn't exist yet (use " +
        "Write to create it first). Nothing before the existing content is touched. NOT the right " +
        "tool for changing the middle of a file — use Patch or Insert for that instead.")]
    public static string Append(string relativePath, string content) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using (var s = OpenExisting(full, relativePath))
        {
            s.Seek(s.Length, SeekOrigin.Begin);
            s.Write(bytes);
        }
        Sync(relativePath);
        return Ser(new { written = relativePath, bytes = bytes.Length });
    });

    [McpServerTool(Name = "file_replace_at", Title = "Patch File Bytes", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
    System.ComponentModel.Description(
       "OVERWRITE bytes starting at a byte offset, REPLACING content there in place — content after " +
       "the patched region survives unless you also call Truncate. Use Insert instead if you want " +
       "to splice in content without overwriting anything. Errors: offset>size, mid-UTF-8.")]
    public static string Patch(string relativePath, string content, long offset) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        long total;
        using (var s = OpenExisting(full, relativePath))
        {
            CheckOffset(s, offset);
            s.Seek(offset, SeekOrigin.Begin);
            s.Write(bytes);
            total = s.Length;
        }
        Sync(relativePath);
        return Ser(new { written = relativePath, offset, bytes = bytes.Length, totalBytes = total });
    });

    [McpServerTool(Name = "file_truncate", Title = "Truncate File", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     System.ComponentModel.Description(
        "Cut a file down to a byte length, discarding everything after — the 'set new end of file' " +
        "step of a manual rewrite. Writes nothing itself, only removes content past the given length. " +
        "Errors: length>size, mid-UTF-8.")]
    public static string Truncate(string relativePath, long length) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        long removed;
        using (var s = OpenExisting(full, relativePath))
        {
            CheckOffset(s, length);
            removed = s.Length - length;
            s.SetLength(length);
        }
        Sync(relativePath);
        return Ser(new { truncated = relativePath, totalBytes = length, bytesRemoved = removed });
    });

    [McpServerTool(Name = "file_insert", Title = "Insert Text into File", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
    System.ComponentModel.Description(
       "SPLICE text IN at a byte offset, SHIFTING everything after it to make room — nothing existing " +
       "is overwritten, the file grows. Use Patch instead if you want to REPLACE content at an offset " +
       "rather than push it aside. Atomic via temp file. Errors: offset>size, mid-UTF-8.")]
    public static string Insert(string relativePath, string content, long offset) => Guarded(() =>
    {
        var full = Resolve(relativePath);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        var s = OpenExisting(full, relativePath);
        try
        {
            CheckOffset(s, offset);
            var total = s.Length;
            using (var t = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                s.Seek(0, SeekOrigin.Begin);
                CopyBytes(s, t, offset);
                t.Write(bytes);
                s.CopyTo(t);
            }
            s.Dispose();
            File.Move(tmp, full, overwrite: true);
            Sync(relativePath);
            return Ser(new { written = relativePath, offset, bytes = bytes.Length, totalBytes = total + bytes.Length });
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
        finally { s.Dispose(); }
    });

    // ── delete / restore / move ──

    record TrashMeta(string originalPath, bool isDir, DateTime deletedUtc);
    static string TrashDir => Path.Combine(Root, ".trash");

    static (string full, bool isDir) ResolveExisting(string rel)
    {
        var full = Resolve(rel);
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full)) throw new McpException($"not found: {rel}");
        return (full, isDir);
    }

    static void RejectTrashPath(string full)
    {
        if (full.StartsWith(TrashDir + Path.DirectorySeparatorChar, PathCmp) || full.Equals(TrashDir, PathCmp))
            throw new McpException(".trash is managed storage; use Restore instead");
    }

    [McpServerTool(Name = "file_trash", Title = "Trash (Recoverable Delete)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
    System.ComponentModel.Description(
       "RECOVERABLY delete a file or directory — moves it into .trash/, recording its original path " +
       "so Restore(trashId) can undo it. This is the DEFAULT delete tool — prefer this over " +
       "DeletePermanent unless you specifically need the space reclaimed right now.")]
    public static string Trash(string relativePath) => Guarded(() =>
    {
        var (full, isDir) = ResolveExisting(relativePath);
        RejectTrashPath(full);
        Directory.CreateDirectory(TrashDir);
        var trashId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        var target = Path.Combine(TrashDir, trashId);
        try { if (isDir) Directory.Move(full, target); else File.Move(full, target); }
        catch (IOException)
        { throw new McpException($"could not trash '{relativePath}'; it may be locked or on another volume"); }
        File.WriteAllText(target + ".meta.json", Ser(new TrashMeta(relativePath, isDir, DateTime.UtcNow)));
        Sync(relativePath, isDir);
        return Ser(new { deleted = relativePath, trashId, undo = $"Restore(\"{trashId}\")" });
    });

    [McpServerTool(Name = "file_delete", Title = "Permanently Delete", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
    System.ComponentModel.Description(
       "IRREVERSIBLY delete a file or directory (recursive) — no trash, no undo, cannot be recovered. " +
       "Use Trash instead unless you specifically need this to be permanent right now.")]
    public static string DeletePermanent(string relativePath) => Guarded(() =>
    {
        var (full, isDir) = ResolveExisting(relativePath);
        RejectTrashPath(full);
        if (isDir) Directory.Delete(full, recursive: true); else File.Delete(full);
        Sync(relativePath, isDir);
        return Ser(new { deleted = relativePath, permanent = true });
    });

    static string RestoreCore(string trashId, string destRelOverride)
    {
        if (trashId != Path.GetFileName(trashId) || trashId.Contains(".."))
            throw new McpException("trashId must be a bare id from Trash's result");
        var src = Path.Combine(TrashDir, trashId);
        var metaPath = src + ".meta.json";
        var isDir = Directory.Exists(src);
        if (!isDir && !File.Exists(src)) throw new McpException($"no trash entry '{trashId}'");
        var destRel = destRelOverride
            ?? (File.Exists(metaPath)
                ? JsonSerializer.Deserialize<TrashMeta>(File.ReadAllText(metaPath))?.originalPath
                : null)
            ?? throw new McpException("entry has no manifest; use RestoreTo");
        var dest = Resolve(destRel);
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new McpException($"'{destRel}' exists; restore destination must be free");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (isDir) Directory.Move(src, dest); else File.Move(src, dest);
        if (File.Exists(metaPath)) File.Delete(metaPath);
        Sync(destRel, isDir);
        return Ser(new { restored = destRel, wasTrashId = trashId });
    }

    [McpServerTool(Name = "file_trash_restore", Title = "Restore from Trash", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
   System.ComponentModel.Description(
      "Undo a Trash: restore the entry to its recorded original path. Fails if that path exists — " +
      "use RestoreTo for a different destination.")]
    public static string Restore(string trashId) => Guarded(() => RestoreCore(trashId, null));

    [McpServerTool(Name = "file_trash_restore_to", Title = "Restore from Trash to Path", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
    System.ComponentModel.Description(
       "Restore a trash entry to an explicit path instead of its original. Fails if it exists.")]
    public static string RestoreTo(string trashId, string destinationRelativePath) =>
        Guarded(() => RestoreCore(trashId, destinationRelativePath));

    static string MoveCore(string fromRel, string toRel, bool overwrite)
    {
        var from = Resolve(fromRel);
        var to = Resolve(toRel);
        RejectTrashPath(from); RejectTrashPath(to);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try { File.Move(from, to, overwrite); }
        catch (FileNotFoundException) { throw new McpException($"not found: {fromRel}"); }
        catch (IOException) when (!overwrite && File.Exists(to))
        { throw new McpException($"'{toRel}' already exists; use MoveReplace"); }
        Sync(fromRel); Sync(toRel);
        return Ser(new { from = fromRel, to = toRel });
    }

    [McpServerTool(Name = "file_move", Title = "Move/Rename File", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
    System.ComponentModel.Description(
       "Move or rename a file within root. Fails if the destination exists — use MoveReplace.")]
    public static string Move(string fromRelativePath, string toRelativePath) =>
        Guarded(() => MoveCore(fromRelativePath, toRelativePath, false));

    [McpServerTool(Name = "file_move_replace", Title = "Move/Rename File (Replace)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     System.ComponentModel.Description(
        "Move or rename a file within root, REPLACING the destination if it already exists. " +
        "Use Move instead if you want it to fail on a collision rather than clobber.")]
    public static string MoveReplace(string fromRelativePath, string toRelativePath) =>
        Guarded(() => MoveCore(fromRelativePath, toRelativePath, true));
}