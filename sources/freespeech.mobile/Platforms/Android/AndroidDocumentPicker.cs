/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Android.App;
using Android.Content;
using Android.Provider;
using Microsoft.Win32.SafeHandles;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// The system's document picker (Storage Access Framework): the person chooses the place, the app gets a file descriptor on it. A descriptor
/// gives a seekable stream, which the archive needs to read itself back; a plain content stream would not.
/// </summary>
public sealed class AndroidDocumentPicker : IDocumentPicker
{
	private const int CreateRequest = 7301, OpenRequest = 7302;
	private static TaskCompletionSource<Android.Net.Uri?>? _pending;

	public Task<DocumentHandle?> CreateAsync(string suggestedName, string mime) =>
		Ask(CreateRequest, new Intent(Intent.ActionCreateDocument).AddCategory(Intent.CategoryOpenable).SetType(mime).PutExtra(Intent.ExtraTitle, suggestedName), suggestedName);

	public Task<DocumentHandle?> OpenAsync() =>
		Ask(OpenRequest, new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("*/*"), null);

	private static async Task<DocumentHandle?> Ask(int request, Intent intent, string? name)
	{
		var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No screen to ask from.");
		_pending = new TaskCompletionSource<Android.Net.Uri?>();
		activity.StartActivityForResult(intent, request);
		var uri = await _pending.Task;
		if (uri == null) return null;

		var resolver = activity.ContentResolver!;
		Stream Open(string mode)
		{
			var descriptor = resolver.OpenFileDescriptor(uri, mode) ?? throw new IOException("The chosen file cannot be opened.");
			var access = mode.Contains('w') ? FileAccess.ReadWrite : FileAccess.Read;   // "wt" truncates; the archive reads back what it wrote on another handle
			return new FileStream(new SafeFileHandle(descriptor.DetachFd(), ownsHandle: true), access);
		}
		return new DocumentHandle(name ?? DisplayName(resolver, uri), () => Open("r"), () => Open("rwt"));
	}

	/// <summary>Called by the activity when the picker answers.</summary>
	public static bool OnResult(int request, Result result, Intent? data)
	{
		if (request is not (CreateRequest or OpenRequest)) return false;
		_pending?.TrySetResult(result == Result.Ok ? data?.Data : null);
		return true;
	}

	private static string DisplayName(ContentResolver resolver, Android.Net.Uri uri)
	{
		using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
		return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) ?? "archive" : "archive";
	}
}
