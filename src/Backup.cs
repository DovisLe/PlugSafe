using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PlugSafe;

/// <summary>A safety rule would be broken: the whole backup stops.</summary>
sealed class SafetyException(string message) : Exception(message);

/// <summary>One file can't be backed up right now: it's skipped and the rest carry on.</summary>
sealed class SkipException(string message) : Exception(message);

/// <summary>
/// Safety layer 2: every path the backup writes, renames or removes goes through <see cref="Check"/>.
/// Nothing may land outside the backup folder, inside an original folder, behind a link to somewhere else,
/// or on a drive that isn't the one we started with.
/// </summary>
sealed class TargetGuard
{
    readonly string root, volumeRoot;
    readonly uint serial;
    readonly List<string> sources;
    readonly HashSet<string> safeDirs = new(StringComparer.OrdinalIgnoreCase);
    long lastSerialCheck;

    public TargetGuard(string root, uint serial, IEnumerable<string> sources)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        volumeRoot = Path.GetPathRoot(this.root)!;
        this.serial = serial;
        this.sources = sources.Select(s => Path.TrimEndingDirectorySeparator(Path.GetFullPath(s))).ToList();
        if (this.root.Length <= volumeRoot.Length) throw new SafetyException("The backup folder can't be a whole drive.");
        foreach (var source in this.sources)
            if (IsInside(this.root, source) || IsInside(source, this.root))
                throw new SafetyException($"The backup folder and \"{source}\" overlap. Refusing to run.");
    }

    public string Root => root;

    public static bool IsInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.EndsWith('\\') ? folder : folder + "\\", StringComparison.OrdinalIgnoreCase);

    public bool DriveStillThere() => Native.VolumeSerial(volumeRoot) == serial;

    public string Check(string path)
    {
        string full = Path.GetFullPath(path);
        if (!IsInside(full, root))
            throw new SafetyException($"Blocked a write outside the backup folder: {full}");
        foreach (var source in sources)
            if (IsInside(full, source)) throw new SafetyException($"Blocked a write into your original files: {full}");

        long now = Environment.TickCount64;
        if (now - lastSerialCheck > 1000)
        {
            if (!DriveStillThere()) throw new SafetyException("The backup drive was removed or swapped, so the backup stopped.");
            lastSerialCheck = now;
        }

        // No junctions / symbolic links between the drive root and the target: they could lead to another drive.
        for (string? dir = Path.GetDirectoryName(full); dir != null && dir.Length > volumeRoot.Length; dir = Path.GetDirectoryName(dir))
        {
            if (safeDirs.Contains(dir)) break; // checked earlier, together with everything above it
            var info = new DirectoryInfo(dir);
            if (!info.Exists) continue;        // we'll create it ourselves, as a plain folder
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new SafetyException($"Blocked: \"{dir}\" is a link to somewhere else.");
            safeDirs.Add(dir);
        }
        var target = new FileInfo(full);
        if (target.Exists && target.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new SafetyException($"Blocked: \"{full}\" is a link to somewhere else.");
        return full;
    }
}

/// <summary>Walks a folder tree without following links, skipping Windows system files and folders on the skip list.</summary>
static class FileWalker
{
    public static readonly HashSet<string> NoSkips = new();

    public static IEnumerable<FileInfo> Files(DirectoryInfo root, HashSet<string> skipFolders, CancellationToken ct = default)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry is FileInfo file) yield return file;
                else if (entry is DirectoryInfo sub && !skipFolders.Contains(sub.Name)) pending.Push(sub);
            }
        }
    }
}

enum FileStatus { Waiting, Copying, Added, Updated, Versioned, Skipped }

/// <summary>Overall progress, plus (when <see cref="Index"/> ≥ 0) the state of one file in the scan's Changed list.</summary>
readonly record struct BackupProgress(int Done, int Total, long Bytes, long TotalBytes, string File,
    int Index = -1, long FileBytes = 0, FileStatus Status = FileStatus.Copying, string? Reason = null);

/// <param name="ItemPath">The chosen file/folder this file belongs to (for per-item progress in the window).</param>
sealed record WorkItem(string Source, string Target, long Size, string ItemPath = "");

sealed class ScanResult
{
    public List<WorkItem> Changed { get; } = new();
    public int Total, Unchanged, New;
    public long ChangedBytes;
    public List<string> Problems { get; } = new();
}

