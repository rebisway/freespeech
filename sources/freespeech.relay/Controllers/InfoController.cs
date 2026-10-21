/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using Microsoft.AspNetCore.Mvc;

using FreeSpeech.Relay.Services;

namespace FreeSpeech.Relay.Controllers;

/// <summary>
/// What a user of this server may ask about the software it runs. The AGPL (section 13) obliges whoever runs a modified version as a
/// network service to offer its source code to the users of that service: <c>Server:SourceUrl</c> is that offer. Public and
/// unauthenticated, and it says nothing about the server's state.
/// </summary>
[ApiController]
[Route("api/info")]
public class InfoController : ControllerBase
{
    private readonly RelaySettings _settings;

    public InfoController(RelaySettings settings) => _settings = settings;

    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        software = "FreeSpeech relay server",
        license = "AGPL-3.0-or-later",
        version = FreeSpeech.Ops.ReleaseInfo.Version,
        source = _settings.Current.SourceUrl,
    });
}
