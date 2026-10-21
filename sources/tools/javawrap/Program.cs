/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;

// The Android workload's R8 task writes "-dontobfuscate" at the top of obj\...\proguard\proguard_xamarin.cfg every time it runs, just before it starts
// java: the code is shrunk but its names are kept. This program takes the place of java for that one call: it removes the line from the file the
// response file points at, then starts the real java (the folder is in java.real, written by the build) with the same arguments, and returns its exit code.
// Any other java call goes through untouched.
var self = AppContext.BaseDirectory;
var realDir = File.ReadAllText(Path.Combine(self, "java.real")).Trim().TrimEnd('\\', '/');

if (args.Contains("com.android.tools.r8.R8"))
    foreach (var a in args.Where(a => a.StartsWith('@')))
        AllowObfuscation(a[1..]);

var start = new ProcessStartInfo(Path.Combine(realDir, "java.exe")) { UseShellExecute = false };
foreach (var a in args) start.ArgumentList.Add(a);
using var java = Process.Start(start) ?? throw new InvalidOperationException("java did not start");
java.WaitForExit();
return java.ExitCode;

static void AllowObfuscation(string responseFile)
{
    if (!File.Exists(responseFile)) return;
    var lines = File.ReadAllLines(responseFile).Select(l => l.Trim()).ToList();
    var changed = false;
    for (var i = 0; i + 1 < lines.Count; i++)
    {
        if (lines[i] != "--pg-conf") continue;
        var cfg = lines[i + 1];
        if (!File.Exists(cfg)) continue;
        if (cfg.EndsWith("proguard_xamarin.cfg", StringComparison.OrdinalIgnoreCase))
        {
            var kept = File.ReadAllLines(cfg).Where(l => l.Trim() != "-dontobfuscate").ToArray();
            File.WriteAllLines(cfg, kept);
        }
        else if (File.ReadAllLines(cfg).Any(l => l.Trim() == "-dontoptimize"))
        {
            // The SDK's own proguard-android.txt (a shared file, never edited) turns the optimisations off, which Google Play reports as 0 % optimised code:
            // R8 reads a copy of it without that line, next to the response file.
            var copy = Path.Combine(Path.GetDirectoryName(responseFile)!, "optimizing-" + Path.GetFileName(cfg));
            File.WriteAllLines(copy, File.ReadAllLines(cfg).Where(l => l.Trim() != "-dontoptimize"));
            lines[i + 1] = copy;
            changed = true;
        }
    }
    if (changed) File.WriteAllLines(responseFile, lines);
}
