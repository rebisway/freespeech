/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FreeSpeech.Tests;

/// <summary>A real FreeSpeech server process (relay, push gateway…) on a free local port, started from its build output.</summary>
public sealed class TestProcess : IDisposable
{
    private readonly Process _process;
    public int Port { get; }
    public string Http => $"http://127.0.0.1:{Port}";
    public Uri WsUri => new($"ws://127.0.0.1:{Port}/ws/relay");

    private TestProcess(Process process, int port) { _process = process; Port = port; }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// .../freespeech.tests/bin/&lt;config&gt;/net10.0/ -> .../&lt;project&gt;/bin/&lt;config&gt;/net10.0/&lt;dll&gt;. The project is looked for next to the test
    /// project, then in the v2 folder beside the test project's solution folder (tests kept in another folder that use v2's programs).
    /// </summary>
    public static string Dll(string project, string dll)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var testsFolder = dir.Parent!.Parent!.Parent!.Parent!;   // the folder holding the test project's folder
        string[] roots = [testsFolder.FullName, Path.Combine(testsFolder.Parent!.FullName, "v2")];
        var paths = roots.Select(root => Path.Combine(root, project, "bin", dir.Parent.Name, dir.Name, dll)).ToList();
        return paths.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"Build {project} first.", paths[0]);
    }

    /// <param name="fixedPort">Start on this port (to restart a server where it was); otherwise a free one. "{port}" in an environment value becomes the port.</param>
    public static async Task<TestProcess> StartAsync(string dll, IDictionary<string, string>? env = null, string healthPath = "/health", int? fixedPort = null)
    {
        var port = fixedPort ?? FreePort();
        var info = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{port}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (env != null) foreach (var (k, v) in env) info.Environment[k] = v.Replace("{port}", port.ToString());
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var started = new TestProcess(process, port);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 60; i++)
        {
            try { if ((await http.GetAsync(started.Http + healthPath)).IsSuccessStatusCode) return started; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        started.Dispose();
        throw new TimeoutException($"{Path.GetFileName(dll)} did not start.");
    }

    public void Dispose()
    {
        try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } catch (InvalidOperationException) { }
        _process.Dispose();
    }

    /// <summary>Runs "dotnet &lt;dll&gt; admin --url ... args" and returns its exit code and output.</summary>
    public static async Task<(int Code, string Output)> AdminCliAsync(string dll, string url, string secret, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("admin");
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add(url);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["FS_ADMIN_SECRET"] = secret;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
