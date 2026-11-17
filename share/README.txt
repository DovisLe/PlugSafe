PlugSafe - automatic backups to your USB drive
==============================================

Plug in your USB drive and PlugSafe copies the files you chose onto it. No clicking needed.

INSTALL
  1. Right-click the zip -> "Extract All..." (don't run it from inside the zip).
  2. Double-click "Install PlugSafe.bat".
     - If Windows says "Windows protected your PC", click "More info" -> "Run anyway".
       (The app isn't signed with a paid certificate; that's all the warning means.)
     - It installs Microsoft's .NET runtime if needed. Windows may ask permission once.
  3. In PlugSafe: plug in your USB drive, click "Add drive", then "+ Files" / "+ Folder".

HOW IT WORKS
  - Every time you plug that drive in, a small popup counts down 5 seconds and backs up
    whatever changed. "Skip" if you're in a hurry. When it says "Safe to unplug", you can.
  - On the drive: PlugSafe\<computer name>\Latest has the newest copy of everything,
    so you can open your files on any computer. Older copies are kept in ...\Versions.
  - It reminds you if you haven't backed up in a week, and warns when the drive gets full
    (then use "..." -> "Clean up old versions").
  - Uses no CPU while it waits in the tray.

SAFETY
  - Your original files are only ever read, never changed, moved or deleted.
  - It only writes inside the PlugSafe folder, and only on the drive you added (it checks
    the drive's ID and serial number before every backup). Nothing else on the drive is touched.
  - Every copy is read back from the drive and checked before it's trusted.
  - Removing something from the list never deletes it.

PRIVACY
  - No account, no cloud, no tracking. Your files never leave your PC and your USB drive.
  - The only network use is the optional weekly update check (you choose in the installer;
    change it from the tray menu). It asks GitHub for the newest version number, nothing else.

UNINSTALL
  Double-click "Uninstall PlugSafe.bat". Your files and your backups stay where they are.

Free and open source (MIT licence). It's a hobby project with no warranty: for anything
you can't lose, keep a second backup somewhere else too.
