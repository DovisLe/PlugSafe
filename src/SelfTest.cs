using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PlugSafe;

/// <summary>
/// Dev aid: runs the real backup engine against throwaway folders and checks every safety rule.
/// Usage: PlugSafe.exe --selftest &lt;empty folder&gt;   (writes selftest.txt there)
/// </summary>
static class SelfTest
{
    public static void Run(string dir)
    {
        var report = new List<string>();
        int passed = 0, failed = 0;
        string reportPath = Path.Combine(dir, "selftest.txt");
        void Say(string line) { report.Add(line); File.WriteAllLines(reportPath, report); }
        void Check(bool ok, string what)
        {
            if (ok) passed++; else failed++;
            Say((ok ? "PASS  " : "FAIL  ") + what);
        }

        string src = Path.Combine(dir, "pc"), drive = Path.Combine(dir, "usb", "PlugSafe"), outside = Path.Combine(dir, "elsewhere");
        foreach (var d in new[] { src, Path.Combine(dir, "usb"), outside })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        Directory.CreateDirectory(Path.Combine(src, "Documents", "Sub"));
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(outside);

        string notes = Path.Combine(src, "Documents", "notes.txt"), deep = Path.Combine(src, "Documents", "Sub", "deep.txt");
        string big = Path.Combine(src, "Documents", "big.bin"), thesis = Path.Combine(src, "thesis.txt");
        File.WriteAllText(notes, Words(60_000, 1));
        File.WriteAllText(deep, "deep file");
        File.WriteAllText(Path.Combine(src, "Documents", "~$notes.docx"), "office lock file");
        File.WriteAllBytes(big, RandomNumberGenerator.GetBytes(40 << 20));
        File.WriteAllText(thesis, Words(20_000, 2));
        Directory.CreateDirectory(Path.Combine(src, "Documents", "app", "node_modules", "lib"));
        File.WriteAllText(Path.Combine(src, "Documents", "app", "node_modules", "lib", "index.js"), "skipped");
        Directory.CreateDirectory(Path.Combine(src, "Documents", ".git"));
        File.WriteAllText(Path.Combine(src, "Documents", ".git", "HEAD"), "skipped");
        var skip = new HashSet<string>(Config.DefaultSkipped, StringComparer.OrdinalIgnoreCase);

        var items = new List<BackupItem>
        {
            new() { Path = Path.Combine(src, "Documents"), IsFolder = true, Name = "Documents" },
            new() { Path = thesis, Name = "thesis.txt" },
        };
        uint serial = Native.VolumeSerial(Path.GetPathRoot(Path.GetFullPath(dir))!)!.Value;
        const string pc = "TESTPC";
        string latest = Path.Combine(drive, pc, "Latest"), versions = Path.Combine(drive, pc, "Versions");
        Say($"PlugSafe self-test  {DateTime.Now:yyyy-MM-dd HH:mm}");

        ScanResult lastScan = new();
        var reports = new List<BackupProgress>();

        BackupResult Backup(string label, CancellationToken ct = default)
        {
            var before = Snapshot(src);
            var engine = new BackupEngine(drive, pc, serial, items, fat32: false, skip);
            var clock = Stopwatch.StartNew();
            var scan = lastScan = engine.Scan(ct);
            reports.Clear();
            var result = engine.Run(scan, new Collect(reports), ct);
            Check(SameSnapshot(before, Snapshot(src)), $"{label}: originals untouched (content, size, modified date)");
            Say($"      {label}: {scan.Total} files, {scan.Changed.Count} changed -> {result.Summary}" +
                (result.StoppedBecause != null ? $"  [stopped: {result.StoppedBecause}]" : "") + $"  ({clock.ElapsedMilliseconds} ms)");
            return result;
        }

        // 1. First backup copies everything except junk files, verified.
        var r = Backup("first backup");
        Check(r.Added == 4, "first backup copies all 4 files");
        Check(SameBytes(big, Path.Combine(latest, "Documents", "big.bin")), "40 MB file copied byte-for-byte");
        Check(SameBytes(deep, Path.Combine(latest, "Documents", "Sub", "deep.txt")), "nested folders kept");
        Check(!File.Exists(Path.Combine(latest, "Documents", "~$notes.docx")), "Office lock file skipped");
        Check(!Directory.Exists(Path.Combine(latest, "Documents", "app", "node_modules")) && !Directory.Exists(Path.Combine(latest, "Documents", ".git")),
            "skipped folders (node_modules, .git) not backed up");
        Check(File.GetLastWriteTimeUtc(Path.Combine(latest, "thesis.txt")) == File.GetLastWriteTimeUtc(thesis), "copy keeps the original's modified date");
        Check(File.Exists(Path.Combine(drive, "README.txt")), "README written on the drive");
        Check(lastScan.Changed.All(w => items.Any(i => i.Path == w.ItemPath)), "every file is tagged with the chosen item it belongs to (for per-item progress)");
        Check(Enumerable.Range(0, lastScan.Changed.Count).All(i => reports.Any(p => p.Index == i && p.Status == FileStatus.Added && p.FileBytes == lastScan.Changed[i].Size)),
            "every file gets a final progress report (status + full size)");
        Check(Leftovers(drive).Count == 0, "no temp files left behind");

        // 2. Nothing changed: nothing copied.
        r = Backup("second run");
        Check(r.Added + r.Updated == 0 && r.Unchanged == 4, "unchanged files are not copied again");

        // 3. Small edit the same day: today's copy is refreshed, no extra version.
        File.AppendAllText(notes, " one more sentence.");
        r = Backup("small edit, same day");
        Check(r.Updated == 1 && r.VersionsKept == 0, "small same-day edit: copy refreshed, no extra version");
        Check(SameBytes(notes, Path.Combine(latest, "Documents", "notes.txt")), "refreshed copy matches the original");
        string beforeMassive = File.ReadAllText(notes);

        // 4. Rewritten (same size, different content) the same day: the earlier copy is kept too.
        File.WriteAllText(notes, Words(60_000, 99)); // about the same size, but different text throughout
        r = Backup("rewritten, same day");
        var kept = Directory.Exists(versions) ? Directory.GetFiles(versions, "notes.txt", SearchOption.AllDirectories) : Array.Empty<string>();
        Check(r.VersionsKept == 1 && kept.Length == 1 && File.ReadAllText(kept[0]) == beforeMassive,
            "massive same-day change: the earlier copy is kept in Versions");

        // 5. Shrunk to a fraction (accidentally deleted most of it?): earlier copy kept.
        string beforeShrink = File.ReadAllText(notes);
        File.WriteAllText(notes, beforeShrink[..(beforeShrink.Length / 10)]);
        r = Backup("shrank to 10%, same day");
        Check(r.VersionsKept == 1 && Directory.GetFiles(versions, "notes*.txt", SearchOption.AllDirectories).Length == 2,
            "file that shrank a lot: earlier copy kept as another version");

        // 6. First change on a new day: yesterday's copy moves to Versions\<yesterday>.
        string thesisCopy = Path.Combine(latest, "thesis.txt");
        File.SetCreationTime(thesisCopy, DateTime.Now.AddDays(-1)); // pretend it was backed up yesterday
        string yesterdayText = File.ReadAllText(thesis);
        File.AppendAllText(thesis, " edited today.");
        r = Backup("edit on a new day");
        string archived = Path.Combine(versions, DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"), "thesis.txt");
        Check(r.VersionsKept == 1 && File.Exists(archived) && File.ReadAllText(archived) == yesterdayText,
            "new day: yesterday's copy kept in Versions\\<yesterday>");

        // 7. Deleting an original never deletes its backup.
        File.Delete(deep);
        Backup("original deleted");
        Check(File.Exists(Path.Combine(latest, "Documents", "Sub", "deep.txt")), "backup kept after the original was deleted");

        // 8. A file locked by another program is skipped; the rest carry on.
        string locked = Path.Combine(src, "Documents", "locked.db");
        File.WriteAllText(locked, "in use");
        File.AppendAllText(thesis, " and again.");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = Backup("one file locked");
        Check(r.Skipped == 1 && r.Updated == 1 && r.Problems.Any(p => p.Contains("another program")),
            "locked file skipped with a clear reason, other files still backed up");

        // 9. The guard refuses anything outside the backup folder.
        var guard = new TargetGuard(drive, serial, new[] { src });
        Check(Throws(() => guard.Check(Path.Combine(outside, "x.txt"))), "guard: write outside the backup folder blocked");
        Check(Throws(() => guard.Check(drive + @"\..\..\escape.txt")), "guard: ..\\ escape blocked");
        Check(Throws(() => new TargetGuard(Path.Combine(src, "Documents"), serial, new[] { src })), "guard: backup folder inside the originals refused");
        Check(Throws(() => new TargetGuard(drive, serial ^ 1, new[] { src }).Check(Path.Combine(drive, pc, "x"))), "guard: wrong drive (serial mismatch) refused");
        Check(Throws(() => new TargetGuard(Path.GetPathRoot(drive)!, serial, new[] { src })), "guard: a whole drive as backup folder refused");

        // 10. A junction planted inside the backup folder (pointing elsewhere) is never followed.
        string evilSource = Path.Combine(src, "Documents", "Sub", "evil");
        Directory.CreateDirectory(evilSource);
        File.WriteAllText(Path.Combine(evilSource, "payload.txt"), "should never leave the backup folder");
        string junction = Path.Combine(latest, "Documents", "Sub", "evil");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        r = Backup("junction planted in backup folder");
        Check(r.StoppedBecause?.Contains("link") == true && !File.Exists(Path.Combine(outside, "payload.txt")),
            "junction inside the backup folder: backup stops, nothing written elsewhere");
        Directory.Delete(junction); // removes the link only
        Directory.Delete(evilSource, recursive: true);

        // 11. A run cut off between "move old copy aside" and "put new copy in place" is repaired.
        File.Move(thesisCopy, thesisCopy + BackupEngine.OldSuffix);
        File.WriteAllText(Path.Combine(latest, "Documents", "half.bin" + BackupEngine.TempSuffix), "half-written");
        File.WriteAllText(Path.Combine(drive, pc, ".in-progress"), "");
        Backup("after an interrupted run");
        Check(File.Exists(thesisCopy) && Leftovers(drive).Count == 0, "interrupted run repaired: copy restored, temp files cleaned");

        // 12. Stopping is clean.
        File.AppendAllText(thesis, " final.");
        using (var cts = new CancellationTokenSource())
        {
            var engine = new BackupEngine(drive, pc, serial, items, fat32: false, skip);
            var scan = engine.Scan(cts.Token);
            cts.Cancel();
            r = engine.Run(scan, null, cts.Token);
        }
        Check(r.StoppedBecause != null && Leftovers(drive).Count == 0, "stopping mid-way leaves no half files");

        // 13. Nothing was ever written outside the backup folder.
        Check(Directory.GetFiles(outside, "*", SearchOption.AllDirectories).Length == 0, "nothing ever written outside the backup folder");

        // 14. The "drive plugged in" message from Windows is understood (the same bytes Windows sends for E:).
        using (var menu = new ContextMenuStrip())
        using (var window = new MainForm(new Config(), menu, _ => false))
        {
            var heard = new List<char>();
            window.DrivesArrived += letters => heard.AddRange(letters);
            var message = Marshal.AllocHGlobal(20);
            try
            {
                Marshal.WriteInt32(message, 0, 20);                           // dbcv_size
                Marshal.WriteInt32(message, 4, Native.DBT_DEVTYP_VOLUME);     // dbcv_devicetype
                Marshal.WriteInt32(message, 8, 0);                            // dbcv_reserved
                Marshal.WriteInt32(message, 12, 1 << ('E' - 'A'));            // dbcv_unitmask: drive E:
                Marshal.WriteInt16(message, 16, 0);                           // dbcv_flags
                Native.SendMessage(window.Handle, Native.WM_DEVICECHANGE, (IntPtr)Native.DBT_DEVICEARRIVAL, message);
            }
            finally { Marshal.FreeHGlobal(message); }
            Check(heard.SequenceEqual(new[] { 'E' }), "\"drive plugged in\" message from Windows decoded as E:");
        }

        // 15. Cleaning up old versions deletes only old PlugSafe day-folders, never Latest, never through links.
        string oldDay = Path.Combine(versions, "2025-01-15"), oldDayTimed = Path.Combine(versions, "2025-01-15 10-30");
        string notOurs = Path.Combine(versions, "keep-me");
        Directory.CreateDirectory(Path.Combine(oldDay, "Documents"));
        File.WriteAllText(Path.Combine(oldDay, "Documents", "old.txt"), "old version");
        Directory.CreateDirectory(oldDayTimed);
        File.WriteAllText(Path.Combine(oldDayTimed, "older.txt"), "old version");
        Directory.CreateDirectory(notOurs);
        File.WriteAllText(Path.Combine(notOurs, "mine.txt"), "not made by PlugSafe");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "must survive");
        string trap = Path.Combine(oldDay, "trap");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{trap}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        var latestBefore = Snapshot(latest);
        var days = VersionCleaner.Scan(versions);
        var old = days.Where(d => d.Day < DateTime.Today.AddMonths(-6)).ToList();
        Check(old.Count == 2 && days.Count >= 4, $"clean-up finds the old version days ({old.Count} old of {days.Count}), ignores folders it didn't make");
        var cleaner = new TargetGuard(drive, serial, new[] { src });
        var (deleted, _) = VersionCleaner.Delete(cleaner, versions, old);
        Check(deleted == 2 && !File.Exists(Path.Combine(oldDay, "Documents", "old.txt")) && !Directory.Exists(oldDayTimed),
            "clean-up deleted the old version files");
        Check(SameSnapshot(latestBefore, Snapshot(latest)), "clean-up never touches Latest");
        Check(File.Exists(Path.Combine(notOurs, "mine.txt")), "clean-up leaves folders it didn't make alone");
        Check(File.Exists(Path.Combine(outside, "precious.txt")), "clean-up never follows a link out of the backup folder");
        Check(Throws(() => VersionCleaner.Delete(cleaner, latest, new[] { new VersionDay(DateTime.Today.AddYears(-2), Path.Combine(latest, "Documents"), 0, 0) })),
            "clean-up refuses to run on anything but a Versions folder");
        if (Directory.Exists(trap)) Directory.Delete(trap);
        File.Delete(Path.Combine(outside, "precious.txt"));

        // 16. Reminder rules.
        var now = DateTime.Now;
        var remind = new BackupDrive { Items = { new BackupItem { Path = src } }, Added = now.AddDays(-30) };
        remind.LastBackup = now.AddDays(-8);
        Check(Reminders.Due(remind, 7, now, pluggedIn: false), "reminder: due after 8 days without a backup");
        Check(!Reminders.Due(remind, 7, now, pluggedIn: true), "reminder: not shown while the drive is plugged in");
        Check(!Reminders.Due(remind, 0, now, pluggedIn: false), "reminder: off when set to Never");
        remind.LastReminder = now.AddHours(-1);
        Check(!Reminders.Due(remind, 7, now, pluggedIn: false), "reminder: at most once a day");
        remind.LastReminder = null;
        remind.LastBackup = now.AddDays(-2);
        Check(!Reminders.Due(remind, 7, now, pluggedIn: false), "reminder: not due 2 days after a backup");
        remind.LastBackup = null;
        Check(Reminders.Due(remind, 7, now, pluggedIn: false), "reminder: due when never backed up since adding the drive 30 days ago");
        Check(!Reminders.Due(new BackupDrive { Added = now.AddDays(-30) }, 7, now, pluggedIn: false), "reminder: not shown for a drive with nothing chosen");

        Say("");
        Say($"{passed} passed, {failed} failed");
    }

