/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Diagnostics;
using System.Text;

namespace FreeSpeech.Tests;

/// <summary>
/// The one way to configure secrets (appsettings.Local.json next to the program) and the alarm when they are missing: the "check"
/// command of each server and the Critical line at start-up. Real processes, as the update scripts and the operator use them.
/// </summary>
public class ConfigCheckTests
{
    private static readonly string ServerDll = TestProcess.Dll("freespeech.relay", "FreeSpeech.Relay.dll");
    private static readonly string PushDll = TestProcess.Dll("freespeech.push", "FreeSpeech.Push.dll");
    private const string Good = "0123456789abcdef0123456789abcdef";

    private static async Task<(int Code, string Output)> RunAsync(string dll, IDictionary<string, string?>? env, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        foreach (var a in args) info.ArgumentList.Add(a);
        // The test machine must not leak its own settings into the verdict.
        foreach (var key in new[] { "Admin__Secret", "Push__StatePath", "Push__Fake", "Fcm__ServiceAccountFile", "Apns__KeyFile", "Admin__DataPath" })
            info.Environment.Remove(key);
        if (env != null) foreach (var (k, v) in env) if (v != null) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task RelayCheck_failsWithoutASecret_andSaysWhereToPutIt()
    {
        var (code, output) = await RunAsync(ServerDll, null, "check");
        Assert.Equal(1, code);
        Assert.Contains("Admin:Secret", output);
        Assert.Contains("is not set", output);
        Assert.Contains("appsettings.Local.json", output);
        Assert.Contains("NOT ready", output);
    }

    [Fact]
    public async Task RelayCheck_refusesAnExampleValueAndAShortSecret()
    {
        var example = await RunAsync(ServerDll, new Dictionary<string, string?> { ["Admin__Secret"] = "<openssl rand -hex 32>" }, "check");
        Assert.Equal(1, example.Code);
        Assert.Contains("example value", example.Output);

        var changeMe = await RunAsync(ServerDll, new Dictionary<string, string?> { ["Admin__Secret"] = "change-me-long-random" }, "check");
        Assert.Equal(1, changeMe.Code);

        var shortOne = await RunAsync(ServerDll, new Dictionary<string, string?> { ["Admin__Secret"] = "short" }, "check");
        Assert.Equal(1, shortOne.Code);
        Assert.Contains("too short", shortOne.Output);
    }

    [Fact]
    public async Task RelayCheck_passesWithAGoodSecret()
    {
        var (code, output) = await RunAsync(ServerDll, new Dictionary<string, string?> { ["Admin__Secret"] = Good }, "check");
        Assert.Equal(0, code);
        Assert.Contains("nothing missing", output);
    }

    [Fact]
    public async Task RelayCheck_readsTheSecretsFileNextToTheProgram_andAPublishDoesNotHoldOne()
    {
        // A copy of the program in its own folder, as installed: the file sits beside the dll and is not part of what is published.
        var source = Path.GetDirectoryName(ServerDll)!;
        Assert.False(File.Exists(Path.Combine(source, "appsettings.Local.json")), "the build output must not contain a secrets file");

        var dir = TempDir();
        try
        {
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            var dll = Path.Combine(dir, Path.GetFileName(ServerDll));

            Assert.Equal(1, (await RunAsync(dll, null, "check")).Code);
            File.WriteAllText(Path.Combine(dir, "appsettings.Local.json"), $"{{ \"Admin\": {{ \"Secret\": \"{Good}\" }} }}");
            Assert.Equal(0, (await RunAsync(dll, null, "check")).Code);

            // And an update (the published files copied again) leaves it where it is.
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            Assert.Equal(0, (await RunAsync(dll, null, "check")).Code);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task TheCommentedExampleFiles_loadAsTheyAre_andTheirPlaceholdersCountAsMissing()
    {
        // The templates are copied to appsettings.Local.json as they are (with their // comments): they must parse, and must NOT pass for real secrets.
        var deploy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "deploy"));
        foreach (var (dll, kind) in new[] { (ServerDll, "relay"), (PushDll, "push") })
        {
            var dir = TempDir();
            try
            {
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(dll)!)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
                File.Copy(Path.Combine(deploy, kind, "deploy", $"appsettings.Local.{kind}.example.json"), Path.Combine(dir, "appsettings.Local.json"));
                var (code, output) = await RunAsync(Path.Combine(dir, Path.GetFileName(dll)), null, "check");
                Assert.Equal(1, code);
                Assert.Contains("example value", output);
                Assert.DoesNotContain("Unhandled", output);   // a parse error would crash instead of reporting
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
        }
    }

    [Fact]
    public async Task RelayStartup_shoutsAtCriticalLevel_whenTheSecretIsMissing()
    {
        var port = TestProcess.FreePort();
        var info = new ProcessStartInfo("dotnet", $"\"{ServerDll}\" --urls http://127.0.0.1:{port}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["Logging__LogLevel__FreeSpeech"] = "Warning";   // the production setting: only warnings of FreeSpeech code…
        info.Environment.Remove("Admin__Secret");
        using var process = Process.Start(info)!;
        var seen = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (seen) seen.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline) { lock (seen) if (seen.ToString().Contains("NOT CONFIGURED")) break; await Task.Delay(200); }
            string text; lock (seen) text = seen.ToString();
            Assert.Contains("NOT CONFIGURED", text);   // …and still the alarm gets through
            Assert.Contains("Admin:Secret", text);
        }
        finally { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    [Fact]
    public async Task PushCheck_flagsAMissingSecretAndAMissingFirebaseFile()
    {
        var dir = TempDir();
        try
        {
            var bare = await RunAsync(PushDll, null, "check");
            Assert.Equal(1, bare.Code);
            Assert.Contains("Admin:Secret", bare.Output);
            Assert.Contains("Fcm:ServiceAccountFile", bare.Output);

            var missingFile = await RunAsync(PushDll, new Dictionary<string, string?> { ["Admin__Secret"] = Good, ["Fcm__ServiceAccountFile"] = Path.Combine(dir, "nope.json") }, "check");
            Assert.Equal(1, missingFile.Code);
            Assert.Contains("does not exist", missingFile.Output);

            var account = Path.Combine(dir, "firebase-service-account.json");
            File.WriteAllText(account, "{}");
            var fine = await RunAsync(PushDll, new Dictionary<string, string?> { ["Admin__Secret"] = Good, ["Fcm__ServiceAccountFile"] = account }, "check");
            Assert.Equal(0, fine.Code);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task PushCheck_flagsAMissingRelayKey_whenTheStateFolderIsKnown()
    {
        var dir = TempDir();
        try
        {
            var account = Path.Combine(dir, "firebase-service-account.json");
            File.WriteAllText(account, "{}");
            var env = new Dictionary<string, string?> { ["Admin__Secret"] = Good, ["Fcm__ServiceAccountFile"] = account, ["Push__StatePath"] = Path.Combine(dir, "state") };

            var none = await RunAsync(PushDll, env, "check");
            Assert.Equal(1, none.Code);
            Assert.Contains("none accepted yet", none.Output);
            Assert.Contains("add relay", none.Output);

            PeerSeed.Accepted(Path.Combine(dir, "state"), [("relay", "", null)]);   // an accepted relay counts
            Assert.Equal(0, (await RunAsync(PushDll, env, "check")).Code);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

}