sealed class BackupResult
{
    public int Added, Updated, VersionsKept, Unchanged, Skipped;
    public long Bytes;
    public List<string> Problems { get; } = new();
    public string? StoppedBecause;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added > 0) parts.Add($"{Added:N0} new");
            if (Updated > 0) parts.Add($"{Updated:N0} updated");
            if (VersionsKept > 0) parts.Add($"{VersionsKept:N0} older version{(VersionsKept == 1 ? "" : "s")} kept");
            if (Skipped > 0) parts.Add($"{Skipped:N0} skipped");
            if (parts.Count == 0) parts.Add("nothing changed");
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// Copies the chosen files to the backup drive. Layout on the drive:
///   PlugSafe\&lt;PC&gt;\Latest\...    newest copy of everything (open it on any computer)
///   PlugSafe\&lt;PC&gt;\Versions\&lt;day&gt;\...  the previous copy, when a file changes on a later day
///   PlugSafe\&lt;PC&gt;\Versions\&lt;day time&gt;\...  extra copy when a file changes massively within one day
/// Originals are only ever opened for reading. Backups are never deleted; today's copy of a file is only replaced
/// by a newer copy of the same file, and only after that newer copy was read back and verified.
/// </summary>
sealed class BackupEngine
{
    public const string TempSuffix = ".plugsafe-tmp", OldSuffix = ".plugsafe-old";
    const int BufferSize = 1 << 20;
    const long Fat32Max = uint.MaxValue;
    const FileOptions NoBuffering = (FileOptions)0x20000000;

    enum Outcome { Added, Updated, Versioned }

    readonly TargetGuard guard;
    readonly string computerRoot, latest, versions, volumeRoot;
    readonly IReadOnlyList<BackupItem> items;
    readonly HashSet<string> skipFolders;
    readonly bool fat32;
    readonly DateTime started = DateTime.Now;
    readonly byte[] buffer = new byte[BufferSize];
    readonly byte[] alignedBuffer = GC.AllocateArray<byte>(BufferSize + 4096, pinned: true);

    /// <param name="driveFolder">The PlugSafe folder on the backup drive; nothing is ever written outside it.</param>
    /// <param name="computer">This PC's folder inside it, so several computers can share one drive.</param>
    public BackupEngine(string driveFolder, string computer, uint serial, IReadOnlyList<BackupItem> items, bool fat32,
        HashSet<string>? skipFolders = null)
    {
        this.skipFolders = skipFolders ?? FileWalker.NoSkips;
        guard = new TargetGuard(driveFolder, serial, items.Select(i => i.Path));
        computerRoot = Path.Combine(guard.Root, computer);
        latest = Path.Combine(computerRoot, "Latest");
        versions = Path.Combine(computerRoot, "Versions");
        volumeRoot = Path.GetPathRoot(guard.Root)!;
        this.items = items;
        this.fat32 = fat32;
    }

    public string ComputerFolder => computerRoot;

    // ---------------------------------------------------------------- scan

