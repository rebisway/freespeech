/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Console;

// The profile name selects a local identity directory so two instances can
// run side by side for testing:
//   dotnet run alice
//   dotnet run bob http://localhost:5299
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// Options: --log <off|error|warning|info|debug|trace> and --log-file <path> (diagnostic trace of the client library; never content).
args = ConsoleLog.Setup(args);
var profileName = args.Length > 0 ? args[0] : "default";
var serverUrl = args.Length > 1 ? args[1] : "http://localhost:5299";

var profileDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "FreeSpeech",
    profileName);

var app = new ConsoleApp(profileName, profileDir, serverUrl);
await app.RunAsync();
