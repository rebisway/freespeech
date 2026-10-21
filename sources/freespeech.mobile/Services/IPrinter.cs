/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/namespace FreeSpeech.Mobile.Services;

/// <summary>Sends an HTML page to the system's print dialog (paper, or a PDF the user then decides what to do with).</summary>
public interface IPrinter
{
	Task PrintAsync(string jobName, string html);
}