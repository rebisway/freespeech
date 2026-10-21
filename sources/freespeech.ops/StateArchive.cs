/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeSpeech.Ops;

/// <summary>One state file of a server: the name it has in an archive and where it lives on this machine.</summary>
public sealed record StateFile(string Name, string Path);

/// <summary>The state files of one kind of server (<c>relay</c>, <c>push</c>, ...): what <c>export</c> saves and <c>import</c> puts back.</summary>
public sealed record StateFiles(string Kind, IReadOnlyList<StateFile> Files);

/// <summary>
/// A server's state in one file, to move a server to another machine or to keep a copy. The archive holds the server's private key, so it is
/// always encrypted with a passphrase (PBKDF2-SHA256, 600 000 rounds, then AES-256-GCM). It holds no secret of <c>appsettings.Local.json</c>:
/// those are written again on the new machine.
/// </summary>
public static class StateArchive
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FSSTATE\u0001");
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, Rounds = 600_000, MinPassphrase = 8;

    private sealed class Content
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("created")] public DateTime Created { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, byte[]> Files { get; set; } = [];
    }

    public static void CheckPassphrase(string? passphrase)
    {
        if (passphrase == null || passphrase.Length < MinPassphrase) throw new ArgumentException($"the passphrase needs at least {MinPassphrase} characters");
    }

    /// <summary>Reads the files that exist and returns the encrypted archive.</summary>
    public static byte[] Pack(StateFiles state, string passphrase)
    {
        CheckPassphrase(passphrase);
        var content = new Content { Kind = state.Kind, Created = DateTime.UtcNow };
        foreach (var file in state.Files)
            if (File.Exists(file.Path)) content.Files[file.Name] = File.ReadAllBytes(file.Path);
        if (content.Files.Count == 0) throw new InvalidOperationException("there is no state to save yet");

        var plain = JsonSerializer.SerializeToUtf8Bytes(content);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(Key(passphrase, salt), TagSize)) aes.Encrypt(nonce, plain, cipher, tag, Magic);
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Decrypts an archive and checks that it is the state of a server of the same kind.</summary>
    public static IReadOnlyDictionary<string, byte[]> Unpack(StateFiles state, byte[] data, string passphrase)
    {
        var header = Magic.Length + SaltSize + NonceSize + TagSize;
        if (data.Length <= header || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("this is not a FreeSpeech state archive");
        var salt = data.AsSpan(Magic.Length, SaltSize);
        var nonce = data.AsSpan(Magic.Length + SaltSize, NonceSize);
        var tag = data.AsSpan(Magic.Length + SaltSize + NonceSize, TagSize);
        var cipher = data.AsSpan(header);
        var plain = new byte[cipher.Length];
        try { using var aes = new AesGcm(Key(passphrase, salt.ToArray()), TagSize); aes.Decrypt(nonce, cipher, tag, plain, Magic); }
        catch (CryptographicException) { throw new InvalidDataException("wrong passphrase, or the file is damaged"); }

        var content = JsonSerializer.Deserialize<Content>(plain) ?? throw new InvalidDataException("the archive is empty");
        if (content.Kind != state.Kind) throw new InvalidDataException($"this archive is the state of a {content.Kind} server, not of a {state.Kind} server");
        var unknown = content.Files.Keys.Where(n => state.Files.All(f => f.Name != n)).ToList();
        if (unknown.Count > 0) throw new InvalidDataException("the archive holds files this program does not know: " + string.Join(", ", unknown));
        return content.Files;
    }

    /// <summary>
    /// Puts the files of an archive in place. Refuses to replace a state that exists unless <paramref name="force"/>, in which case the replaced
    /// file is kept next to it as <c>.before-import</c>. The server must be stopped: a running one would write its own state over it.
    /// </summary>
    public static IReadOnlyList<string> Import(StateFiles state, byte[] data, string passphrase, bool force)
    {
        var files = Unpack(state, data, passphrase);
        var existing = files.Keys.Select(n => state.Files.First(f => f.Name == n)).Where(f => File.Exists(f.Path)).ToList();
        if (existing.Count > 0 && !force)
            throw new InvalidOperationException("a state already exists (" + string.Join(", ", existing.Select(f => f.Path)) + "): stop the server and use --force to replace it");

        var written = new List<string>();
        foreach (var (name, bytes) in files)
        {
            var target = state.Files.First(f => f.Name == name).Path;
            if (File.Exists(target)) File.Copy(target, target + ".before-import", overwrite: true);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target))!);
            AtomicFile.WriteAllBytes(target, bytes);
            written.Add(target);
        }
        return written;
    }

    private static byte[] Key(string passphrase, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Rounds, HashAlgorithmName.SHA256, 32);
}

/// <summary>The offline <c>import</c> command of a server: <c>import &lt;file&gt; [--force]</c>, run on the machine with the service stopped.</summary>
public static class StateCli
{
    public const string ImportUsage = "import <file> [--force]";

    public static int RunImport(string[] args, StateFiles state)
    {
        var list = args.ToList();
        var force = AdminCliKit.TakeFlag(list, "--force");
        if (list.Count != 1) { Console.Error.WriteLine("usage: " + ImportUsage); return 1; }
        try
        {
            var data = File.ReadAllBytes(list[0]);
            var passphrase = PromptKit.ReadSecret("Passphrase of the archive: ", "FS_STATE_PASSPHRASE", confirm: false);
            var written = StateArchive.Import(state, data, passphrase, force);
            foreach (var path in written) Console.WriteLine("restored " + path);
            Console.WriteLine("Start the service again. appsettings.Local.json is not part of the archive: check it with 'check'.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
    }
}

/// <summary>Reads a secret typed by a person (nothing shown), piped in, or given by an environment variable — never as a command-line argument.</summary>
public static class PromptKit
{
    public static string ReadSecret(string prompt, string environmentVariable, bool confirm)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment) return fromEnvironment;
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? throw new ArgumentException("no secret on the standard input");

        var first = Hidden(prompt);
        if (confirm && Hidden("Again: ") != first) throw new ArgumentException("the two entries differ");
        return first;
    }

    private static string Hidden(string prompt)
    {
        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}