    /// <summary>Finds what changed since the last backup. Only reads file names, sizes and dates; nothing is copied yet.</summary>
    public ScanResult Scan(CancellationToken ct)
    {
        var scan = new ScanResult();
        int cloudOnly = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsFolder)
            {
                var folder = new DirectoryInfo(item.Path);
                if (!folder.Exists) { scan.Problems.Add($"Folder not found: {item.Path}"); continue; }
                // Doesn't follow folder links (they can loop or point elsewhere) and skips folders on the skip list.
                foreach (var file in FileWalker.Files(folder, skipFolders, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsJunk(file.Name)) continue;
                    if (IsCloudOnly(file)) { cloudOnly++; continue; }
                    Consider(scan, file, Path.Combine(latest, item.Name, Path.GetRelativePath(folder.FullName, file.FullName)), item.Path);
                }
            }
            else
            {
                var file = new FileInfo(item.Path);
                if (!file.Exists) { scan.Problems.Add($"File not found: {item.Path}"); continue; }
                if (IsCloudOnly(file)) { cloudOnly++; continue; }
                Consider(scan, file, Path.Combine(latest, item.Name), item.Path);
            }
        }
        if (cloudOnly > 0)
            scan.Problems.Add($"{cloudOnly:N0} OneDrive file(s) are online-only and were skipped. Set them to \"Always keep on this device\" to back them up.");
        return scan;
    }

    static void Consider(ScanResult scan, FileInfo source, string target, string itemPath)
    {
        scan.Total++;
        var copy = new FileInfo(target);
        // Same size and same modified time (FAT drives store it in 2-second steps) = already backed up.
        if (copy.Exists && copy.Length == source.Length &&
            Math.Abs((copy.LastWriteTimeUtc - source.LastWriteTimeUtc).TotalSeconds) <= 2)
        {
            scan.Unchanged++;
            return;
        }
        if (!copy.Exists) scan.New++;
        scan.Changed.Add(new WorkItem(source.FullName, target, source.Length, itemPath));
        scan.ChangedBytes += source.Length;
    }

    static bool IsJunk(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) ||             // Office "file is open" markers
        name.StartsWith(".~lock.", StringComparison.Ordinal) ||        // LibreOffice lock files
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".DS_Store", StringComparison.Ordinal);

    /// <summary>OneDrive "online-only" files would be downloaded just by reading them; skip those.</summary>
    static bool IsCloudOnly(FileInfo file) =>
        ((int)file.Attributes & (0x00400000 /* RECALL_ON_DATA_ACCESS */ | 0x00040000 /* RECALL_ON_OPEN */ | 0x1000 /* OFFLINE */)) != 0;

    // ---------------------------------------------------------------- run

    public BackupResult Run(ScanResult scan, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var result = new BackupResult { Unchanged = scan.Unchanged };
        result.Problems.AddRange(scan.Problems);
        string marker = Path.Combine(computerRoot, ".in-progress");
        long bytesDone = 0, lastReport = 0;
        int done = 0;
        try
        {
            Directory.CreateDirectory(guard.Check(computerRoot));
            File.WriteAllText(guard.Check(Path.Combine(guard.Root, "README.txt")), Drives.ReadmeText);
            if (File.Exists(guard.Check(marker))) RecoverInterrupted();
            File.WriteAllText(guard.Check(marker), started.ToString("o"));

            for (int index = 0; index < scan.Changed.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var work = scan.Changed[index];
                string name = Path.GetFileName(work.Source);
                int total = scan.Changed.Count;
                progress?.Report(new BackupProgress(done, total, bytesDone, scan.ChangedBytes, name, index));
                long fileStart = bytesDone, fileBytes = 0;
                FileStatus status;
                string? reason = null;
                try
                {
                    var outcome = CopyOne(work, ct, n =>
                    {
                        bytesDone += n;
                        fileBytes += n;
                        long now = Environment.TickCount64;
                        if (now - lastReport > 100)
                        {
                            lastReport = now;
                            progress?.Report(new BackupProgress(done, total, bytesDone, scan.ChangedBytes, name, index, fileBytes));
                        }
                    });
                    if (outcome == Outcome.Added) result.Added++;
                    else result.Updated++;
                    if (outcome == Outcome.Versioned) result.VersionsKept++;
                    result.Bytes += work.Size;
                    status = outcome switch { Outcome.Added => FileStatus.Added, Outcome.Updated => FileStatus.Updated, _ => FileStatus.Versioned };
                }
                catch (SkipException ex)
                {
                    Skip(result, work, reason = ex.Message);
                    status = FileStatus.Skipped;
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    result.StoppedBecause = "The backup drive is full. Clean up old versions in PlugSafe (… menu), then plug it in again.";
                    progress?.Report(new BackupProgress(done, total, bytesDone, scan.ChangedBytes, name, index, 0, FileStatus.Skipped, "the backup drive is full"));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (!guard.DriveStillThere()) throw new SafetyException("The backup drive was removed, so the backup stopped.");
                    Skip(result, work, reason = Friendly(ex));
                    status = FileStatus.Skipped;
                }
                bytesDone = fileStart + work.Size;
                done++;
                // Every file's final state is reported (never throttled), so the popup's list ends up exact.
                progress?.Report(new BackupProgress(done, total, bytesDone, scan.ChangedBytes, name, index, work.Size, status, reason));
            }
        }
        catch (OperationCanceledException) { result.StoppedBecause = "You stopped the backup."; }
        catch (SafetyException ex)
        {
            result.StoppedBecause = ex.Message;
            Log.Write("SAFETY STOP: " + ex.Message);
        }
        finally
        {
            try { if (guard.DriveStillThere() && File.Exists(marker)) File.Delete(guard.Check(marker)); } catch { }
        }
        progress?.Report(new BackupProgress(done, scan.Changed.Count, bytesDone, scan.ChangedBytes, ""));
        Log.Write($"Backup to {computerRoot}: {result.Summary}" + (result.StoppedBecause != null ? $" (stopped: {result.StoppedBecause})" : ""));
        return result;
    }

    static void Skip(BackupResult result, WorkItem work, string reason)
    {
        result.Skipped++;
        if (result.Problems.Count < 200) result.Problems.Add($"{work.Source}: {reason}");
    }

    Outcome CopyOne(WorkItem work, CancellationToken ct, Action<long> onBytes)
    {
        var source = new FileInfo(work.Source);
        if (!source.Exists) throw new SkipException("it no longer exists on your PC");
        long size = source.Length;
        DateTime modified = source.LastWriteTimeUtc;
        if (fat32 && size > Fat32Max) throw new SkipException("it's over 4 GB, too big for this drive's FAT32 format");
        if (new DriveInfo(volumeRoot).AvailableFreeSpace < size + (16L << 20))
            throw new IOException("The backup drive is full.", unchecked((int)0x80070070));

        string target = guard.Check(work.Target);
        Directory.CreateDirectory(guard.Check(Path.GetDirectoryName(target)!));
        string temp = guard.Check(target + TempSuffix);
        if (File.Exists(temp)) File.Delete(temp); // our own leftover from an interrupted run
        bool moved = false;
        try
        {
            byte[] expected = CopyAndHash(work.Source, temp, ct, onBytes);

            // If the original changed while we were reading it, the copy could be half old, half new.
            source.Refresh();
            if (source.Length != size || source.LastWriteTimeUtc != modified)
                throw new SkipException("it changed while being copied; it'll be backed up next time");

            // Read the copy back from the drive itself (not the memory cache) and compare checksums.
            if (!HashFromDrive(temp).AsSpan().SequenceEqual(expected))
                throw new SkipException("the copy didn't match when read back; the drive may be faulty");

            File.SetLastWriteTimeUtc(temp, modified);
            File.SetCreationTime(temp, started); // "backed up on": drives the one-version-per-day rule
            var outcome = Swap(temp, target);
            moved = true;
            return outcome;
        }
        finally
        {
            if (!moved) try { if (File.Exists(temp)) File.Delete(guard.Check(temp)); } catch { }
        }
    }

    /// <summary>Puts a verified copy in place. The previous copy is kept as a version, or replaced if it's from today.</summary>
    Outcome Swap(string temp, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(temp, target);
            return Outcome.Added;
        }
        var previous = new FileInfo(target);
        DateTime backedUpOn = previous.CreationTime;
        if (backedUpOn.Date < started.Date)
        {
            // First change on a new day: the older copy moves to Versions\<the day it was backed up>.
            Archive(target, backedUpOn.ToString("yyyy-MM-dd"));
            File.Move(temp, target);
            return Outcome.Versioned;
        }
        if (IsMassiveChange(target, temp))
        {
            // Changed a lot within the same day (maybe by accident): keep today's earlier copy as well.
            Archive(target, started.ToString("yyyy-MM-dd HH-mm"));
            File.Move(temp, target);
            return Outcome.Versioned;
        }
        // Ordinary edit on the same day: today's copy is replaced by the newer one (the original is never involved).
        string old = guard.Check(target + OldSuffix);
        if (File.Exists(old)) File.Delete(old);
        File.Move(target, old);
        File.Move(temp, target);
        File.Delete(old);
        return Outcome.Updated;
    }

    void Archive(string target, string stamp)
    {
        string relative = Path.GetRelativePath(latest, target);
        string destination = guard.Check(Path.Combine(versions, stamp, relative));
        string folder = Path.GetDirectoryName(destination)!, name = Path.GetFileNameWithoutExtension(destination), ext = Path.GetExtension(destination);
        for (int i = 2; File.Exists(destination); i++) destination = guard.Check(Path.Combine(folder, $"{name} ({i}){ext}"));
        Directory.CreateDirectory(guard.Check(folder));
        File.Move(target, destination);
    }

    /// <summary>An earlier run was cut off (drive pulled out?): finish or undo its half-done steps.</summary>
    void RecoverInterrupted()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var old in Directory.EnumerateFiles(computerRoot, "*" + OldSuffix, options).ToList())
        {
            string final = old[..^OldSuffix.Length];
            if (!File.Exists(final)) File.Move(guard.Check(old), guard.Check(final)); // the swap stopped halfway: put the copy back
            else File.Delete(guard.Check(old));
        }
        foreach (var temp in Directory.EnumerateFiles(computerRoot, "*" + TempSuffix, options).ToList())
            File.Delete(guard.Check(temp));
        Log.Write("Recovered after an interrupted backup");
    }

    // ---------------------------------------------------------------- copying + checking

    byte[] CopyAndHash(string source, string temp, CancellationToken ct, Action<long> onBytes)
    {
        // Safety layer 3: the original is opened read-only, and shared so other programs keep working with it.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
            output.Write(buffer, 0, n);
            onBytes(n);
        }
        output.Flush(flushToDisk: true); // make sure it's really on the stick, not just in Windows' write cache
        return sha.GetHashAndReset();
    }

    byte[] HashFromDrive(string path)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            // Unbuffered reads need a sector-aligned buffer; this one is pinned, so its address never moves.
            long address = Marshal.UnsafeAddrOfPinnedArrayElement(alignedBuffer, 0).ToInt64();
            var chunk = alignedBuffer.AsMemory((int)((4096 - address % 4096) % 4096), BufferSize);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering | FileOptions.SequentialScan);
            long offset = 0;
            int n;
            while ((n = RandomAccess.Read(handle, chunk.Span, offset)) > 0)
            {
                sha.AppendData(chunk.Span[..n]);
                offset += n;
                if (n < chunk.Length) break;
            }
            return sha.GetHashAndReset();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // Some drives refuse unbuffered reads; fall back to a normal read-back.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            return SHA256.HashData(stream);
        }
    }

    /// <summary>
    /// "Massive" = shrank below 70 % (maybe most of it was deleted by accident) or over half the content is new.
    /// Content is compared in chunks whose edges follow the data itself, so inserting a paragraph only affects the
    /// chunks around it. Growing alone doesn't count: adding to a file doesn't put the earlier content at risk.
    /// </summary>
    static bool IsMassiveChange(string oldPath, string newPath)
    {
        long before = new FileInfo(oldPath).Length, after = new FileInfo(newPath).Length;
        if (before == 0) return false;
        if (after < before * 0.7) return true;
        if (before < 4096 || before > 512L << 20) return false; // tiny files: any edit looks big; huge files: too slow to compare
        return Similarity.ChangedFraction(oldPath, newPath, Math.Max(64, before / 32)) > 0.5;
    }

    static bool IsDiskFull(IOException ex) => ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    static string Friendly(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "it no longer exists on your PC",
        PathTooLongException => "its path is too long",
        UnauthorizedAccessException => "Windows didn't allow reading it",
        IOException io when io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) =>
            "it's open in another program that locks it; close that program and plug in again",
        _ => ex.Message,
    };
}

