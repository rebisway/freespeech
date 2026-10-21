# Textes des livres (phrases de récupération) / Book texts (recovery phrases)

Ces fichiers (`fr.txt.gz`, `en.txt.gz`, `es.txt.gz`) servent à tirer les phrases de récupération de l'utilisateur
(`PhraseGenerator`, PROTOCOL §1). Ce sont des œuvres **du domaine public**, tirées de Project Gutenberg ; l'en-tête
et le pied de page de licence de Gutenberg sont retirés, seul le livre est conservé. Regénérés par `scripts/prepare-books.ps1`.

These files (`fr.txt.gz`, `en.txt.gz`, `es.txt.gz`) are used to draw the user's recovery phrases (`PhraseGenerator`,
PROTOCOL §1). They are **public-domain** works from Project Gutenberg; Gutenberg's license header and footer are removed,
only the book is kept. Rebuilt by `scripts/prepare-books.ps1`.

| Langue / Language | Livre / Book | Gutenberg |
|---|---|---|
| fr | Jules Verne, *Vingt mille lieues sous les mers* ; *Le tour du monde en quatre-vingts jours* | #5097, #800 |
| en | Jules Verne, *Twenty Thousand Leagues under the Sea* (L. P. Mercier, 1872) ; *Around the World in Eighty Days* (G. M. Towle, 1873) | #164, #103 |
| es | Miguel de Cervantes, *Don Quijote* (début / first 1.4 M characters) | #2000 |

**La sécurité ne repose pas sur le secret de ces textes** : ils sont publics, comme le code. Chaque phrase y ajoute des mots tirés
au hasard par le générateur cryptographique du système (voir PROTOCOL §1).
**Security does not rely on these texts being secret**: they are public, like the code. Each phrase adds words drawn at random
by the system's cryptographic generator (see PROTOCOL §1).
