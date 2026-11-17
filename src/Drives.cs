namespace PlugSafe;

/// <summary>A plugged-in USB / SD drive as it looks right now.</summary>
sealed record DriveCandidate(char Letter, string Label, string Product, long Total, long Free, string Format, uint Serial)
{
    public string Root => $"{Letter}:\\";
    /// <summary>The label you gave the drive, or its product name if the label is empty or a Windows default.</summary>
    public string DisplayName =>
        Label.Length > 0 && !GenericLabels.Contains(Label) ? Label : Product.Length > 0 ? Product : Label.Length > 0 ? Label : "USB drive";

    static readonly HashSet<string> GenericLabels = new(StringComparer.OrdinalIgnoreCase)
        { "New Volume", "Removable Disk", "USB Drive", "USB DISK", "NO NAME", "Local Disk" };
    public bool IsFat32 => Format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || Format.Equals("FAT", StringComparison.OrdinalIgnoreCase);
}

static class Drives
{
    public const string FolderName = "PlugSafe";
    const string MarkerName = "drive-id.txt";

    /// <summary>Plugged-in drives that are attached over USB or an SD/MMC reader. Internal disks are never offered.</summary>
    public static List<DriveCandidate> UsbDrives()
    {
        var result = new List<DriveCandidate>();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                char letter = char.ToUpperInvariant(drive.Name[0]);
                var storage = Native.StorageInfo(letter);
                bool external = storage?.Bus is Native.BusUsb or Native.BusSd or Native.BusMmc ||
                                (storage == null && drive.DriveType == DriveType.Removable);
                if (!external) continue;
                if (Native.VolumeSerial(drive.Name) is not uint serial) continue;
                result.Add(new DriveCandidate(letter, drive.VolumeLabel, storage?.Product ?? "", drive.TotalSize,
                    drive.AvailableFreeSpace, drive.DriveFormat, serial));
            }
            catch { /* drive vanished or isn't readable: not a candidate */ }
        }
        return result;
    }

    public static DriveCandidate? Candidate(char letter) =>
        UsbDrives().FirstOrDefault(d => d.Letter == char.ToUpperInvariant(letter));

    static string MarkerPath(DriveCandidate drive) => Path.Combine(drive.Root, FolderName, MarkerName);

    public static string? ReadMarkerId(DriveCandidate drive)
    {
        try
        {
            var path = MarkerPath(drive);
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("id=", StringComparison.Ordinal)) return line[3..].Trim();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Safety layer 1: a drive only counts as "the backup drive" when BOTH its PlugSafe ID file and its volume serial
    /// number match. A different stick, a reformatted stick, or a copied ID file will never be written to.
    /// </summary>
    public static DriveCandidate? FindConnected(BackupDrive drive, IEnumerable<DriveCandidate>? candidates = null) =>
        (candidates ?? UsbDrives()).FirstOrDefault(c => c.Serial == drive.Serial && ReadMarkerId(c) == drive.Id);

    /// <summary>Prepares a drive for backups: only creates the PlugSafe folder, its ID file and a README.</summary>
    public static string Register(DriveCandidate drive)
    {
        string id = ReadMarkerId(drive) ?? Guid.NewGuid().ToString("N"); // re-adding keeps the same identity
        string folder = Path.Combine(drive.Root, FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(MarkerPath(drive),
            "PlugSafe backup drive\r\n" +
            $"id={id}\r\n" +
            $"added={DateTime.Now:yyyy-MM-dd} on {Environment.MachineName}\r\n" +
            "\r\nThis file lets PlugSafe recognise this drive. Deleting it stops automatic backups to this drive.\r\n");
        File.WriteAllText(Path.Combine(folder, "README.txt"), ReadmeText);
        return id;
    }

    public static string FolderOn(DriveCandidate drive) => Path.Combine(drive.Root, FolderName);

    /// <summary>Under 10 % (or 1 GB) free counts as "almost full".</summary>
    public static bool LowOnSpace(long free, long total) => free < total * 0.10 || free < 1L << 30;

    /// <summary>This PC's folder name on the drive (computer names can't contain path characters, but be safe).</summary>
    public static string ComputerName => ComputerNameOverride ?? string.Concat(Environment.MachineName.Split(Path.GetInvalidFileNameChars()));

    /// <summary>Preview renders only, so screenshots don't show the real computer name.</summary>
    internal static string? ComputerNameOverride;

    public const string ReadmeText =
        "PlugSafe backups\r\n================\r\n\r\n" +
        "Every computer gets its own folder:\r\n\r\n" +
        "  <computer name>\\Latest\\    the newest copy of every file. Open these on any computer.\r\n" +
        "  <computer name>\\Versions\\  older copies, filed by the day they were backed up,\r\n" +
        "                              plus extra copies when a file changed a lot in one day.\r\n\r\n" +
        "PlugSafe never deletes your backups and never changes your original files - it only reads them.\r\n" +
        "drive-id.txt lets PlugSafe recognise this drive.\r\n";

    /// <summary>Drive letters named in a WM_DEVICECHANGE volume message.</summary>
    public static IEnumerable<char> LettersFromMask(uint mask)
    {
        for (int i = 0; i < 26; i++)
            if ((mask & (1u << i)) != 0) yield return (char)('A' + i);
    }
}
