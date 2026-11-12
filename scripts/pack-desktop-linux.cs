// Packs the published Linux desktop client (called by scripts/build-desktop-linux.ps1;
// `dotnet run scripts/pack-desktop-linux.cs -- <version> <publishDir> <iconPng> <outDir>`).
// Writes, without any Linux tool: freespeech-desktop-<version>-linux-x64.tar.gz, freespeech-desktop_<version>_amd64.deb and SHA256SUMS.
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

var (version, publish, icon, outDir) = (args[0], args[1], args[2], args[3]);
Directory.CreateDirectory(outDir);
const string Desktop = "[Desktop Entry]\nType=Application\nName=FreeSpeech\nComment=Encrypted messaging\nExec={0}\nIcon=freespeech\nTerminal=false\nCategories=Network;InstantMessaging;Chat;\nStartupWMClass=FreeSpeech\n";
var files = Directory.GetFiles(publish, "*", SearchOption.AllDirectories)
	.Select(f => (Path: f, Rel: Path.GetRelativePath(publish, f).Replace('\\', '/'))).ToList();
const UnixFileMode Exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

GnuTarEntry Entry(TarEntryType type, string name, UnixFileMode mode, byte[]? data = null, string? link = null)
{
	var e = new GnuTarEntry(type, name) { Mode = mode, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = DateTimeOffset.UtcNow };
	if (link != null) e.LinkName = link;
	if (data != null) e.DataStream = new MemoryStream(data);
	return e;
}
byte[] TarGz(Action<TarWriter> fill)
{
	var ms = new MemoryStream();
	using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
	using (var tar = new TarWriter(gz, TarEntryFormat.Gnu, true)) fill(tar);
	return ms.ToArray();
}
void Dir(TarWriter t, string name) => t.WriteEntry(Entry(TarEntryType.Directory, name.TrimEnd('/') + "/", Exec));
UnixFileMode ModeOf(string rel) => rel == "FreeSpeech" || rel.EndsWith(".so") || rel.Contains(".so.") ? Exec : Plain;

// 1. tar.gz: freespeech/ (the program, the icon, the shortcut model); install.sh is hosted next to it.
var root = $"freespeech-desktop-{version}";
File.WriteAllBytes(Path.Combine(outDir, $"{root}-linux-x64.tar.gz"), TarGz(t =>
{
	Dir(t, "freespeech");
	foreach (var f in files) t.WriteEntry(Entry(TarEntryType.RegularFile, "freespeech/" + f.Rel, ModeOf(f.Rel), File.ReadAllBytes(f.Path)));
	t.WriteEntry(Entry(TarEntryType.RegularFile, "freespeech/freespeech.png", Plain, File.ReadAllBytes(icon)));
	t.WriteEntry(Entry(TarEntryType.RegularFile, "freespeech/freespeech.desktop", Plain, Encoding.UTF8.GetBytes(string.Format(Desktop, "freespeech"))));
}));

// 2. .deb: /opt/freespeech, /usr/bin/freespeech (link), menu shortcut, icon.
long size = files.Sum(f => new FileInfo(f.Path).Length) / 1024;
var control = $"Package: freespeech\nVersion: {version}\nSection: net\nPriority: optional\nArchitecture: amd64\nInstalled-Size: {size}\n" +
	"Depends: libc6, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libicu74 | libicu72 | libicu70 | libicu67 | libicu66\n" +
	"Maintainer: Thomas Gourgnier <noreply@freespeech.invalid>\nDescription: FreeSpeech desktop client\n End-to-end encrypted messaging, groups and files.\n";
var script = "#!/bin/sh\nset -e\ncommand -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true\ncommand -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor || true\nexit 0\n";
var controlTar = TarGz(t =>
{
	t.WriteEntry(Entry(TarEntryType.RegularFile, "./control", Plain, Encoding.UTF8.GetBytes(control)));
	t.WriteEntry(Entry(TarEntryType.RegularFile, "./postinst", Exec, Encoding.UTF8.GetBytes(script)));
	t.WriteEntry(Entry(TarEntryType.RegularFile, "./postrm", Exec, Encoding.UTF8.GetBytes(script)));
});
var subDirs = files.Select(f => Path.GetDirectoryName(f.Rel)!.Replace('\\', '/')).Where(d => d != "")
	.SelectMany(d => { var p = d.Split('/'); return Enumerable.Range(1, p.Length).Select(n => string.Join('/', p.Take(n))); })
	.Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
var dataTar = TarGz(t =>
{
	foreach (var d in new[] { "./opt", "./opt/freespeech", "./usr", "./usr/bin", "./usr/share", "./usr/share/applications", "./usr/share/icons", "./usr/share/icons/hicolor", "./usr/share/icons/hicolor/1024x1024", "./usr/share/icons/hicolor/1024x1024/apps" }) Dir(t, d);
	foreach (var d in subDirs) Dir(t, "./opt/freespeech/" + d);
	foreach (var f in files) t.WriteEntry(Entry(TarEntryType.RegularFile, "./opt/freespeech/" + f.Rel, ModeOf(f.Rel), File.ReadAllBytes(f.Path)));
	t.WriteEntry(Entry(TarEntryType.SymbolicLink, "./usr/bin/freespeech", Exec, null, "/opt/freespeech/FreeSpeech"));
	t.WriteEntry(Entry(TarEntryType.RegularFile, "./usr/share/applications/freespeech.desktop", Plain, Encoding.UTF8.GetBytes(string.Format(Desktop, "/opt/freespeech/FreeSpeech"))));
	t.WriteEntry(Entry(TarEntryType.RegularFile, "./usr/share/icons/hicolor/1024x1024/apps/freespeech.png", Plain, File.ReadAllBytes(icon)));
});
using (var deb = File.Create(Path.Combine(outDir, $"freespeech-desktop_{version}_amd64.deb")))
{
	deb.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
	void Member(string name, byte[] data)
	{
		deb.Write(Encoding.ASCII.GetBytes($"{name + "/",-16}{DateTimeOffset.UtcNow.ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{data.Length,-10}`\n"));
		deb.Write(data);
		if (data.Length % 2 == 1) deb.WriteByte((byte)'\n');
	}
	Member("debian-binary", Encoding.ASCII.GetBytes("2.0\n"));
	Member("control.tar.gz", controlTar);
	Member("data.tar.gz", dataTar);
}

// 3. SHA256SUMS of every archive of the folder.
var sums = Directory.GetFiles(outDir).Where(f => f.EndsWith(".tar.gz") || f.EndsWith(".deb") || f.EndsWith(".AppImage")).OrderBy(f => f, StringComparer.Ordinal)
	.Select(f => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}  {Path.GetFileName(f)}");
File.WriteAllText(Path.Combine(outDir, "SHA256SUMS"), string.Join("\n", sums) + "\n");
Console.WriteLine($"{outDir}: tar.gz, deb, SHA256SUMS");
