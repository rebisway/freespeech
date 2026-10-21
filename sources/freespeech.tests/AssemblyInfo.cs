/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

// Many test classes start real server processes (relay, push gateway, console). Run all at once on a small machine, they starve each
// other and the timing-sensitive tests fail at random; two at a time keeps the suite fast and steady.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
