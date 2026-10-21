# FreeSpeech - AGPL-3.0-or-later - Copyright (c) 2024-2026 Thomas Gourgnier
#
# Builds the compressed texts the app draws recovery phrases from (sources\freespeech.mobile\Resources\Raw\books\<lang>.txt.gz).
# The texts are public-domain books from Project Gutenberg (old translations for English; the license header and footer are removed,
# only the book itself is kept). Run once, commit the resulting .gz files; see Resources\Raw\books\SOURCES.md.
#
#   scripts\prepare-books.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'sources\freespeech.mobile\Resources\Raw\books'
New-Item -ItemType Directory -Force $out | Out-Null

# language -> Gutenberg ebook ids, and the most characters kept per language (about 1.3 MB of text is plenty)
$books = [ordered]@{
    fr = @{ Ids = 5097, 800;  Max = 1400000 }   # Vingt mille lieues sous les mers; Le tour du monde en quatre-vingts jours
    en = @{ Ids = 164, 103;   Max = 1400000 }   # Twenty Thousand Leagues under the Sea (Mercier, 1872); Around the World in Eighty Days (Towle, 1873)
    es = @{ Ids = 2000;       Max = 1400000 }   # Don Quijote
}

function Clean([string] $raw) {
    # keep only what lies between the Gutenberg start and end markers
    $s = [regex]::Match($raw, '\*\*\* ?START OF (?:THE|THIS) PROJECT GUTENBERG EBOOK[^\r\n]*\*\*\*').Index
    $m = [regex]::Match($raw, '\*\*\* ?START OF (?:THE|THIS) PROJECT GUTENBERG EBOOK[^\r\n]*\*\*\*')
    if ($m.Success) { $raw = $raw.Substring($m.Index + $m.Length) }
    $e = $raw.IndexOf('*** END OF')
    if ($e -gt 0) { $raw = $raw.Substring(0, $e) }
    $raw = $raw -replace '\[(Illustration|Ilustraci[oó]n|Footnote)[^\]]*\]', ' '
    $raw = $raw -replace '\r', ''
    # drop short upper-case lines (chapter titles, table of contents), keep prose; join the rest into one flowing text
    $lines = $raw -split "`n" | Where-Object { $t = $_.Trim(); $t.Length -gt 0 -and -not ($t -cmatch '^[^a-zà-ÿ]{0,60}$') }
    (($lines -join ' ') -replace '\s+', ' ').Trim()
}

foreach ($lang in $books.Keys) {
    $text = ''
    foreach ($id in $books[$lang].Ids) {
        $url = "https://www.gutenberg.org/cache/epub/$id/pg$id.txt"
        Write-Host "[$lang] $url"
        $raw = (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 120).Content
        if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }
        $text += (Clean $raw) + ' '
    }
    $text = $text.Trim()
    if ($text.Length -gt $books[$lang].Max) { $text = $text.Substring(0, $books[$lang].Max) }

    $path = Join-Path $out "$lang.txt.gz"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $fs = [IO.File]::Create($path)
    $gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionLevel]::Optimal)
    $gz.Write($bytes, 0, $bytes.Length); $gz.Dispose(); $fs.Dispose()
    '{0}: {1:N0} characters -> {2:N0} KB compressed' -f $lang, $text.Length, ((Get-Item $path).Length / 1KB)
}
