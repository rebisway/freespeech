/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Globalization;
using System.Text.Json;

namespace FreeSpeech.Desktop.Services;

/// <summary>What an import found: the language, and how many texts it still lacks compared with English (those show in English).</summary>
public sealed record ImportReport(string Code, string Name, int Missing);

/// <summary>A language the app can show: its code ("fr"), its own name ("Français"), and where it came from.</summary>
public sealed record LanguageInfo(string Code, string Name, bool Imported);

/// <summary>
/// The texts: the language files shared with the mobile app (embedded, "Lang.xx.json") plus the desktop's own ("Desk.desktop.xx.json").
/// Each language is a flat JSON file; more can be dropped in <c>{AppData}/FreeSpeech/lang/{code}.json</c> (the Settings screen imports
/// them). The language is the saved choice, or the system's, or English; a missing key falls back to English, then to the key itself.
/// The language is read at start: a change asks to start the program again.
/// </summary>
public static class Loc
{
	public const string FallbackCode = "en";

	private static readonly Dictionary<string, Dictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> Imported = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, string> English;
	private static readonly Dictionary<string, string> Current;

	public static string ImportDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSpeech", "lang");

	static Loc()
	{
		LoadPacks();
		English = Packs.GetValueOrDefault(FallbackCode) ?? [];
		var wanted = Prefs.Get("language", "");
		if (!Packs.ContainsKey(wanted)) wanted = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
		Code = Packs.ContainsKey(wanted) ? Packs.Keys.First(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase)) : FallbackCode;
		Current = Packs.GetValueOrDefault(Code) ?? English;
	}

	private static void LoadPacks()
	{
		var assembly = typeof(Loc).Assembly;
		foreach (var resource in assembly.GetManifestResourceNames())
		{
			string code;
			if (resource.StartsWith("Lang.", StringComparison.Ordinal) && resource.EndsWith(".json", StringComparison.Ordinal)) code = resource[5..^5];
			else if (resource.StartsWith("Desk.desktop.", StringComparison.Ordinal) && resource.EndsWith(".json", StringComparison.Ordinal)) code = resource[13..^5];
			else continue;
			try
			{
				using var stream = assembly.GetManifestResourceStream(resource)!;
				// A broken shipped file must not take the app down: that language is skipped, the others (English first) still work.
				var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
				if (!Packs.TryGetValue(code, out var pack)) Packs[code] = pack = [];
				foreach (var (k, v) in map) pack[k] = v;
			}
			catch (JsonException ex) { Diag.Log($"Language file {resource} is invalid and was skipped: {ex.Message}"); }
		}

		if (!Directory.Exists(ImportDir)) return;
		foreach (var file in Directory.EnumerateFiles(ImportDir, "*.json"))
		{
			try
			{
				var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
				var code = Path.GetFileNameWithoutExtension(file);
				Packs[code] = map;
				Imported.Add(code);
			}
			catch (Exception ex) when (ex is IOException or JsonException) { /* a broken pack is ignored, the app stays usable */ }
		}
	}

	/// <summary>The language in use ("fr"): the saved choice, the system's, or English.</summary>
	public static string Code { get; }

	/// <summary>The two-letter code of the language in use (the book of the recovery phrases follows it).</summary>
	public static string Language => Code;

	public static IReadOnlyList<LanguageInfo> Available => Packs
		.Select(p => new LanguageInfo(p.Key, p.Value.GetValueOrDefault("_name", p.Key), Imported.Contains(p.Key)))
		.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
		.ToList();

	/// <summary>Saves the choice; it applies the next time the program starts.</summary>
	public static void Select(string code) => Prefs.Set("language", code);

	/// <summary>
	/// Adds a language from a JSON file chosen by the user. It needs a <c>_name</c>; its code is <c>_code</c> or the file name. The shipped
	/// languages cannot be replaced. Throws <see cref="InvalidDataException"/> with a text key when refused.
	/// </summary>
	public static ImportReport Import(Stream json, string fileName)
	{
		Dictionary<string, string>? map;
		try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
		catch (JsonException) { throw new InvalidDataException("lang_invalid"); }
		if (map == null || !map.TryGetValue("_name", out var name) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("lang_invalid");

		var code = (map.GetValueOrDefault("_code") ?? Path.GetFileNameWithoutExtension(fileName)).Trim().ToLowerInvariant();
		if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z]{2,3}(-[a-z0-9]{2,8})?$")) throw new InvalidDataException("lang_invalid");
		if (Packs.ContainsKey(code) && !Imported.Contains(code)) throw new InvalidDataException("lang_reserved");

		Directory.CreateDirectory(ImportDir);
		File.WriteAllText(Path.Combine(ImportDir, code + ".json"), JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
		Packs[code] = map;
		Imported.Add(code);
		Select(code);
		var missing = English.Keys.Count(k => !k.StartsWith('_') && !map.ContainsKey(k));
		return new ImportReport(code, name, missing);
	}

	/// <summary>Removes an imported language (the shipped ones stay); English is chosen if it was the saved one.</summary>
	public static void RemoveImported(string code)
	{
		if (!Imported.Remove(code)) return;
		Packs.Remove(code);
		try { File.Delete(Path.Combine(ImportDir, code + ".json")); } catch (IOException) { /* gone next start */ }
		if (string.Equals(Prefs.Get("language", ""), code, StringComparison.OrdinalIgnoreCase)) Select(FallbackCode);
	}

	/// <summary>The English texts as a file to translate (a starting point for a new language).</summary>
	public static string Template()
	{
		var map = new Dictionary<string, string> { ["_code"] = "xx", ["_name"] = "My language" };
		foreach (var (k, v) in English) if (!k.StartsWith('_')) map[k] = v;
		return JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
	}

	public static bool TryGet(string key, out string text)
	{
		text = Current.GetValueOrDefault(key) ?? English.GetValueOrDefault(key) ?? "";
		return text.Length > 0;
	}

	public static string T(string key, params object[] args)
	{
		var text = Current.GetValueOrDefault(key) ?? English.GetValueOrDefault(key) ?? key;
		return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
	}
}