    /// <summary>
    /// Dev aid for the updater: reads a real GitHub release ("owner/repo:file.exe"), then installs a locally built
    /// newer exe over this copy the same way a downloaded one would be. Bad checksum and wrong version must be refused.
    /// </summary>
    public static void Update(string repoAndExe, string newExe, string reportFile)
    {
        var report = new List<string>();
        void Say(string line) { report.Add(line); File.WriteAllLines(reportFile, report); }
        static void Sync(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

        var parts = repoAndExe.Split(':');
        try
        {
            var found = Task.Run(() => Updater.CheckAsync(parts[0], parts[1], new Version(0, 0))).GetAwaiter().GetResult();
            Say(found == null ? $"check {parts[0]}: no newer release"
                : $"check {parts[0]}: {found.Tag} -> {Updater.Short(found.Version)}  digest={(found.Sha256 != null ? "yes" : "no")}");
            var mine = Task.Run(() => Updater.CheckAsync()).GetAwaiter().GetResult();
            Say($"check own repo ({Updater.Repo}): {(mine == null ? "nothing newer (or no releases yet)" : Updater.Short(mine.Version))}");
        }
        catch (Exception ex) { Say("check: FAILED " + ex.Message); }

        var version = Version.Parse(FileVersionInfo.GetVersionInfo(newExe).FileVersion!);
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newExe))).ToLowerInvariant();
        try
        {
            using (var input = File.OpenRead(newExe)) Sync(() => Updater.SaveCheckedAsync(input, new string('0', 64)));
            Say("bad checksum: ACCEPTED - FAILED");
        }
        catch (InvalidDataException) { Say("bad checksum: refused OK"); }
        using (var input = File.OpenRead(newExe)) Sync(() => Updater.SaveCheckedAsync(input, sha));
        try
        {
            Updater.Apply(new Version(9, 9, 9));
            Say("wrong version: ACCEPTED - FAILED");
        }
        catch (InvalidDataException) { Say("wrong version: refused OK"); }
        Updater.Apply(version);
        Say($"swapped in {Updater.Short(version)} and started it");
    }

    /// <summary>
    /// Dev aid for a real end-to-end test: adds a plugged-in USB drive through the same code as the "Add drive" button,
    /// with one folder to back up. Usage: PlugSafe.exe --dev-register E "C:\some\test folder"
    /// </summary>
    public static void Register(string letterText, string folder)
    {
        char letter = char.ToUpperInvariant(letterText[0]);
        var candidate = Drives.Candidate(letter);
        if (candidate == null)
        {
            Log.Write($"Dev: {letter}: is not a plugged-in USB drive; nothing registered");
            Environment.ExitCode = 1;
            return;
        }
        var config = Config.Load();
        string id = Drives.Register(candidate);
        var drive = config.Drives.FirstOrDefault(d => d.Id == id && d.Serial == candidate.Serial);
        if (drive == null)
        {
            drive = new BackupDrive { Id = id, Serial = candidate.Serial };
            config.Drives.Add(drive);
        }
        drive.Name = candidate.DisplayName;
        drive.TotalBytes = candidate.Total;
        drive.Added ??= DateTime.Now;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!drive.Items.Any(i => i.Path.Equals(full, StringComparison.OrdinalIgnoreCase)))
            drive.Items.Add(new BackupItem { Path = full, IsFolder = true, Name = Path.GetFileName(full) });
        config.Save();
        Log.Write($"Dev: registered {letter}: \"{candidate.DisplayName}\" ({candidate.Product}, serial {candidate.Serial:X8}) with test folder {full}");
    }

    static string Words(int count, int seed)
    {
        var rng = new Random(seed);
        string[] words = { "airsoft", "radio", "squad", "map", "battery", "lora", "field", "team", "the", "and", "plan", "night", "op" };
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(words[rng.Next(words.Length)]).Append(i % 12 == 11 ? ".\n" : " ");
        return sb.ToString();
    }

    static Dictionary<string, string> Snapshot(string folder)
    {
        var map = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            string hash;
            try { hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))); }
            catch (IOException) { hash = "locked"; }
            map[file] = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{info.CreationTimeUtc.Ticks}|{info.Attributes}|{hash}";
        }
        return map;
    }

    static bool SameSnapshot(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static bool SameBytes(string a, string b) =>
        File.Exists(a) && File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    static List<string> Leftovers(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(BackupEngine.TempSuffix) || f.EndsWith(BackupEngine.OldSuffix) || f.EndsWith(".in-progress")).ToList();

    /// <summary>Records progress reports synchronously (Progress&lt;T&gt; would post them to a UI thread).</summary>
    sealed class Collect(List<BackupProgress> into) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) { lock (into) into.Add(value); }
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (SafetyException) { return true; }
    }
}
