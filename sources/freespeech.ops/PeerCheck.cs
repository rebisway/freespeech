/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FreeSpeech.Ops;

/// <summary>
/// How often a server checks that the servers it is linked with still answer (<c>Link:CheckSeconds</c>, default 60). Changed while the server runs
/// (<c>peer-check &lt;seconds&gt;</c>) and kept in <c>appsettings.Local.json</c>.
/// </summary>
public sealed class PeerCheckSettings(IConfiguration config, IServiceProvider services)
{
    public const int Min = 10, Max = 3600, Default = 60;

    private int _seconds = Math.Clamp(config.GetValue("Link:CheckSeconds", Default), Min, Max);

    public int Seconds => Volatile.Read(ref _seconds);

    public void Set(int seconds)
    {
        if (seconds is < Min or > Max) throw new ArgumentException($"seconds must be between {Min} and {Max}");
        Volatile.Write(ref _seconds, seconds);
        services.GetService<LocalConfigFile>()?.Set(new Dictionary<string, object?> { ["Link:CheckSeconds"] = seconds });
    }
}