/// <summary>Content-defined chunking (like rsync / backup tools use) to estimate how much of a file changed.</summary>
static class Similarity
{
    static readonly ulong[] Gear = MakeGear();

    static ulong[] MakeGear()
    {
        var table = new ulong[256];
        ulong x = 0x9E3779B97F4A7C15;
        for (int i = 0; i < 256; i++)
        {
            x += 0x9E3779B97F4A7C15;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            table[i] = z ^ (z >> 31);
        }
        return table;
    }

    public static double ChangedFraction(string oldPath, string newPath, long averageChunk)
    {
        var known = new HashSet<ulong>();
        foreach (var (hash, _) in Chunks(oldPath, averageChunk)) known.Add(hash);
        long shared = 0, total = 0;
        foreach (var (hash, length) in Chunks(newPath, averageChunk))
        {
            total += length;
            if (known.Contains(hash)) shared += length;
        }
        return total == 0 ? 0 : 1 - shared / (double)total;
    }

    static IEnumerable<(ulong Hash, int Length)> Chunks(string path, long averageChunk)
    {
        int bits = Math.Clamp((int)Math.Round(Math.Log2(averageChunk)), 8, 20);
        ulong mask = (1UL << bits) - 1;
        int min = 1 << (bits - 2), max = 1 << (bits + 2);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var data = new byte[1 << 16];
        ulong rolling = 0, hash = 14695981039346656037;
        int length = 0, n;
        while ((n = stream.Read(data, 0, data.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                byte b = data[i];
                rolling = (rolling << 1) + Gear[b];
                hash = (hash ^ b) * 1099511628211;
                length++;
                if ((length >= min && (rolling & mask) == 0) || length >= max)
                {
                    yield return (hash ^ (ulong)length, length);
                    rolling = 0;
                    hash = 14695981039346656037;
                    length = 0;
                }
            }
        }
        if (length > 0) yield return (hash ^ (ulong)length, length);
    }
}
