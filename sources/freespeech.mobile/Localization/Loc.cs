/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace FreeSpeech.Mobile.Localization;

/// <summary>What an import found: the language, and how many texts it still lacks compared with English (those show in English).</summary>
public sealed record ImportReport(string Code, string Name, int Missing);

/// <summary>A language the app can show: its code ("fr"), its own name ("Français"), and where it came from.</summary>
public sealed record LanguageInfo(string Code, string Name, bool Imported);

/// <summary>
/// The app's texts. Each language is a flat JSON file (<c>{ "key": "text with {0} placeholders" }</c>): the ones shipped
/// with the app are embedded (fr, en, es); more can be dropped in <c>{AppData}/lang/{code}.json</c> (an import screen
/// will copy them there). A key missing from the chosen language falls back to English, then to the key itself.
/// The special key <c>_name</c> is the language's own name. Bindings to <c>Loc.Instance[key]</c> refresh on a language change.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
	public const string FallbackCode = "en";
	private const string PrefKey = "language";

	public static Loc Instance { get; } = new();

	private readonly Dictionary<string, Dictionary<string, string>> _packs = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<string, string> _current = [];

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Language { get; private set; } = FallbackCode;

	public IReadOnlyList<LanguageInfo> Available => _packs
		.Select(p => new LanguageInfo(p.Key, p.Value.GetValueOrDefault("_name", p.Key), _imported.Contains(p.Key)))
		.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
		.ToList();

	/// <summary>Loads the embedded and imported languages, then picks the saved one, or the phone's, or English.</summary>
	public void Initialize()
	{
		_packs.Clear();
		_imported.Clear();

		var assembly = typeof(Loc).Assembly;
		foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Lang.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
		{
			// A broken shipped file must not take the app down: that language is skipped, the others (English first) still work.
				try { using var stream = assembly.GetManifestResourceStream(name)!; Add(name[5..^5], stream, imported: false); }
				catch (JsonException ex) { System.Diagnostics.Debug.WriteLine($"Language file {name} is invalid and was skipped: {ex.Message}"); }
		}

		var dir = ImportDir;
		if (Directory.Exists(dir))
		{
			foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
			{
				try { using var stream = File.OpenRead(file); Add(Path.GetFileNameWithoutExtension(file), stream, imported: true); }
				catch (Exception ex) when (ex is IOException or JsonException) { /* a broken pack is ignored, the app stays usable */ }
			}
		}

		var wanted = Preferences.Default.Get(PrefKey, "");
		if (!_packs.ContainsKey(wanted)) wanted = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
		Select(_packs.ContainsKey(wanted) ? wanted : FallbackCode, save: false);
	}

	/// <summary>Where imported language files live.</summary>
	public static string ImportDir => Path.Combine(FileSystem.AppDataDirectory, "lang");

	private void Add(string code, Stream json, bool imported)
	{
		var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
		_packs[code] = map;
		if (imported) _imported.Add(code); else _imported.Remove(code);
	}

	public void Select(string code, bool save = true)
	{
		if (!_packs.TryGetValue(code, out var pack)) return;
		Language = _packs.Keys.First(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));
		_current = pack;
		if (save) Preferences.Default.Set(PrefKey, Language);
		// An empty name means "everything changed": the only form every indexer binding reacts to.
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
	}

	/// <summary>
	/// Adds a language from a JSON file chosen by the user. It needs a <c>_name</c>; its code is <c>_code</c> or the file name.
	/// The shipped languages cannot be replaced. Throws <see cref="InvalidDataException"/> with a text key when refused.
	/// </summary>
	public ImportReport Import(Stream json, string fileName)
	{
		Dictionary<string, string>? map;
		try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
		catch (JsonException) { throw new InvalidDataException("lang_invalid"); }
		if (map == null || !map.TryGetValue("_name", out var name) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("lang_invalid");

		var code = (map.GetValueOrDefault("_code") ?? Path.GetFileNameWithoutExtension(fileName)).Trim().ToLowerInvariant();
		if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z]{2,3}(-[a-z0-9]{2,8})?$")) throw new InvalidDataException("lang_invalid");
		if (_packs.ContainsKey(code) && !_imported.Contains(code)) throw new InvalidDataException("lang_reserved");

		Directory.CreateDirectory(ImportDir);
		File.WriteAllText(Path.Combine(ImportDir, code + ".json"), JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
		_packs[code] = map;
		_imported.Add(code);
		Select(code);
		var missing = _packs[FallbackCode].Keys.Count(k => !k.StartsWith('_') && !map.ContainsKey(k));
		return new ImportReport(code, name, missing);
	}

	/// <summary>Removes an imported language (the shipped ones stay); the app goes back to English if it was in use.</summary>
	public void RemoveImported(string code)
	{
		if (!_imported.Remove(code)) return;
		_packs.Remove(code);
		try { File.Delete(Path.Combine(ImportDir, code + ".json")); } catch (IOException) { /* gone next start */ }
		if (string.Equals(Language, code, StringComparison.OrdinalIgnoreCase)) Select(FallbackCode);
		else PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
	}

	/// <summary>Writes the English texts as a file to translate (a starting point for a new language) and returns its path.</summary>
	public string WriteTemplate()
	{
		var map = new Dictionary<string, string> { ["_code"] = "xx", ["_name"] = "My language" };
		foreach (var (k, v) in _packs[FallbackCode]) if (!k.StartsWith('_')) map[k] = v;
		var path = Path.Combine(FileSystem.CacheDirectory, "freespeech-language-template.json");
		File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
		return path;
	}

	public string this[string key] =>
		_current.TryGetValue(key, out var text) ? text
		: _packs.TryGetValue(FallbackCode, out var en) && en.TryGetValue(key, out var fallback) ? fallback
		: key;

	/// <summary>The text for a key, with its <c>{0}</c> placeholders filled.</summary>
	public static string T(string key, params object[] args)
	{
		var text = Instance[key];
		return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
	}
}
