# Protocole FreeSpeech

*English version: [PROTOCOL.en.md](PROTOCOL.en.md). Référence appel par appel : [PROTOCOL_API.fr.md](PROTOCOL_API.fr.md). Architecture, cryptographie et stockage local : [ARCHITECTURE.fr.md](ARCHITECTURE.fr.md).*

Document de conception du protocole.
Le serveur ne connaît jamais le contenu — seulement des métadonnées de routage
strictement nécessaires (from/to, présence, epoch de clé).

---

## 1. Identité

```
uid = f(phrase1, phrase2, phrase3)   déterministe, reproductible, anonyme
keypair = derive(uid)                ECDH X25519 (chiffrement) + Ed25519 (signature)
discovery_id = SHA256(enc_pub_key + sign_pub_key)
```

**Dérivation (normative)** — les mêmes trois phrases doivent donner la même identité dans
toute implémentation :
```
seed         = Argon2id( mot de passe = p1 ‖ 0x00 ‖ p2 ‖ 0x00 ‖ p3   (chaque phrase sans espaces de bord, UTF-8),
                         sel = "FreeSpeech-v3-seed", mémoire 32 Mio, t = 3, p = 1, sortie 32 o )
enc_seed     = HKDF-SHA256(seed, sel="FreeSpeech-v2", info="encryption")
sign_seed    = HKDF-SHA256(seed, sel="FreeSpeech-v2", info="signing")
db_key       = HKDF-SHA256(seed, sel="FreeSpeech-v2", info="database-v1")   (chiffrement de la base locale)
```
Chaque phrase : 75 caractères au moins, les trois différentes. Le sel ne peut pas être propre à
chaque utilisateur (la dérivation doit marcher avec les seules phrases) : c'est une étiquette
publique fixe, qui évite les tables précalculées partagées avec d'autres applications. Argon2id
rend coûteuse l'énumération de phrases saisies à la main ; pour les phrases tirées par
l'application (≈ 150 bits en tout) elle ne change pas l'ordre de grandeur de la sécurité.
Des vecteurs de test figés (`CryptoCompatibilityTests`) permettent de vérifier une autre
implémentation. Seules les identités créées par cette dérivation se retrouvent en resaisissant leurs
phrases ; une identité d'une autre origine ne reste valable que tant que l'appli garde son profil.

- Pas de recovery si les 3 phrases sont perdues (choix de sécurité assumé).
- Pas de serveur nécessaire pour créer une identité — génération 100% locale.
- Perte de phrases = perte définitive d'identité, pas de mécanisme alternatif.

**Multi-device** : rien n'empêche de recréer la même identité sur un autre client
en resaisissant les 3 phrases. Conséquence : la même identité peut être active
simultanément sur plusieurs devices.
- **Pas de pairing entre devices** : `uid`/`keypair` étant dérivés
  *déterministement* des 3 phrases, resaisir les mêmes phrases sur un 2e
  device calcule exactement le même `discovery_id` et les mêmes clés — sans
  échange, sans confirmation, sans canal dédié entre devices. Le serveur ne
  distingue jamais les devices entre eux : juste plusieurs connexions sous un
  même `discovery_id` (cf. registre ci-dessous). Le protocole n'a pas besoin
  de connaître la notion de "device".
- Registre serveur : `discovery_id → liste de connexions` (pas 1:1).
- Diffusion : tout message entrant est relayé à **toutes** les connexions actives
  de l'identité.
- **Self-sync live conditionnel (§5)** : avec le chiffrement asymétrique
  réel de crypto=1, l'enveloppe d'un `send` est chiffrée pour la clé
  publique du **destinataire** — un simple écho serveur du même ciphertext
  vers les autres connexions de l'expéditeur serait
  illisible par elles (pas la bonne clé privée). Le serveur ne fait donc
  pas cet écho : il indique seulement, dans `send_result.self_multi_device`,
  si l'expéditeur a au moins une autre connexion active en ce moment
  (`RelaySocketHandler.HandleSendAsync`). Si oui, le **client** envoie en
  plus un second envelope, adressé à lui-même (`to` = son propre
  `discovery_id`), chiffré avec sa propre clé publique, portant le vrai
  destinataire (`orig_to`) à l'intérieur du contenu déchiffré — c'est ce qui
  permet à A2 de voir en direct un message envoyé par A1 (même identité,
  device différent). Coût : un envoi réseau de plus, mais seulement quand
  un autre device est effectivement connecté (pas systématique). Ne couvre
  que le cas live ; le cas offline reste couvert par le mécanisme de relais
  sur réception ci-dessous.
- Sync inter-devices : traiter "soi-même" comme un pseudo-contact — les mêmes
  types de Letter (message, identity_update...) adressés à son propre
  discovery_id servent à propager l'état (lu/non-lu, contacts ajoutés) entre
  ses propres appareils. Pas de canal dédié.
  - **Déclenchement systématique à la réception** : dès qu'un device reçoit un
    message (live ou via rendez-vous WAITING/READY), il relaie immédiatement
    ce message vers son propre `discovery_id`, comme s'il l'envoyait à un
    contact. C'est le mécanisme qui règle la synchro multi-device pour le
    rendez-vous offline (cf. note ci-dessous) — pas besoin d'étendre le flag
    WAITING/READY pour qu'il connaisse tous les devices.
  - **Limite du rendez-vous WAITING/READY en multi-device** : le flag serveur
    est stocké par paire `(From, To)` de `discovery_id`, une seule entrée —
    pas par connexion (cf. §9, même registre que les notifications). Si B a
    un message pour A et que A a 2 devices, seul le device qui répond READY
    en premier programme le rendez-vous avec B ; l'autre device de A n'est
    pas informé directement par ce flag. Il reçoit néanmoins le message via
    la propagation "soi-même" ci-dessus, dès que le premier device l'a reçu
    et qu'il est lui-même joignable (live ou via son propre cycle
    WAITING/READY avec sa propre identité).
- OTC (*One-Time Code*) de mise en contact : chaîne aléatoire courte servant
  de secret partagé pour chiffrer symétriquement l'échange de ContactCard
  (cf. §3). Invalidé globalement après un seul usage, quel que soit le device
  qui l'a consommé.

### Miroir d'un appareil restauré (un autre appareil de la même identité est connecté)

Un appareil A neuf (les trois phrases saisies, éventuellement la sauvegarde de B importée) peut se faire **refléter** par un autre appareil B de la même
identité, connecté en ce moment : B est la référence. Aucun appairage : ce sont des lettres adressées à sa propre identité (comme `wipe`), scellées et
signées avec les clés de l'identité ; le relais, qui n'envoie jamais une lettre à la connexion qui l'a émise, ne les livre qu'aux **autres** appareils.

- **`device_sync_request { id, device, have, contacts, ts, code }`** (A → soi-même) : `id` aléatoire (128 bits) ; `code` = **six chiffres** tirés au hasard,
  affichés **sur A seulement** ; `device` = identifiant aléatoire de cet appareil pour cette exécution (la réponse lui est adressée, un appareil ne se répond
  jamais) ; `have` = pour chaque conversation **hors groupes**, l'empreinte de ce que A détient (SHA-256 tronqué à 16 hexadécimaux sur `id|état|date de
  modification;` de chaque message, dans l'ordre) ; `contacts` = pour chaque contact, l'empreinte de sa fiche. Valable **5 minutes**. Si aucun autre appareil ne
  l'a reçue (`send_result.delivered` faux), A la renvoie à chaque tick pendant ce temps.
- **Accord de B : le code se saisit sur B.** B affiche seulement « synchronisation demandée » (avis interne, avis du téléphone en arrière-plan, bandeau sur
  l'accueil tant que la demande vit) ; l'utilisateur la touche et **saisit le code montré sur A** (espaces et tirets admis). B n'envoie **rien** avant le bon
  code. Trois codes faux au plus : au troisième la demande est annulée, A reçoit une réponse `refused`. Le code n'étant que sur l'écran de A, il ne peut être
  donné que par qui tient A : qui possède les trois phrases mais pas le propriétaire de B ne peut rien obtenir, et une demande inattendue sur B révèle un vol
  de phrases. Quand l'un des appareils de la même identité y répond, les autres retirent la demande.
- **`device_sync_data { request, to_device, contacts[], replace[], remove[], history{conv: entries[]}, attachments[], last, refused }`** (B → soi-même, en
  plusieurs parties d'environ 150 Kio, la dernière avec `last`) : les fiches de contact qui diffèrent ; les **conversations dont l'empreinte diffère**,
  **entières** (messages, états : modifié, supprimé pour tous, fichier retiré) avec leur nom dans `replace` ; `remove` = les conversations que A détient et B
  non. A ne prend la réponse que si `request` est sa demande en cours et `to_device` son identifiant.
- **Miroir** : A **remplace** chaque conversation de `replace` (effacée une seule fois par demande, les parties suivantes s'y ajoutent), supprime celles de
  `remove`, et prend les fiches de contact de B (celle de B l'emporte). Les messages supprimés pour soi sur B n'existent plus sur A. Les **groupes** ne
  passent pas par là : A les rattrape auprès de leurs membres (§7). Les **fichiers** des messages repris (images, documents, reçus ou
  envoyés) sont des pièces jointes réelles : A reçoit leurs enregistrements (« proposés », `from_my_device`) et les télécharge **depuis B** par les lettres
  `attachment_request` / `attachment_chunk` (§6) adressées à sa propre identité, chunks vérifiés par leur preuve de Merkle. B ne sert à un autre appareil de
  son identité que les fichiers nommés dans une réponse que son propriétaire a acceptée (par le code) : une demande qui en nomme un autre est ignorée. L'appli
  lance les téléchargements selon son réglage de téléchargement automatique, le reste par un appui.
- **Déclenchement** (appli) : à la fin de l'import de l'identité sur A (sauvegarde importée, écran Sauvegarde ou restauration à l'ouverture), A demande « Synchroniser
  avec un autre appareil ? » ; sur oui, la demande part et le code s'affiche. Il ne se passe rien si aucun autre appareil n'est en ligne (A l'indique).

### Créer une identité (mobile) — trois étapes

1. **Nouvelle connexion** : l'utilisateur saisit seulement son nom. L'appli tire les trois phrases (ci-dessus) mais **ne crée pas
   encore l'identité** ; phrases, nom et serveur restent en mémoire (« brouillon »).
2. **Sauvegarde des phrases** (écran sans retour possible, avec l'avertissement « une fois cette fenêtre quittée, il ne sera plus
   jamais possible de récupérer ces phrases »). Trois moyens, cumulables :
   - **Afficher** : les trois phrases à l'écran, dans un champ en lecture seule dont le texte se sélectionne ; un bouton les
     copie (une phrase par ligne, sans numérotation, pour que le collage dans l'écran de restauration marche tel quel).
   - **Imprimer** : feuille imprimée par le système (boîte d'impression d'Android, qui propose aussi « Enregistrer au format
     PDF »), avec les phrases et un **QR code** (format « feuille » ci-dessous). **Papier uniquement** : le QR code donne les
     trois phrases à qui le lit.
   - **Fichier protégé** (« clé ») : les phrases chiffrées par un mot de passe choisi par l'utilisateur (format « fichier de clé »
     ci-dessous), remis au menu de partage du système.
   Continuer sans rien avoir fait demande une confirmation explicite (« sans vos phrases, la perte ou le changement de ce téléphone
   fera perdre votre identité pour toujours »).
3. **Coordonnées** : en quittant l'étape 2, l'identité est **créée** (dérivation Argon2id, quelques secondes), puis l'appli ouvre
   l'écran des coordonnées tant qu'aucun contact ni groupe n'existe. Si l'appli est fermée pendant l'étape 2, le brouillon
   disparaît et **rien n'a été créé** : au démarrage suivant, de nouvelles phrases sont tirées.

La page Sauvegarde de l'appli **ne concerne que les données** (contacts, groupes, discussions, pièces jointes, §2) : elle ne propose
jamais d'afficher les phrases, pour qu'un tiers qui tient le téléphone ne puisse pas les lire.

### Fichier de clé (`.fskey`) et feuille imprimée

**Fichier de clé** — `"FSKEY1" ‖ mémoire_Kio(4) ‖ itérations(4) ‖ sel(16) ‖ nonce(12) ‖ ChaCha20-Poly1305(clé, nonce, AD = tout
ce qui précède le chiffré, "phrase1\nphrase2\nphrase3")`, entiers en grand-boutiste, avec
`clé = Argon2id(mot de passe UTF-8, sel, mémoire, itérations, p = 1)` sur 32 octets. Coût écrit : **64 Mio, 3 passes** (de l'ordre
de la seconde sur un téléphone) ; il est lu dans le fichier, donc peut croître sans casser les anciens fichiers. Le mot de passe est
choisi par l'utilisateur (**10 caractères au moins**, saisi deux fois, affichable à l'écran). Le sel et le nonce sont tirés au hasard à
chaque fichier (deux fichiers des mêmes phrases diffèrent). Le fichier est écrit dans le cache de l'appli puis partagé ; il est
nommé `freespeech-key-AAAAMMJJ.fskey`. À l'ouverture : en-tête vérifié, **coût borné** (mémoire ≤ 256 Mio, itérations ≤ 10) avant toute
allocation, puis déchiffrement authentifié (un mot de passe faux, un fichier modifié ou un coût falsifié — l'en-tête est
authentifié — donnent la même erreur « mot de passe incorrect, ou fichier modifié »). Code : `Identity/PhraseVault.cs`.

**Feuille imprimée** — QR code (correction d'erreur L) du texte `freespeech-phrases-v1\nphrase1\nphrase2\nphrase3`. La première ligne
sert d'étiquette : le scanner de l'appli distingue une feuille d'une fiche de contact et refuse tout autre QR code dans l'écran de
restauration. Code : `Identity/PhraseVault.cs` (`PhraseSheet`).

### Restaurer une identité

« Restaurer mon identité » (première connexion) propose, au choix, pour remplir les trois phrases :

| Moyen | Détail |
|---|---|
| Saisie | trois champs, chaque phrase d'au moins 75 caractères, les trois différentes |
| Coller | trois lignes (une par phrase) copiées d'un gestionnaire de mots de passe ou d'une note ; refusé si le texte ne fait pas exactement trois lignes |
| Fichier de clé | choix du fichier `.fskey`, puis mot de passe (affichable) ; le déchiffrement remplit les trois champs |
| Feuille imprimée | scan du QR code avec la caméra ; remplit les trois champs |

Puis, **en option**, un **fichier de sauvegarde** de données (§2) pour retrouver aussi contacts, groupes et discussions.
Avant de créer quoi que ce soit, l'appli vérifie que les phrases ouvrent bien la sauvegarde choisie (une faute de frappe ou un mauvais
fichier créerait sinon une autre identité, vide) : en cas d'échec, message d'erreur et **rien n'est créé**. La restauration remet aussi
le nom, la description et la photo de l'utilisateur (§2). Même écran de saisie quand le téléphone a perdu la clé locale de son profil
(état « phrases à ressaisir »).

### Verrouillage de l'appli par code PIN (optionnel, local)

Rien à voir avec le protocole réseau : c'est un réglage de l'appli mobile (`AppLock`, logique pure dans `PinLock`).

- **Désactivé par défaut**, activable dans les réglages (« Verrouillage par code »). Le PIN fait 4 à 8 chiffres, saisi deux fois.
- **Quand** : au retour dans l'appli (ou à son lancement) si elle est restée **inutilisée** plus longtemps que le délai choisi : tout de
  suite (15 s de grâce pour ne pas se verrouiller après le sélecteur de fichiers), 5 min, 1 h, 1 jour ou **1 semaine (défaut)**. Le délai
  compte depuis la dernière sortie de l'appli au premier plan, pas depuis la dernière connexion réseau. Une horloge reculée vaut
  « verrouillé ».
- **Stockage** : jamais le PIN, mais `sel(16) ‖ PBKDF2-SHA256(200 000 itérations)` dans le coffre sécurisé du système (Keystore Android).
  Comparaison à temps constant.
- **Ce qu'il protège** : l'écran. La base reste chiffrée par la clé du coffre (§1, inchangé) et la réception continue en arrière-plan
  (réveil push, contrôle périodique) : un téléphone verrouillé reçoit toujours ses messages. Tant que l'écran de code est affiché, rien
  n'est marqué lu.
- **Effacement après N échecs** (option, **désactivée par défaut**, 5 ou 10) : les codes faux sont comptés ; au N-ième de suite, l'appli
  efface tout ce qu'elle garde sur le téléphone (profil, coffre, préférences, cache) et repart de zéro. L'identité se retrouve avec les
  trois phrases. L'activation demande confirmation.
- **Désactiver ou changer le code** demande le code actuel (un voleur devant un téléphone déverrouillé ne le coupe pas).
- **Code oublié** : saisir les trois phrases ; elles doivent redonner la clé de base conservée sur le téléphone. Le verrouillage est alors
  désactivé (à réactiver avec un nouveau code).
- **Limites** : verrouillage au niveau de l'appli, pas du chiffrement ; un attaquant qui obtient les droits root du téléphone ou
  l'extraction du Keystore le contourne. Les compteurs et le délai sont des préférences : modifiables avec un accès root. Pas de
  masquage de l'aperçu dans le sélecteur d'applications récentes, sauf si le réglage **écran protégé** (Android, désactivé par défaut,
  `FLAG_SECURE`) est activé.

### Téléphone volé : effacement à distance (implémenté)

- **Clé de révocation** : Ed25519 dérivée de la graine des phrases (HKDF, info `revocation-v1`), comme la clé de base. Les appareils n'en
  gardent que la clé **publique** (`revocation_public_key` de l'identité) ; la privée n'existe que le temps de saisir les phrases. Une
  signature par la clé d'identité ne prouverait rien : le voleur a les clés dans la base du téléphone.
- **Ordre `wipe`** (§5) : lettre à soi-même `{ ts, sig }`, `sig` = signature de la clé de révocation sur
  `"FreeSpeech-wipe-v1|<discovery_id>|<ts>"` (liée à l'identité et à l'instant). Envoyée depuis un autre appareil, **avec les trois
  phrases** (saisies, jamais conservées).
- **Réception** (appareil de la même identité) : ordre ignoré s'il ne vient pas de soi-même, si l'identité n'a pas de clé de révocation
  publique (rien à vérifier : jamais obéi sur parole), si la signature est fausse, ou si l'appareil a été installé **après** `ts`
  (un ancien ordre ne peut pas effacer un téléphone restauré plus tard). L'appareil qui envoie ne s'efface pas. Sinon l'appli efface
  tout ce qu'elle garde (profil, coffre, préférences, cache) et repart de zéro ; l'identité se retrouve avec les phrases.
- **Remise au mieux** : le relais ne stocke rien. L'ordre part tout de suite vers les appareils connectés, puis il est **reproposé toutes
  les 2 minutes pendant 30 jours** par le contrôle périodique de l'appareil émetteur. Un appareil volé n'est atteint que s'il se
  connecte pendant que l'émetteur l'est aussi (ou au réveil push de celui-ci) ; coupé du réseau, il n'est jamais atteint.
- **Activation** : une identité créée avant cette fonction n'a pas la clé publique : sur chaque téléphone, saisir une fois les trois
  phrases (Réglages → Téléphone volé ou perdu → Activer). Une identité créée ou restaurée depuis cette version l'a d'office.
- **Limite** : l'effacement détruit la copie du téléphone volé mais ne **révoque pas** l'identité : un voleur qui a déjà lu les clés
  peut s'en servir ailleurs. La suite (lettre `identity_moved`, §12) permettrait de migrer contacts et groupes.

### Téléphone volé : migration vers une nouvelle identité `identity_moved` (implémentée)

Le wipe détruit la copie volée mais ne révoque rien : les clés restent valides pour qui les a lues. Le propriétaire (qui a encore ses phrases)
passe donc à une **nouvelle identité** (nouvelles phrases) et prévient ses contacts et les modérateurs de ses groupes ; chacun décide de le suivre.

- **Épinglage de la clé de révocation** : `revocation_public_key` figure dans la fiche de contact, dans les `identity_update` et dans le
  `member_profile` (champs `revocation_public_key` et `revocation_sig` = signature du membre sur `revpin|id|clé`, **hors** de `sig` : les versions qui
  ne les connaissent pas vérifient le profil comme avant). Le premier exemplaire vu est **épinglé** (`RevocationPinStore`) et ne change plus ; une clé
  différente pour un identifiant déjà épinglé est refusée. Jamais prise dans le `GroupState` (l'identifiant ne la couvre pas). Les pins sont dans la
  sauvegarde. `AnnounceRevocationKeyAsync` l'annonce aux groupes, une fois par groupe.
- **Côté expéditeur** (appareil portant l'ancienne identité ; Réglages → « Changer d'identité », écran `MigratePage`) :
  1. **remise des rôles** : créateur d'un groupe → un successeur choisi (modérateurs d'abord) reçoit le rôle, puis je quitte celui de modérateur ;
     simple modérateur → je quitte le rôle (`RolesToHandOver`, `HandOverRolesAsync`) ; un créateur sans successeur bloque l'étape ;
  2. anciennes phrases saisies et **vérifiées** avant tout changement ;
  3. nouvelles phrases tirées, écran de sauvegarde habituel (§1) ;
  4. en quittant cet écran, `SendMovedAsync` envoie `identity_moved { old_id, new_card, ts, sig }` — `sig` = clé de révocation de l'ancienne identité sur
     `"FreeSpeech-moved-v1|<old_id>|<new_id>|<ts>"` — **depuis l'ancien identifiant**, à chaque contact et aux modérateurs de chaque groupe (en file
     d'attente si la personne est hors ligne) ;
  5. « Passer à la nouvelle identité » : les nouvelles phrases saisies doivent redonner l'identifiant annoncé (preuve qu'elles ont été bien notées).
     `SwitchToNewIdentity` met la nouvelle identité **dans le même profil** : contacts, historique, serveurs et profil sont gardés, les groupes sont
     archivés (le nouvel identifiant n'en est pas membre), les messages non livrés sont abandonnés, la base est re-chiffrée avec la clé des nouvelles
     phrases, puis l'appli redémarre. Les avis encore en attente ne partiront plus (signés par l'ancienne identité) : l'assistant le dit et demande confirmation.
- **Côté destinataire** (`HandleMoved`) : l'avis est ignoré sans clé épinglée pour l'expéditeur, avec une signature fausse, si `From` ≠ `old_id`, si la
  nouvelle fiche est incohérente (`discovery_id` ≠ SHA-256 des clés), ou si cet identifiant est **banni** d'un groupe que je modère. Un seul avis par
  ancien identifiant (un second, différent, est signalé comme conflit). Un **modérateur** exclut l'ancien identifiant avec **rotation de clé** dans un seul
  état (réglage `AutoExcludeMigrated`, activé par défaut ; le créateur ne peut pas l'être : il doit avoir passé la main). L'avis arrive dans « Demandes » :
  **Accepter** crée le nouveau contact (nom privé gardé, marque ✔ non reprise), marque l'ancien (`moved_to` : plus rien ne lui est envoyé, erreur
  `contact_moved`) et invite le nouvel identifiant dans les groupes d'où je l'ai exclu ; **Refuser** ne change rien d'autre. `AutoAcceptMigration`
  (désactivé par défaut) suit l'avis d'un contact sans demander, **jamais** pour un contact marqué ✔.
- **Tests** : `MigrationTests` (12, serveur réel : contact, faux avis signé par la seule clé d'identité, absence de pin, pin immuable, ✔ vs automatique,
  modérateur et membre simple, créateur qui passe la main, créateur qui ne la passe pas, banni, annonce aux groupes, bascule complète avec
  réinvitation) et essai sur trois émulateurs (deux groupes, créateur et membre simple).
- **Limites** : un contact d'avant la fonction n'a pas de pin tant qu'il n'a pas reçu un `identity_update` (envoyé quand la protection est activée dans
  Réglages → Téléphone volé ou perdu) ; un modérateur qui n'est **pas contact** de la personne ne peut pas la réinviter (l'invité n'accepte que des
  contacts, §7) ; si les **phrases** ont fuité, rien n'est protégé ; remise au mieux (le relais ne stocke rien) ; le voleur peut agir avant l'assistant
  (mêmes clés d'identité) ; le bannissement ne suit pas un successeur qui arrive par une autre voie.

### Précautions et failles possibles (identité)

- **Les phrases sont l'identité** : qui les possède *est* l'utilisateur, sur autant d'appareils qu'il veut (§1, multi-device), et
  **rien ne permet de le détecter ni de le révoquer**. Les garder sur papier, hors du téléphone, hors du cloud, jamais dans un message.
- **Pas de récupération** : phrases perdues et aucune copie = identité perdue (choix assumé). D'où l'écran de sauvegarde obligatoire
  à la création et la confirmation avant de le quitter sans rien avoir fait.
- **Rien d'écrit sur le téléphone** : ni phrases, ni QR code, ni PDF ne sont créés par l'appli ; seuls le fichier de clé (chiffré) et le
  brouillon en mémoire le sont. Limite : le brouillon est en mémoire gérée (des copies de chaînes peuvent subsister jusqu'au
  ramasse-miettes) ; accepté.
- **Affichage** : l'écran de sauvegarde n'est protégé contre la capture d'écran et l'enregistrement de l'écran que si le réglage
  **écran protégé** (Android, désactivé par défaut, `FLAG_SECURE`) est activé, et une personne derrière l'épaule peut lire.
- **Presse-papiers** : le bouton Copier place les phrases dans le presse-papiers, lisible par les claviers et les applications de
  presse-papiers ; à coller aussitôt dans un gestionnaire de mots de passe, puis à vider.
- **Vérifier un fichier de clé** (Réglages → « Vérifier mon fichier de clé ») : le fichier est ouvert avec son mot de passe, l'identité en est
  dérivée et comparée à celle de l'appareil (✔ même identité / ✘ une autre / mot de passe faux ou fichier altéré). Rien n'est restauré ni
  enregistré ; les phrases ne vivent en mémoire que le temps du calcul (quelques secondes : Argon2id deux fois).
- **Impression** : le spouleur ou un service d'impression dans le cloud peut conserver une copie, et « Enregistrer au format PDF »
  laisse un fichier en clair ; préférer une imprimante locale. Détruire ou ranger la feuille en lieu sûr : le QR code se lit sans mot de
  passe.
- **Fichier de clé** : il faut le fichier **et** le mot de passe, à garder **séparément** (le mot de passe n'est pas récupérable). La
  protection vaut celle du mot de passe : Argon2id (64 Mio) ralentit chaque essai mais ne sauve pas un mot de passe court ou courant ;
  conseiller une phrase de passe longue (le minimum de 10 caractères n'est qu'un plancher, pas une mesure d'entropie). Le fichier
  reste dans le cache de l'appli (chiffré) jusqu'à ce que le système le purge.
- **Fichier hostile** : un fichier de clé ou de sauvegarde forgé ne peut pas faire allouer plus de 256 Mio ; toute altération est
  détectée par l'AEAD.
- **Phrases saisies à la main** : leur force est celle de l'utilisateur ; Argon2id ralentit l'énumération mais ne remplace pas
  75 caractères réellement aléatoires (d'où le tirage par l'appli).
- **Sauvegarde Android** : `allowBackup=false`, pour que les secrets du Keystore ne partent pas dans la sauvegarde du système vers un autre
  téléphone.

## 2. Export / Import (backup)

- Backup chiffré : contacts confirmés + alias + historique local + groupes
  (états, clés, journal).
  **Pas l'identité elle-même** : entièrement redérivable depuis les 3
  phrases sur n'importe quel device, donc pas de clé privée à faire
  transiter par ce fichier. **Ni les phrases** : la sauvegarde ne concerne que les données.
- **Marques de lecture** (`read_marks` : conversation, côté, `ts`, `id`) : incluses, les miennes et celles du correspondant. À la restauration elles ne font qu'avancer ; une sauvegarde qui n'en porte pas marque chaque conversation restaurée comme lue jusqu'à son dernier message reçu.
- **Serveurs de l'identité** (`servers` : adresses par ordre de priorité) : inclus. À la restauration, quand l'identité a encore les serveurs que l'application lui a donnés à sa création (§4 d), le client teste ceux de la sauvegarde ; si au moins un répond, toute la liste remplace les serveurs initiaux, sinon rien ne change. Une identité dont l'utilisateur a choisi le serveur, ou dont la liste a été modifiée, n'est pas touchée.
- **Profil de l'utilisateur** (`profile` : nom, description, photo en base64) : inclus, car les clés se redérivent mais pas le
  profil. À la restauration à la **première connexion** (identité tout juste recréée, profil vide) il est remis en place ; sur une
  identité déjà utilisée (page Sauvegarde) il n'est **pas** appliqué, pour qu'une vieille sauvegarde n'écrase pas un profil plus
  récent. Un champ vide du fichier ne remplace jamais un champ rempli. Les contacts et groupes connaissent déjà le profil : rien n'est
  renvoyé. Une sauvegarde faite avant l'ajout du champ se restaure sans profil.
- Clé de chiffrement = HKDF-SHA256(clé privée de chiffrement de l'identité,
  salt="FreeSpeech-Backup", info="backup-key") → ChaCha20-Poly1305,
  déterministe. Conséquence assumée : un backup n'est lisible qu'en
  redérivant la même identité (mêmes 3 phrases) — usage prévu : transfert
  entre devices d'une même identité, pas de partage à un tiers.
- Import = fusion, jamais un remplacement : contacts upsertés (le backup
  importé l'emporte en cas de conflit), alias fusionnés, historique
  fusionné par `message_id` (le mécanisme de dédoublonnage existant du §5
  rend un import répété inoffensif).
- **Pièces jointes (§6), trois modes** : `none` (aucun fichier), `Nm` (fichiers
  des N derniers mois, **défaut 6m**), `all`. Historique et métadonnées
  toujours inclus. Le filtre porte sur la date du **message** (ts du `ref`),
  pas sur celle du fichier. À la restauration : fichier présent → restauré
  (racine de Merkle revérifiée ; rangé sous `blobs/{root}` — un fichier déjà
  présent sous cette racine n'est pas réécrit — donc à nouveau servable s'il
  a été envoyé) ; fichier laissé de côté et **reçu** → l'offre redevient
  « proposée », re-téléchargeable par `download` si l'expéditeur l'a encore ;
  fichier laissé de côté et **envoyé** → perdu (le contact ne pourra plus le
  télécharger chez nous) — la commande l'avertit explicitement.
- **Format** : `"FSBK"`‖version‖blocs. Contenu = zip (`manifest.json` +
  `history/N` + `files/N`) ; l'historique est écrit par blocs de 1 000 lignes d'une
  conversation (`history/N` = `{ conversation, entries }`), lus dans la base au fil de
  l'écriture, de sorte qu'un long historique n'est jamais chargé en entier ; la restauration
  les relit de même. Le tout est chiffré par blocs de 1 Mio, ChaCha20-Poly1305, AD = index de bloc
  ‖ drapeau « dernier » (réordonnancement, suppression de bloc ou troncature
  détectés ; mémoire bornée). Un fichier d'un seul blob JSON en base64 reste
  restaurable.
- Implémenté : `backup [path] [none|6m|all]` / `restore <path>` côté console,
  logique dans `Backup/BackupService.cs` (sans E/S console, réutilisable),
  primitives dans [BackupCrypto.cs](freespeech.client/Backup/BackupCrypto.cs).

### Archive des fichiers joints (libérer de la place)

Distincte de la sauvegarde : elle sort les **fichiers téléchargés** de l'appareil et laisse tout le reste où il est.

- **Ce qui reste.** Tous les messages, texte compris. Une pièce jointe archivée garde sa fiche (état `Archived`, sans chemin local) et, pour une
  photo ou une vidéo, sa miniature (table `thumbnails`, ARCHITECTURE §5.1 quinquies). La discussion montre la miniature, ou le nom et la taille
  pour tout autre fichier, avec une petite icône « archivé » ; un toucher dit où le retrouver.
- **Sélection.** Tous les critères donnés doivent être vrais, aucun = tout : messages plus anciens que N mois (le choix habituel, **12 par
  défaut**), une plage de temps (du / au), une discussion, un type (photos, vidéos, audio, autres), une taille minimale. La date est celle du
  message (ts de la `ref`). Un contenu tenu par plusieurs fiches (le même fichier dans deux discussions) n'est archivé que si **toutes**
  correspondent : la place est libérée, ou le fichier est laissé. Un aperçu donne le nombre de fichiers et la place libérée avant toute action.
- **Format.** Celui d'une sauvegarde (`"FSBK"`, blocs de 1 Mio, ChaCha20-Poly1305, clé dérivée de l'identité comme dans cette section), un zip
  à l'intérieur avec `archive.json` (`{ created_at, items: [ { root, size, file, records[] } ] }` : un élément par contenu, avec les fiches qui
  le désignaient, sans leur chemin local) et un `files/N` par contenu. `archive.json` remplace `manifest.json` : une sauvegarde ne peut pas être
  prise pour une archive, ni l'inverse. Elle ne se lit qu'avec la même identité.
- **Écriture.** La compression et le chiffrement tournent côte à côte par un tuyau : ni copie en clair, ni fichier temporaire aussi gros que
  l'archive. La destination est un flux positionnable choisi par la personne (sur Android le sélecteur de documents du système : carte
  mémoire, clé USB, cloud qui propose ses fichiers aux applications) : un téléphone à court de place n'a pas besoin de place pour l'archive.
  **Avant de retirer le moindre fichier**, toute l'archive est relue, chaque bloc authentifié ; au moindre échec elle est supprimée et rien n'a
  bougé. Puis chaque fiche passe à `Archived`, et son fichier, ses morceaux reçus et son blob (quand plus aucune fiche n'en a besoin) sont supprimés.
- **Chargement.** L'archive est déchiffrée (dans un zip temporaire, supprimé ensuite), chaque contenu est écrit une fois sous `blobs/{root}`
  s'il n'y est pas déjà, sa racine de Merkle est comparée à `root` (une entrée abîmée ou falsifiée est ignorée), et toute fiche qui le désigne
  redevient `Complete` ; une fiche que l'appareil n'a pas (archive faite sur un autre appareil de l'identité) est ajoutée. Charger deux fois,
  ou une archive dont les fichiers sont déjà là, ne duplique rien.
- **Quand le verrouillage est actif** (PIN), créer une archive redemande le PIN, comme une sauvegarde.
- Réalisé : `Archive/ArchiveService.cs` (`Preview`, `Create`, `Restore`, sans entrée-sortie propre hors des flux qu'on lui donne), écran
  « Archiver les fichiers » de l'application mobile (page Sauvegarder / Restaurer), `FreeSpeechClient.Archive`.

## 3. Fiche de contact (ContactCard)

```json
{
  "nickname": "...",
  "photo": "base64 JPEG, <100KB",       // optionnel
  "description": "texte libre, ≤ 300 car.",   // optionnel
  "discovery_id": "...",
  "encryption_public_key": "...",
  "signing_public_key": "...",
  "servers": [                          // liste ORDONNÉE par préférence
    { "url": "https://public.freespeech.org", "priority": 0 },
    { "url": "https://circle.invalid", "priority": 1, "restricted": true, "circle": "base64" }
  ],
  "enable_push": false,                 // capacité annoncée, pas le token
  "created_at": ...,
  "version": "2.0"
}
```

- Deux modes d'échange, via un OTC (*One-Time Code*, chaîne
  alphanumérique 16 caractères) embarqué dans la première carte et servant de
  clé symétrique (HKDF-SHA256 → ChaCha20-Poly1305) pour chiffrer la carte
  réponse :
  - **qr** (auto-validé) — QR/Bluetooth en personne, OTC valide **10 min**,
    import sans confirmation.
  - **manual** — partage distant (copier/coller, email/cloud), OTC valide
    **1 mois**, validation manuelle obligatoire dans tous les
    cas.
- `servers` : la liste des serveurs de l'identité (cf. §8 routage multi-serveur).
- **Nom donné à un contact (deux « Thomas »).** Le carnet garde, en plus du `nickname` annoncé par le contact,
  un `local_name` que **moi seul** vois (jamais envoyé) : il prime à l'affichage (`DisplayName`) et dans la recherche
  par nom. Un `identity_update` qui change le pseudo du contact ne l'efface pas. `local_name` et `verified_at` font
  partie de la sauvegarde (§2) et sont restaurés avec les contacts. Vide = on reprend le pseudo du contact.
- **Numéro de sécurité (vérification contre l'interception).** Un intercepteur présent lors de
  l'échange des cartes peut y glisser ses propres clés ; ensuite tout paraît normal. La parade est
  de comparer, hors du canal, un numéro que seuls deux interlocuteurs légitimes ont en commun :
  `SHA-512` itéré 5 200 fois sur `"FreeSpeech-safety-v1" ‖ A ‖ B`, où `A` et `B` sont les deux
  paires de clés publiques (`enc ‖ sign`, 64 octets) **triées dans l'ordre des octets**, de sorte que
  les deux côtés calculent la même valeur ; 12 groupes de 5 chiffres (les 5 octets du groupe modulo
  100 000), affichés sur quatre lignes. Si quelqu'un a substitué des clés, chaque côté détient une autre
  clé pour l'autre et les deux numéros diffèrent. Le contact comparé est marqué `verified_at` dans
  le carnet. Un `identity_update` dont les clés changent reste rejeté (§5).

## 4. Première connexion

- a) Import d'une fiche de contact → apprentissage de son/ses serveur(s) →
  enregistrement dessus si pertinent.
- b) Ajout manuel d'un serveur connu (notamment tout serveur **privé**, qui
  n'est découvrable d'aucune autre façon).
- c) Aucun serveur connu → `localhost` par défaut (placeholder dev ; la valeur
  de prod reste à définir au déploiement, hors protocole).
- d) Recherche d'un relais (une nouvelle identité, ou aucun des relais de l'identité n'est joignable). Une **nouvelle identité**, sur une application qui connaît des annuaires, les interroge (§10 « Annuaire ») :
  les premiers relais qu'ils proposent deviennent ses serveurs ; si aucun ne répond, l'un des relais de repli intégrés à l'application (`FsFallbackServers`), tiré au hasard, prend leur place. Le serveur par défaut de l'application n'est gardé que lorsqu'elle n'a ni annuaire ni repli.
  Ces serveurs initiaux peuvent encore changer une fois : une sauvegarde de l'identité (§2) qui liste des serveurs les remplace quand l'un d'eux répond, et le tout premier contact aussi, quand il est sur d'autres serveurs et que l'un d'eux répond.
  Ni l'un ni l'autre n'a lieu quand l'utilisateur a choisi un serveur ou modifié la liste (une application lancée avec un serveur choisi par l'utilisateur ne donne ni annuaire ni repli). Quand aucun des relais de l'identité n'est joignable,
  la bibliothèque cliente propose d'abord les relais de ses contacts, dans un ordre aléatoire, et les annuaires seulement si tous les contacts sont sur un relais déjà essayé (ou s'il n'y a aucun contact).
  Un client qui a un relais ne contacte jamais un annuaire.
- Le client fonctionne sans aucun serveur configuré : l'identité existe
  indépendamment de toute connectivité.

## 5. Communications 1-à-1

### Identifiant de message (unifié uid + causalité)
```
message_id = discovery_id_émetteur ":" ts        (texte)
ts         = ticks UTC (100 ns) strictement croissants sur l'installation ; les 12 bits de
             poids faible sont une étiquette aléatoire tirée une fois par exécution
```
- Pas de compteur global partagé → aucune collision entre émetteurs différents.
- **Plusieurs appareils d'une même identité** : deux appareils qui émettent au même instant
  doivent avoir le même créneau de 0,4 ms **et** la même étiquette (1 chance sur 4096) pour
  produire le même `message_id` — négligeable, et sans changer le format. Un client
  incrémente son `ts` de 4096 s'il émet plus vite qu'un message par 0,4 ms (la date reste lisible).
- Sert à la fois d'id unique et d'ordre causal (pas d'horloge serveur requise).
  `ts` est un **départage**, pas une preuve d'ancienneté : un `ts` lointain (futur) est
  possible, c'est pourquoi les opérations concurrentes (profils, bannissements, §7) ne
  s'appuient pas seulement sur lui pour la sécurité.
- Horodatage : serveur de temps si accès internet, sinon horloge locale.
  Le serveur expose son horloge UTC (`TimeController`, `GET /api/time`)
  pour caler `NextConnectAt` (§9) sans dépendre de l'horloge
  locale du device, qui elle sert uniquement d'ordre causal ci-dessus.

### Chiffrement (`MessageCrypto.cs`)
```
Enveloppe { from, to, crypto, to_server?, msg: base64 }
  crypto = 0 : OTC symmetric — clé = HKDF-SHA256(otc) → ChaCha20-Poly1305
               (échange de ContactCard, §3)
  crypto = 1 : asymétrique — ECDH X25519 éphémère (nouvelle paire par
               message) → HKDF-SHA256(secret partagé) → ChaCha20-Poly1305
               (nonce 12o aléatoire, tag Poly1305 = intégrité)
               sortie = ephemeral_pub(32o) ‖ nonce(12o) ‖ ciphertext
Letter (contenu déchiffré, crypto=1) { type, payload, sig, orig_to? }
  type    : le type de la lettre (liste ci-dessous)
  payload : une chaîne : le texte lui-même pour « message », sinon une chaîne JSON (liste ci-dessous)
  sig     = Ed25519("freespeech-letter-v1|" + from + "|" + to + "|" + type + "|" + payload + "|" + ts),
            vérifiée avec la signing_public_key du contact stockée localement
            (ou la nôtre pour un self-sync copy de message, cf. note ci-dessus).
            from/to = discovery_id de l'enveloppe ; pour un self-sync copy (enveloppe
            adressée à soi-même) `to` = `orig_to`, le vrai destinataire.
  orig_to : présent uniquement sur un self-sync copy d'un "message" — vrai
            destinataire, l'enveloppe elle-même étant adressée à soi-même
```
- **Pourquoi `from` et `to` sont signés** (changement du 2026-10-05) : sans eux, un destinataire B
  pouvait déchiffrer la lettre de A, la rechiffrer pour un tiers C et la lui présenter comme venant
  de A (la signature restait valable). Liée à `to`, elle n'est valable que pour son vrai destinataire.
  `orig_to` ne compte que sur une enveloppe adressée à son propre émetteur ; ailleurs, c'est le `to`
  de l'enveloppe qui est vérifié. Étiquette de domaine `freespeech-letter-v1` : une signature de
  lettre ne peut pas être prise pour une signature d'un autre type (groupes, profils, etc.).
- Signature **avant** chiffrement (sign-then-encrypt) : elle voyage à
  l'intérieur du contenu chiffré plutôt que dans un champ séparé de
  l'enveloppe — celle-ci reste `{ from, to, crypto, msg, ts, reply_to }`,
  inchangée. Le payload de chaque type est donné dans la liste ci-dessous et dans [PROTOCOL_API.fr.md](PROTOCOL_API.fr.md).
- **Envoi vers un contact non confirmé impossible** : sans sa
  `encryption_public_key` (obtenue par l'échange OTC, §3), le client refuse
  d'envoyer (pas de repli en clair) — message d'erreur explicite invitant à
  faire l'échange de contact d'abord.

### Types de Letter
```
message         texte — le payload est le texte lui-même ; reply_to est un champ de l'enveloppe
read_ack         { target } — accusé de lecture : « j'ai lu jusqu'à ce message (inclus) » (1-à-1, §5)
typing           { group? } — indicateur « écrit… » (voir ci-dessous) ; lettre éphémère
wipe             { ts, sig } — ordre d'effacement à distance, à soi-même uniquement (voir §1) ; sig = Ed25519 de la clé de révocation
                 sur "FreeSpeech-wipe-v1|<discovery_id>|<ts>" (ts = Unix ms)
identity_moved   { old_id, new_card, ts, sig } — migration vers une nouvelle identité (voir §1) ; sig = Ed25519 de la clé de révocation de
                 l'ANCIENNE identité sur "FreeSpeech-moved-v1|<old_id>|<new_id>|<ts>" ; envoyée depuis l'ancien identifiant aux contacts et
                 aux modérateurs de groupe ; acceptée d'un contact ou d'un membre d'un groupe commun ; contrôlée contre la clé épinglée
identity_update { ContactCard mis à jour — clés crypto immuables }
route_policy     { open_fallback } — autorise (vrai) ou non un serveur ouvert pour mes messages avec ce contact quand nos serveurs restreints communs ne répondent pas (§ 8)
attachment       { name, mime, size, root, thumbnail? } — vignette envoyée directement (mobile)
attachment_request { ref, missing? } — ref = message_id du Letter attachment
attachment_chunk { ref, chunk_id, total_chunks, data, proof[] } — proof = preuve de Merkle
group_state      { state, signer, sig } — composition + clé de l'époque (§7)
group_sync       { group_id, epoch, have{sender: seq} } — rattrapage p2p (§7)
group_relay      { states[], keys[], messages[], more } — réponse au rattrapage (§7)
group_leave      { group_id } — un membre demande aux modérateurs de le retirer (§7)
blacklist_update { group_id, ops[] } — opérations de blacklist signées (§7)
group_pin        { group_id, ops[] } — messages épinglés ou libérés, signés par un modérateur (§7)
group_profile    { group_id, name, photo?, description?, ts, signer, sig } — nom/image du groupe, par le créateur (§7)
member_profile   { group_id, id, nickname, photo?, description?, ts, sig } — nom/image d'un membre, signé par lui (§7)
attachment_have  { ref, have, complete, busy } — ce qu'un membre détient d'un fichier de groupe (§7)
device_sync_request { id, device, have, contacts, ts, code } — un appareil restauré demande à être reflété par un autre de la même identité (§1)
device_sync_data { request, to_device, contacts[], replace[], remove[], history, attachments[], last, refused } — la réponse, en parties (§1)
message_edit     { target, text } — l'auteur remplace le texte d'un de ses messages (§5)
message_retract  { target, seq? } — l'auteur supprime un de ses messages pour tous (§5)
attachment_retract { target, seq? } — l'auteur retire le fichier d'un de ses messages pour tous (§5)
group_suggest    { group_id, candidate, ts, sig } — un membre propose un de ses contacts aux modérateurs (§7)
message_react    { target, text } — réaction emoji à un message ou un fichier ; texte vide = réaction retirée (§5)
ack              { ids } — confirmation que des Letters ont été gardées par le destinataire (§5, « Livraison confirmée »)
```

### Mise en forme du texte
Le texte d'un `message` est transmis tel que saisi. Il est **mis en forme à l'affichage seulement si sa première ligne est exactement `md`** (casse libre, espaces
de fin permis) et qu'une autre ligne la suit ; la ligne `md` n'est pas affichée, et tout autre message est montré tel quel. Un client qui ne connaît pas cette
convention affiche le texte brut, qui reste lisible. Sous-ensemble de Markdown lu (au plus 8 000 caractères) :
- titres `#`, `##`, `###` suivis d'une espace (au-delà de trois dièses : le plus petit) ;
- listes : `-`, `*` ou `+` suivis d'une espace, ou un nombre suivi de `.` ou `)` et d'une espace ; affichées **au bord gauche** avec une puce `•` ou le chiffre
  (l'indentation saisie d'un élément imbriqué est conservée), sans retrait suspendu ;
- citation `>` (barre `▎` devant la ligne), ligne `---` (filet) ; un peu d'air (une courte ligne vide) sépare une ligne de texte d'une ligne de liste ou d'un titre, et une liste d'un titre ;
- en ligne : `**gras**`, `*italique*` ou `_italique_`, `~~barré~~`, `code` entre apostrophes inversées (rien n'est lu à l'intérieur), `[texte](https://…)` et adresses `http(s)://…` nues ;
  une étoile ou un tiret bas simple ne coupe jamais un mot (`snake_case`, `2*3*4`) ; une barre oblique inverse affiche le signe suivant tel quel ;
- ni bloc de code, ni tableau, ni image, ni HTML ; seuls les liens `http` et `https` sont reconnus.
Les aperçus (liste des discussions, notification, citation en réponse) montrent le texte sans signes. Un lien n'est ouvert qu'après confirmation montrant l'adresse réelle.

**Compatibilité entre versions (règle)** : un client qui reçoit un type de Letter qu'il ne connaît pas **l'ignore en silence** (rien dans
l'historique, aucune notification, aucune alerte) ; la signature est vérifiée avant, donc un type inconnu forgé n'a aucun effet. Cela
permet d'ajouter des types sans gêner les versions déjà installées (bêta-test). Conséquence : une fonction qui repose sur un nouveau type
(par exemple `typing`) ne marche qu'entre clients qui le connaissent. Le serveur, lui, relaie les Letters sans lire leur type.

### Lu / non lu et accusé de lecture (1-à-1)
- Chaque appareil garde, par conversation, un **repère de lecture** : la position (`ts` d'origine, puis `message_id`)
  jusqu'à laquelle j'ai lu ce que l'autre a envoyé. Le **nombre de non lus** = les messages reçus après ce repère ;
  il tombe à 0 quand la conversation est affichée (application au premier plan).
- À l'ouverture (et à chaque message reçu pendant que la conversation est ouverte), le client envoie **un**
  `read_ack { target }` au contact, `target` = le dernier message reçu. Un seul accusé couvre tous les messages
  précédents. Envoi **au mieux** : pas de file d'attente, pas dans l'historique ; s'il se perd, le suivant le rattrape.
- Le récepteur n'honore l'accusé que si `target` est **un message dont il est l'auteur** ; son repère « lu par
  l'autre » n'avance que vers l'avant. L'interface affiche ✓ (envoyé) puis ✓✓ dorées (lu).
- L'utilisateur peut **désactiver l'envoi** des accusés (réglage « Accusés de lecture »). Pas d'accusés dans un
  groupe (confidentialité, coût O(N)) : seul le compteur de non lus y existe. Le repère n'est pas synchronisé entre
  les appareils d'un même utilisateur (reporté).

### Indicateur « écrit… » (`typing`)

Lettre **éphémère**, chiffrée et signée comme les autres (§5) mais : **jamais** stockée dans l'historique, **jamais** mise dans l'outbox,
**jamais** de flag WAITING — elle ne peut donc **pas réveiller** un téléphone, et si le destinataire n'est pas connecté elle est perdue
(voulu). Contenu : `{}` en 1-à-1 ; `{ "group": groupId }` en groupe.
- **Émetteur** : pendant que l'utilisateur tape (champ non vide, appli au premier plan), **un signal au plus toutes les 4 s** par
  conversation. En groupe, une lettre par membre : réservé aux groupes de **8 membres au plus** (au-delà, rien).
- **Récepteur** : affiche « écrit… » (en groupe : « Marie écrit… ») sous le nom dans la discussion **ouverte**, l'efface **6 s** après le
  dernier signal ou à l'arrivée du message. Un signal n'est pris en compte que d'un **contact confirmé** ou, avec `group`, d'un **membre**
  de ce groupe (la signature de la lettre désigne l'auteur).
- **Réciproque** : le réglage « Indicateur d'écriture » (activé par défaut) coupe l'envoi **et** l'affichage ; désactivé, un signal reçu est
  ignoré.
- **Vie privée** : l'indicateur dit *quand* l'utilisateur écrit ; le serveur voit passer une lettre de plus, sans contenu (même
  métadonnée que les autres lettres, §13). Code : `FreeSpeechClient.Typing.cs`.

### Réactions (emoji)
- `message_react { target, text }` : `text` est **un seul emoji** (4 éléments de texte au plus : drapeaux, teintes de
  peau, familles) ; vide = retirer la réaction. Un texte contenant une lettre ou un chiffre est refusé : un mot n'est
  pas une réaction.
- Contrairement à une modification, **n'importe quel participant** peut réagir à n'importe quel message ou fichier.
  En groupe c'est un message de groupe (§7) de même type, en 1-à-1 une Letter chiffrée et signée (§5).
- Une personne a au plus **une réaction par message** : la plus récente (`ts` de la Letter) l'emporte, une réaction
  plus ancienne arrivée en retard est ignorée. Elle peut arriver avant le message visé : elle est gardée telle quelle.
  Réagir de nouveau avec le même emoji la retire.
- Même garantie « au mieux » que les autres changements ; les réactions ne sont pas synchronisées entre les
  appareils d'un même utilisateur (reporté).

### Règle identity_update
```
si updated.encryption_public_key != existing.encryption_public_key
   OU updated.signing_public_key  != existing.signing_public_key
alors REJET (changement de clé = usurpation potentielle)
sinon: nickname / photo / servers peuvent changer librement
```
**Câblé dans la bibliothèque cliente** ([FreeSpeechClient.cs](freespeech.client/FreeSpeechClient.cs)
`HandleIncomingIdentityUpdate`, [FreeSpeechClient.Account.cs](freespeech.client/FreeSpeechClient.Account.cs)
`SendIdentityUpdateAsync`) : tout changement de la liste de serveurs
(`server add|remove`, §8) ou du profil (`profile name|about|photo`) envoie un
`identity_update` à chaque contact confirmé — chaque envoi passe par le chemin
normal (live, ou mis en file d'attente dans l'outbox comme un message de chat ; la fiche la plus récente remplace celle qui attend encore).

### Livraison confirmée (accusé de réception)

Le relais déclare une Letter « livrée » dès qu'il l'a écrite sur la connexion du destinataire, sans accusé : si cette connexion est déjà morte (réseau perdu, processus
tué), la Letter est perdue sans que personne le sache. Le destinataire confirme donc ce qu'il garde :
- **`ack { ids }`** : Letter signée et chiffrée comme les autres, envoyée par le destinataire à l'expéditeur ; `ids` = les `message_id` (`{expéditeur}:{ts}`) des Letters
  gardées, regroupées sur 300 ms. Elle est éphémère (ni outbox, ni historique) et n'est elle-même pas confirmée.
- **Types confirmés** : `message`, `attachment`, `message_edit`, `message_retract`, `attachment_retract`, `message_react`, `identity_update`, `route_policy` — tous ceux qui passent par
  l'outbox, d'un contact confirmé. Un message, une modification et une suppression suivent donc le même chemin.
- **Expéditeur** : une Letter livrée reste dans l'outbox, marquée « livrée, non confirmée », jusqu'à l'`ack`. Sans `ack`, elle est renvoyée telle quelle (même `ts`, donc même
  identifiant) après 30 s, 2 min, 10 min, 1 h puis 6 h ; après le dernier délai elle est abandonnée (une version qui ne confirme pas ne reçoit pas la Letter sans fin). Un
  contact injoignable la remet en attente comme toute Letter de l'outbox (flag WAITING). Un message supprimé avant sa confirmation n'est plus renvoyé : seule la suppression part.
- **Destinataire** : une Letter déjà vue n'est pas traitée deux fois (déduplication par `{expéditeur}|{destinataire}|{ts}|{crypto}`) mais elle est **confirmée de nouveau**, pour
  qu'une confirmation perdue ne fasse pas renvoyer la Letter sans fin.
- Les groupes n'ont pas d'`ack` : un membre qui a manqué un message le rattrape auprès des autres (§7).
### Modifier et supprimer un message (contact et groupe)

| Action | Portée | Mécanisme |
|---|---|---|
| Modifier | tous | Letter `message_edit { target, text }` |
| Supprimer chez moi | cet appareil | local, aucun message envoyé |
| Supprimer pour tous | tous | Letter `message_retract { target, seq? }` |
| Retirer le fichier chez moi / pour tous | cet appareil / tous | local / Letter `attachment_retract { target, seq? }` |

Dans un groupe, ces Letters sont des messages de groupe (§7) de même type ; en 1-à-1 ce sont des
Letters ordinaires (chiffrées, signées, §5).


- `target` est le `message_id` visé (`{auteur}:{ts}`). **Chaque récepteur n'honore le changement que si
  l'expéditeur de la Letter est l'auteur du message visé** (préfixe du `message_id`) : personne ne peut
  modifier ni supprimer le message d'autrui — pas même un modérateur de groupe (reporté, §12). La signature
  de la Letter (§5) ou du message de groupe (§7) en est la preuve.
- **Modifier** : le `ts` de la Letter sert de version ; la plus récente l'emporte, une édition plus ancienne
  arrivée en retard est ignorée. L'id du message ne change pas : les `reply_to` qui le visent restent valides.
  Le message s'affiche « modifié ». Seul un message texte est modifiable.
- **Supprimer pour tous** : le contenu est **effacé** ; la ligne reste (« message supprimé ») pour que les
  réponses y restent rattachées. Une suppression l'emporte sur toute modification, dans n'importe quel ordre
  d'arrivée. Pour un message-fichier, le fichier local est supprimé aussi ; **retirer le fichier seul** laisse la
  ligne (« fichier retiré »).
- **Ordre non garanti** : un changement peut arriver avant le message (rattrapage, plusieurs serveurs). Il est
  alors mis en attente (table `message_ops`) et appliqué dès que le message est stocké ; un message déjà
  supprimé à son arrivée n'est pas annoncé.
- **Hors ligne (1-à-1)** : la Letter passe par l'outbox comme un message. Un message **pas encore livré** est
  simplement modifié, ou retiré, dans l'outbox : rien n'est envoyé. Les changements ne sont pas synchronisés
  entre appareils d'une même identité (reporté, §12).
- **Supprimer chez moi** : le contenu est effacé et la ligne cachée. La ligne reste en base, masquée, pour
  qu'une nouvelle livraison (rattrapage de groupe) ne la ramène pas ; elle est exclue du backup. Un message-fichier
  perd son fichier, sauf s'il est partagé (même racine de Merkle, §6) avec une autre discussion.
- **Garantie au mieux** : un client modifié peut ignorer une modification ou une suppression, et le destinataire a
  pu copier, transférer ou sauvegarder le message avant. Aucune limite de durée n'est imposée.

## 6. Pièces jointes (à la demande, pas de push automatique)

```
A envoie: attachment (vignette légère, comme un message normal)
B décide: veut le fichier complet ?
  non → rien de plus, B a pu trier sans télécharger
  oui → B envoie attachment_request { ref }
        un détenteur répond par attachment_chunk[] (chunké, chaque chunk chiffré
        individuellement, AEAD ChaCha20-Poly1305, et accompagné de sa preuve
        de Merkle)
        B vérifie CHAQUE chunk dès réception contre `root`, puis réassemble
```

### Identité du contenu (racine de Merkle)

- Chunks de 48 Kio fixes ; feuille = SHA-256(0x00 ‖ chunk), nœud = SHA-256(0x01 ‖
  gauche ‖ droite) ; un nœud sans frère (nombre impair) est **promu tel quel**,
  jamais dupliqué. Le nombre de chunks se **déduit de `size`**, il n'est pas
  transmis.
- `root` (dans l'offre) = SHA-256(0x02 ‖ size sur 8 octets ‖ racine de l'arbre) :
  la taille est liée à l'identifiant, donc un même arbre ne peut pas être
  « vérifié » avec un autre nombre de chunks, et deux `root` égaux impliquent des
  tailles égales (déduplication sûre).
- Chaque chunk porte sa preuve (hashes frères, feuille → racine, environ 10 pour
  25 Mio et 16 pour 2 Gio) : n'importe quel détenteur peut servir, un chunk corrompu est rejeté
  immédiatement (avec attribution du pair fautif) au lieu d'échouer à la fin.
- **Un contenu = un fichier local** : stocké sous `blobs/{root}.ext` quelle que
  soit la discussion. Transférer un fichier vers une autre discussion
  (`forward`) est une nouvelle offre avec le même `root` : rien n'est copié ni
  re-haché ; un destinataire qui possède déjà ce contenu n'a rien à télécharger.
  L'arbre (feuilles) est conservé à côté pour servir sans re-hasher.

- Reprise possible sur connexion instable (chunk_id explicite).
- Si A hors ligne au moment de la demande → même négociation WAITING/READY
  que pour un message texte, déclenchée spécifiquement pour cette requête.
- Groupes (p2p) : seul le flux entre demandeur et détenteur du fichier est
  chunké — pas de diffusion automatique à tout le maillage.
- **Réglage réseau** (« fichiers seulement en wifi »), propre à l'appareil et hors protocole : sur données mobiles, selon le choix de l'utilisateur
  (toujours / wifi uniquement / wifi sauf fichiers de 1 Mio ou moins), ni les chunks demandés ni les chunks servis ne partent ; messages, accusés,
  états de groupe et `attachment_have` passent toujours. Un téléchargement retenu reste `Receiving` avec l'indicateur `waiting_network` (« en attente
  du wifi »), reprend seul au retour du wifi, ou tout de suite par « télécharger quand même » (ce fichier seulement). Un détenteur sur données mobiles
  répond `busy` aux membres d'un groupe (silence en 1-à-1). L'application indique le type de réseau à la bibliothèque ; un client console l'ignore
  (aucune restriction).
- **Tailles.** Le protocole porte un fichier jusqu'à 2 Gio ; une offre plus grosse est ignorée. En dessous,
  chaque côté décide, hors protocole. Avant d'envoyer, l'application demande une confirmation pour tout fichier
  plus gros qu'une taille réglée par l'utilisateur (25 Mio par défaut, 0 = jamais demandé), comptée telle que le
  fichier partira, une fois les images et vidéos réduites ; elle montre la taille et, quand les serveurs en
  permettent moins aujourd'hui, ce qui reste du quota du jour (§11 « Limites de volume »). Un fichier reçu
  montre sa taille tant que l'utilisateur ne le demande pas ; seul un fichier sous la taille de téléchargement
  automatique (un réglage) vient tout seul. Un téléchargement ne démarre que si l'appareil peut contenir les
  chunks manquants et, à côté, le fichier assemblé (`not_enough_space`, avec la place nécessaire et la place
  libre). Quand un relais refuse de porter davantage pour le moment, l'envoi vers lui est suspendu jusqu'à son
  `retry_after` ; les lettres attendent dans la boîte d'envoi et le receveur redemande les chunks manquants.

### Implémentation client (bibliothèque cliente, `Attachments/`)

- Letters `attachment`, `attachment_request`, `attachment_chunk` : payload JSON
  dans le Letter habituel (chiffré + signé). L'`uid` du fichier est le
  `message_id` du Letter `attachment` (`from:ts`), pas un champ répété.
  `attachment` = `{name, mime, size, root, thumbnail?}`,
  `attachment_request` = `{ref, missing?}` (`missing` absent = tout),
  `attachment_chunk` = `{ref, chunk_id, total_chunks, data(base64), proof[]}`.
- Chunks de 48 Kio bruts, un envelope par chunk (ts strictement croissant :
  il fait partie du message_id, de la signature et de la clé de dédoublonnage
  du pool). Plafond de 2 Gio (`AttachmentService.MaxSize`). Validation côté receveur : taille sous ce plafond, nombre de
  chunks déduit de `size`, taille exacte de chaque chunk, refs connues, preuve
  de Merkle valide ; nom de fichier assaini (pas de chemin). Seul un échec de
  preuve compte comme « strike » (3 = téléchargement abandonné, réessayable) ;
  un chunk simplement malformé est ignoré. L'expéditeur n'a plus à être le
  contact d'origine : c'est la preuve, pas l'émetteur, qui fait foi.
- Un détenteur refuse de servir un chunk dont son propre exemplaire ne
  correspond plus à l'arbre (fichier local endommagé). La politique « qui peut
  demander » est injectée (`authorize`) : en 1-à-1, seul le destinataire d'un
  fichier envoyé ; en groupe, « le demandeur est membre du groupe » (§7).
- Le serveur ne stocke rien : l'expéditeur garde le fichier (`blobs/`) et doit
  être en ligne (ou un autre détenteur, en groupe). Reprise = présence des
  `parts/{ref}/{n}.bin` ; si
  aucun progrès pendant 20 s (tick de 30 s), le receveur re-demande seulement
  les chunks manquants. Demande sans expéditeur joignable → flag WAITING.
  `attachment_request` et chunks ne passent **pas** par l'outbox (le timer de
  reprise du receveur fait office de retry) ; l'offre `attachment`, si.
- Pas de self-sync des pièces jointes (l'autre device n'a pas le fichier) ; le
  backup les couvre selon le mode (§2) ; pas de vignette côté console (réservée
  au mobile, pas de lib image).
- Commandes : `upload <contact> <path>`, `download <contact> <#>` (index de
  `history`), `forward <source> <#> <destinataire>` (texte : renvoyé tel quel ;
  fichier : nouvelle offre de même `root`, refusée si le fichier n'est pas déjà
  téléchargé), `files`. Tests : `sources/freespeech.tests` (68 unitaires) et
  `scripts\test-attachments.ps1` (bout-en-bout, y compris `forward`).

## 7. Groupes

### Modèle
- Pas d'administrateur pour le **routage** des messages (tout membre peut
  relayer à tout autre).
- Un modérateur existe pour la **composition** du groupe : le créateur par
  défaut, qui peut en désigner d'autres.
- `group_id` = 16 octets aléatoires (hex), choisis par le créateur.
- Les membres ne sont **pas forcément contacts entre eux** : l'état du groupe
  embarque, pour chaque membre, ses clés publiques et ses serveurs
  (auto-certifié : `discovery_id` = SHA-256(enc_pub ‖ sign_pub), donc un
  modérateur ne peut pas substituer les clés d'un id existant).
- **Pas d'historique dans le GroupState** : il ne décrit que l'état courant.
  Les états/époques passés et les anciennes clés ne sont conservés que
  **localement** par chaque client (lire l'historique, valider les messages
  d'époque ancienne). Idem pour le journal de messages servant au rattrapage.
- Taille visée : plusieurs centaines de membres (envoi O(N) par message, §12).

### GroupState — l'unité signée (carte + clé de l'époque, un seul message)
```
state = JSON sérialisé UNE fois ; la signature porte sur ces octets exacts
        (aucune canonicalisation à réimplémenter). Le champ `state` de la Letter est une
        CHAÎNE : un blob opaque. Un récepteur ne le reconstruit ni ne le re-sérialise jamais
        (il le relaie et le vérifie tel quel) ; deux JSON équivalents mais sérialisés
        autrement auraient des signatures différentes.
{ group_id, epoch, prev_hash, created_at,
  name, photo?, created_by, moderators[],
  members[ { discovery_id, nickname, encryption_public_key,
             signing_public_key, servers[] } ],
  key_check }                    ← HKDF-SHA256(group_key, info="grp-check"), 32 o
Letter "group_state" { state: "<json>", signer, sig: Ed25519(signer, state),
                       key_envelope }   ← group_key chiffrée pour CE destinataire seul,
                                          hors signature (vérifiée par key_check)
```
- `group_key` = 32 octets aléatoires **par époque**. `prev_hash` = SHA-256 du
  `state` précédent (null à l'époque 1). `key_envelope` au format crypto=1
  (X25519 éphémère, HKDF, ChaCha20-Poly1305) : chaque destinataire ne reçoit
  que la sienne, jamais celles des autres.
- **Rotation et mise à jour de la carte sont atomiques** : la clé est dans le
  même message que la composition. Un membre exclu ou parti n'apparaît pas
  dans `members` et ne reçoit **pas de clé** ; il reçoit néanmoins l'état signé
  **sans `key_envelope`** pour apprendre qu'il est sorti (il arrête d'envoyer),
  sans rien pouvoir déchiffrer de la suite.
- **Aucune enveloppe n'est conservée ni rediffusée.** Un membre absent pendant
  la rotation récupère l'état signé auprès de n'importe quel pair, qui **re-chiffre
  lui-même** la clé pour lui (il la connaît et la clé publique du retardataire
  figure dans `members`). Le retardataire vérifie la clé reçue avec `key_check`
  (signé par le modérateur) : un pair ne peut donc pas lui glisser une fausse
  clé. Un pair ne remet une clé d'époque qu'à un membre de cette époque (états
  conservés localement). Coût de la rotation : O(N), un état + une enveloppe par
  membre.
- Le modérateur n'a pas à rester en ligne : la remise aux absents est
  entièrement p2p.

### Règles d'adoption d'un état S reçu (par n'importe quel chemin)
1. **Création** (epoch 1) : acceptée seulement en tant qu'invitation d'un
   contact confirmé, signée par `created_by`, listant le destinataire. Sinon
   ignorée. **L'invité décide** : l'invitation (état signé + enveloppe de clé, conservés tels que reçus)
   attend dans une liste d'invitations ; rien n'est rejoint, aucune clé n'est adoptée, l'inviteur n'apprend rien
   avant la réponse. **Accepter** adopte l'état comme s'il venait d'arriver (puis rattrapage p2p) ; **refuser**
   supprime l'invitation et garde une marque, pour que le même groupe ne redemande pas. (Réglage de la
   bibliothèque `RequireInvitationApproval`, activé par les clients ; désactivé il rejoint tout de suite, pour les
   tests.) **Ré-invitation** : un état qui rajoute quelqu'un
   qui avait quitté le groupe (ou en avait été exclu) attend de la même façon sa réponse ; accepter le réactive (le
   groupe archivé localement redevient vivant, avec son historique). **Proposer un contact (parrainage)** : un membre non modérateur envoie à chaque modérateur un
   `group_suggest { group_id, candidate, ts, sig }` (`candidate` = la fiche de membre de son contact, `sig` = sa
   signature sur `gsponsor|{groupe}|{candidat}|{ts}`) ; le modérateur le vérifie (expéditeur membre, signature, clés
   du candidat cohérentes avec son id, ni déjà membre ni banni) et le garde en attente. **Inviter** la proposition
   ajoute le candidat (nouvelle époque) et joint à son invitation la preuve du parrain `{ id, ts, sig }`. L'invité,
   qui ne connaît pas forcément ce modérateur, **accepte l'invitation d'un non-contact si** la preuve vient d'un
   membre de l'état qui est **son** contact confirmé et que sa signature couvre ce groupe et lui-même. Il répond
   ensuite comme pour toute invitation. Sans proposition, un modérateur n'invite directement qu'un de ses contacts.
2. **Suite** : `S.epoch = courant + 1`, `S.prev_hash = hash(état courant)`,
   signataire ∈ `moderators` de l'état **courant**.
3. **Époque en avance** (`S.epoch > courant + 1`) : mis en attente ; les états
   intermédiaires sont demandés aux pairs (`group_sync`) et appliqués dans
   l'ordre — l'autorité se vérifie toujours de proche en proche.
4. **Concurrence** (deux états valides distincts pour la même époque, ex.
   deux modérateurs excluent en même temps) : gagne le plus petit
   SHA-256(state). Le perdant est **conservé** (sa clé reste lisible : des
   messages y ont pu être scellés), et son modérateur ré-émet son changement
   par-dessus le gagnant (époque suivante).
5. Un client absent des `members` du nouvel état est exclu : il cesse
   d'envoyer ; il garde localement ce qu'il a déjà.

### Messages de groupe
```
Envelope { from, to = chaque membre, crypto = 2, ts,
           msg = base64( tag(8) ‖ nonce(12) ‖ ChaCha20-Poly1305(group_key, Letter) ) }
tag    = HKDF-SHA256(group_key, info="grp-tag")[0:8]
         — identifie la clé (donc groupe + époque, y compris deux clés
         concurrentes d'une même époque) sans révéler group_id au relais
Letter { type, payload, group_id, epoch, seq, sig }
sig    = Ed25519(expéditeur, type|payload|ts|group_id|epoch|seq)
message_id = (from, ts), inchangé (§5) ; seq = compteur par (groupe, expéditeur),
         sert uniquement à détecter les trous pour le rattrapage
```
- Émission : une enveloppe (même chiffré) par membre courant, O(N) envois.
  **Pas d'outbox par destinataire** : un membre hors ligne rattrape auprès des
  pairs (voir ci-dessous) — le message est aussi dans le journal local de
  l'expéditeur, qui le sert comme n'importe quel pair. Seules les
  **invitations** (`group_state` vers un nouveau membre) sont mises en file
  d'attente et retentées, puisque l'invité ne connaît pas encore le groupe. Un
  `send` serveur multi-destinataires est une optimisation reportée (§12).
- Réception : retrouver la clé par `tag`, déchiffrer, vérifier `sig` avec la
  `signing_public_key` du membre dans l'état de cette époque.
  - Expéditeur ∈ `members` courant → accepté.
  - Expéditeur **retiré** : rejeté en livraison directe ; accepté seulement s'il
    arrive relayé (`group_relay`) par un membre courant **et** que
    l'expéditeur était membre à l'époque du message. Un exclu ne peut donc pas
    injecter de messages sous l'ancienne clé.

### Rattrapage p2p (offline-first, allège serveur et émetteurs)
```
B se connecte, envoie à ≤ 3 pairs connectés (Letter 1-à-1) :
  group_sync { group_id, epoch, have: { sender_id: dernier seq contigu } }
chaque pair répond (Letter 1-à-1) :
  group_relay { states: [états manquants, dans l'ordre],
                keys: { epoch: key_envelope re-chiffrée pour le demandeur },
                messages: [ { from, ts, msg } ] }   ← chiffrés groupe, tels quels
lots de 50 messages ; suite sur nouvelle demande ; dédoublonnage par message_id
```
- Exemple : A écrit les messages 3 à 6 puis se déconnecte. B a 4-6, C a 3-6.
  D se connecte avec `have {A: 2}` : B lui renvoie 4-6, C lui renvoie 3-6 (les
  doublons sont ignorés), D est complet.
- **Convergence** : un `group_sync` dont le `have` dépasse ce que nous détenons
  déclenche en retour notre propre `group_sync` vers ce pair. Ainsi B, qui n'a
  pas le 3, le récupère dès qu'il recroise D ou C : les membres convergent
  sans dépendre de l'auteur.
- La signature de l'auteur, à l'intérieur du chiffré, prouve l'origine : un
  pair ne peut pas fabriquer un message d'autrui, seulement le rejouer.
- Fonctionne même si l'auteur d'origine est déconnecté (cohérent avec l'id
  composite (émetteur, séquence) du §5).
- Pas d'ordre total requis : ordre causal par émetteur + `reply_to` explicite.

### Pièces jointes de groupe
L'offre `attachment` est un message de groupe portant l'identifiant de contenu
(`root`, défini au §6 : `SHA-256(0x02 ‖ taille ‖ racine de Merkle des chunks)`), la taille (le nombre de
chunks s'en déduit : chunk fixe de 48 Kio) et le nom/type. Demande et chunks
restent **1-à-1** (§6), chiffrés pour le demandeur seul, mais **n'importe
quel membre détenant le fichier complet** peut servir : l'auteur, ou tout
membre l'ayant déjà téléchargé. Le demandeur peut donc répartir les chunks
sur plusieurs détenteurs connectés (essaim), sans que l'auteur soit en ligne.
- Un détenteur vérifie que le demandeur est membre de l'époque courante.
- **Intégrité par chunk** : avec un seul hash de fichier, un chunk corrompu
  fourni par un pair ne serait détecté qu'à la fin, sans savoir lequel. L'offre
  porte donc `root`, qui engage la racine d'un arbre de Merkle (feuilles = SHA-256 des
  chunks) ; chaque chunk servi est accompagné de sa preuve (environ 10 hashes pour 25 Mio).
  Chaque chunk est vérifié dès réception et un pair fautif est écarté. L'**index** du chunk
  fait partie de la vérification (il fixe, à chaque niveau, de quel côté se trouve le frère) :
  un chunk valide présenté sous un autre index échoue.
- **Le hash sert d'identité du contenu** : un fichier dont la racine est déjà
  présente localement (téléchargé avant, ou déjà reçu dans une autre
  discussion) n'est ni retéléchargé ni rehashé ; un détenteur mémorise la
  racine et l'arbre une fois pour toutes plutôt que de les recalculer à
  chaque demande.

### Implémentation (bibliothèque cliente, `Groups/`) — jalon 1

Codé et testé : création, invitation, ajout/retrait/promotion/départ avec
rotation de clé, envoi/réception, rattrapage p2p, concurrence de modérateurs.
Commandes : `group create|list|members|add|remove|promote|leave|send|history|sync`.

**Jalon 2** (codé et testé) : pièces jointes de groupe en essaim et blacklist de
groupe, voir les deux sections suivantes. Commandes : `group upload|download`,
`group ban|unban|bans`.

Précisions apparues à l'implémentation :
- **Invité = contact du modérateur** : une invitation (état sans groupe connu)
  n'est acceptée que d'un contact confirmé, signée par un modérateur listé, et
  seulement si la clé de l'enveloppe correspond à `key_check`. Les autres
  échanges de groupe (états, `group_sync`, `group_relay`) sont acceptés de tout
  membre d'un groupe commun, y compris s'il n'est pas un contact : ses clés
  viennent de l'état signé.
- **Branches concurrentes conservées** : un état authentique dont le parent est
  connu est gardé avec sa clé même s'il perd le départage ou arrive après une
  époque plus récente — des messages y ont pu être scellés et doivent rester
  lisibles. Les pairs le fournissent (`states` + `keys`) lors du rattrapage.
- **Rejeu des changements perdants** : un modérateur mémorise les changements
  qu'il a émis (`add`/`remove`/`promote`) jusqu'à ce que leur époque soit
  réglée ; s'il perd le départage, il les rejoue par-dessus le gagnant
  (sans effet si déjà appliqués).
- **Départ** : un modérateur émet lui-même l'état sans lui-même (et promeut le
  premier membre restant s'il était seul modérateur) ; un simple membre envoie
  `group_leave` aux modérateurs. Le créateur ne peut pas être exclu.
- **Rétrogradation** (`group demote`) : seul le créateur retire le rôle de modérateur
  à quelqu'un d'autre (qui reste membre et garde la clé : ce n'est pas une
  exclusion) ; un modérateur peut aussi se retirer lui-même. Vérifié par chaque
  récepteur : un état qui retire un modérateur resté membre n'est valide que s'il est
  signé par le créateur (ou par ce modérateur lui-même) ; exclure un membre, lui,
  retire naturellement son rôle. Le créateur ne peut pas être rétrogradé : il faut
  d'abord céder le rôle. Le « vote de retrait d'un modérateur » reste reporté (§12).
- **Oublier un groupe** (`group forget`) : supprime la copie locale (états, clés,
  journal) d'un groupe qu'on a quitté ou dont on a été exclu ; refusé tant qu'on y est.
- **Réponse ciblée** (`group reply`) : `reply_to` (= `message_id` auteur:ts) est un
  champ du message de groupe, **couvert par la signature** ; l'affichage recalcule
  l'index local (comme en 1-à-1, §5).
- **Rôle de créateur transférable** (`group transfer`) : seul le créateur en
  exercice peut céder le rôle, à un membre (promu modérateur s'il ne l'était pas).
  L'ancien créateur reste modérateur mais perd les pouvoirs propres au créateur
  (lever un ban) et la protection contre l'exclusion ; le nouveau les acquiert.
  Un créateur qui **quitte** le groupe cède automatiquement le rôle au premier
  modérateur restant : le groupe a toujours un créateur présent.
  Règles vérifiées par chaque récepteur sur tout nouvel état : le créateur est
  toujours membre de l'état qu'il dirige, et `created_by` ne change que si
  l'état est signé par le créateur précédent au profit d'un modérateur du
  nouvel état (un modérateur ne peut donc ni se couronner ni évincer le créateur).
- **Un membre récent ne lit pas le passé** : il ne reçoit que les clés des
  époques où il était membre ; les messages plus anciens lui restent illisibles
  (et sont renvoyés à chaque synchronisation tant qu'il n'a pas la clé).
- Tests : `sources/freespeech.tests/GroupTests.cs` (27, réseau en mémoire) et
  `scripts\test-groups.ps1` (bout-en-bout, 4 clients, dont le rattrapage
  d'un membre hors ligne alors que l'auteur a disparu).

### Pièces jointes de groupe — implémentation (essaim)

- **Offre** : `attachment` est un message de groupe (Letter `type = "attachment"`,
  payload = l'offre du §6). Son `ref` est `auteur:ts`, **identique chez tous les
  membres** : c'est ce qui permet à n'importe qui de demander « le fichier
  `ref` » à n'importe qui.
- **Qui sert** : tout membre **courant** qui détient le fichier, **en entier ou en
  partie** (l'auteur, puis chacun de ceux qui l'ont téléchargé ou le téléchargent).
  La politique est injectée (`authorize`) : en groupe, « le demandeur est membre de
  l'époque courante ». Un non-membre n'obtient ni fichier ni même un refus (silence).
- **Chunks partiels** : un membre qui télécharge encore le fichier sert les chunks
  qu'il a déjà reçus. Il garde avec chaque chunk la preuve de Merkle reçue
  (`parts/{ref}/{n}.proof`) et la rejoue ; il revérifie le chunk avant de l'envoyer.
  Pour une demande qu'il ne peut pas satisfaire en entier, il répond `attachment_have`.
- **`attachment_have { ref, have, complete, busy }`** (Letter, groupes, entre membres) :
  `have` = bitmap en base64 (bit `i % 8` de l'octet `i / 8` = chunk `i` détenu) ;
  `complete` = tout est détenu (`have` vide) ; `busy` = « je ne sers personne en ce
  moment » (réglage réseau, §6). Seul un *membre* reçoit cette réponse. Le demandeur
  retient la bitmap comme **borne inférieure** : il ne demande à ce membre que ce qu'il
  annonce, et lui redemande le reste après 8 s (il continue de recevoir). Un membre qui
  ne détient rien, ou `busy`, n'est pas redemandé pendant 30 s ; ses chunks sont
  réattribués **aussitôt**.
- **Répartition dynamique** : chaque détenteur reçoit une **fenêtre** de chunks (8 au
  départ, puis environ 3 s de données d'après son débit observé, de 2 à 64 chunks) ;
  quand la moitié d'une fenêtre est consommée, le demandeur en redonne. Un détenteur
  rapide n'est donc jamais laissé inactif derrière un lent. Un détenteur qui ne livre
  rien pendant 15 s perd sa fenêtre au profit des autres (et est laissé de côté 30 s).
  Quand tout ce qui manque est déjà demandé, un détenteur inactif redemande les chunks
  attendus depuis plus de 3 s chez un autre : la première réponse gagne, le doublon est
  écarté. Un détenteur hors ligne voit sa part redonnée aussitôt aux autres. Le
  téléchargement est suivi toutes les 3 s tant qu'il progresse (2 min sans chunk au plus ;
  ensuite le tick de 30 s continue).
- **Détenteur menteur** : les preuves de Merkle (§6) sont vérifiées chunk par
  chunk, peu importe qui sert. Les échecs sont comptés **par détenteur** ; à 3,
  ce détenteur est écarté *pour ce fichier* et le téléchargement continue avec les
  autres. Il n'échoue que si tous les détenteurs sont écartés (en 1-à-1, il n'y
  en a qu'un : comportement inchangé).
- **Participants non-contacts** : les membres d'un groupe n'ont pas à être
  contacts pour échanger `attachment_request`, `attachment_chunk` et
  `attachment_have` ; leurs clés et serveurs viennent de l'état signé.

### Modifier / supprimer dans un groupe : journal de rattrapage et marqueurs de trou

`message_edit`, `message_retract`, `attachment_retract` et `message_react` (§5) sont des messages de groupe ordinaires : ils
entrent dans le journal et le rattrapage p2p les sert comme les autres. Règles propres au groupe :
- **Suppression pour tous** : chaque détenteur **vide la copie scellée** du message dans son journal
  (`LogEntry.Msg = ""`), ainsi que celles des `message_edit` de l'auteur qui la visent (elles portent son texte) — aucune n'est donc
  rendue à un arrivant tardif. L'entrée reste : son `seq`
  garde le journal contigu et une nouvelle livraison n'est pas retraitée. L'auteur joint le `seq` de son
  message à la Letter (`seq`) pour que même un membre qui ne l'a jamais reçu sache quel trou comptabiliser.
- **Marqueur de trou** : une entrée vidée est servie dans `group_relay.messages` comme `{ from, ts, seq, msg: "" }`.
  Le récepteur l'enregistre dans `covered[from]` ; `have` (§7) compte ces `seq` comme reçus, sinon un membre
  arrivé après la suppression resterait bloqué sous le trou et le rattrapage bouclerait. Un marqueur n'a **aucun
  effet de contenu** et n'empêche jamais l'arrivée d'un vrai message : au pire un pair malveillant fait croire
  à un membre qu'il a déjà un `seq`, ce que ce pair pouvait déjà obtenir en ne relayant pas.
- **Modification** : la copie d'origine reste dans les journaux ; un arrivant tardif reçoit l'original puis
  l'édition, appliqués dans l'ordre (l'édition la plus récente l'emporte).
- **Supprimer chez moi** : la copie scellée **reste** dans mon journal (les autres membres peuvent encore en avoir
  besoin de moi). Le contenu disparaît de l'historique, de l'affichage et du backup, pas du journal chiffré.

### Blacklist — deux mécanismes distincts, ne pas confondre
| | Blacklist personnelle | Blacklist de groupe |
|---|---|---|
| Portée | locale à l'utilisateur | groupe |
| Propagée | **jamais** | oui, via `blacklist_update` |
| Qui décide | n'importe qui, pour soi | modérateur(s) uniquement |
| Signée | non nécessaire | oui (Ed25519, vérifiée contre la liste de modérateurs du GroupCard) |
| Résolution de conflit | n/a | union des ajouts des modérateurs ; seul le créateur peut retirer |
| Garantie | sociale (n'affiche/relaie plus) | sociale — **pas cryptographique** sans rotation de clé |

### Blacklist de groupe — implémentation

- **Opération signée** `BanOp { group_id, kind: add|remove, id, ts, signer, sig }`,
  `sig = Ed25519(signer, "ban|group_id|kind|id|ts")`. Letter `blacklist_update`
  `{ group_id, ops[] }`, aussi embarquée en entier dans `group_relay.bans` (une
  liste courte : un membre hors ligne l'apprend de n'importe quel pair).
- **Validité** (vérifiée par chacun, contre l'état courant) : `add` signé par un
  modérateur courant, `remove` signé **par le créateur uniquement**. Les
  modérateurs et le créateur ne peuvent pas être bannis. Une opération venant
  d'un non-membre, mal signée ou non autorisée est ignorée.
- **État effectif** d'un id = sa dernière opération (par `ts`) ; à `ts` égal, `add`
  l'emporte. Une ancienne opération rejouée ne ressuscite donc rien.
- **Effet** : les messages d'un auteur banni ne sont ni affichés, ni stockés, ni
  relayés (le journal local est purgé de ses messages, `group_relay` ne les
  fournit plus). **Pas de rotation de clé** : il reste membre, détient la clé et
  peut écrire — c'est une garantie sociale, pas cryptographique (pour cela :
  exclure, qui fait tourner la clé). Un banni ne peut pas être ré-ajouté tant que
  le créateur ne lève pas l'entrée.
- Limite assumée : un membre qui ignorerait la blacklist continue d'afficher ou de
  relayer ; chacun applique sa propre copie.

### Messages épinglés

- **Opération signée** `PinOp { group_id, kind: pin|unpin, target, ts, signer, sig }`, `target` = identifiant du message (`auteur:ts`),
  `sig = Ed25519(signer, "pin|group_id|kind|target|ts")`. Letter `group_pin` `{ group_id, ops[] }`, aussi embarquée dans `group_relay.pins` : un membre hors
  ligne ou arrivé plus tard l'apprend de n'importe quel pair. Hors de l'état du groupe : aucune rotation de clé.
- **Validité** (vérifiée par chacun, contre l'état courant) : signée par un **modérateur courant** (le créateur compris), pour n'importe quel message du
  groupe, quel qu'en soit l'auteur ; plus récente (`ts`) que l'opération détenue pour ce message. Une opération d'un non-membre, d'un non-modérateur
  ou mal signée est ignorée.
- **État effectif** : la dernière opération de chaque message décide ; **3 messages au plus** sont épinglés, les plus récemment épinglés (au quatrième, le plus
  ancien est libéré, sur chaque appareil). Les épingles d'un modérateur rétrogradé restent ; un autre modérateur peut les libérer.
- **Message absent** : un appareil qui reçoit une épingle sur un message qu'il n'a pas demande à un membre ce qui lui manque (le rattrapage de `group_sync`).
- Un message supprimé pour tous ne s'affiche plus comme épinglé. Garantie au mieux : un client modifié peut ignorer les épingles.

### Nom et image : du groupe (créateur) et de chaque utilisateur

- **Groupe** : `GroupProfileOp { group_id, name, photo?, ts, signer, sig }`,
  `sig = Ed25519(signer, "gprofile|group_id|name|SHA256(photo)|ts")`. Letter
  `group_profile`. Valide si signée par le **créateur courant**, plus récente
  (`ts`) que celle détenue, nom de 1 à 100 caractères, image de 100 Kio au plus
  (JPEG/PNG/WebP en base64). Comme la blacklist, elle vit **hors du GroupState** :
  renommer un groupe ou changer son image **ne fait pas tourner la clé**. Le nom
  affiché est celui du dernier profil valide (il survit aux changements d'époque,
  dont les états portent toujours le nom d'origine). Suit le rôle de créateur : après
  un transfert, l'ancien créateur ne peut plus la modifier.
- **Utilisateur** : `profile name|photo`. Le changement part (1) vers chaque contact
  confirmé par `identity_update` (§5, déjà prévu pour nickname/photo), (2) vers
  chaque groupe par un **`member_profile`** `{ group_id, id, nickname, photo?, ts, sig }`
  **signé par le membre lui-même** (`"mprofile|group_id|id|nickname|SHA256(photo)|ts"`).
  Il est relayable par n'importe quel pair : un arrivant tardif l'apprend dans
  `group_relay.member_profiles`, sans que son auteur soit en ligne. Reçu en direct,
  il doit venir de son auteur. L'état signé du groupe garde le pseudo d'origine ;
  l'affichage utilise le profil du membre quand il existe (sans autorité sur
  les clés ni les droits).
- **Nom et image par groupe.** Un membre peut utiliser dans un groupe un nom (1 à 64
  caractères) et une image qui lui sont propres, différents de ses nom et image globaux ; ils ne circulent que dans le `member_profile` de ce
  groupe, donc ni le nom ni l'image ne permettent de relier les groupes. Ils sont choisis à l'acceptation de
  l'invitation (les globaux sont proposés) et se modifient dans les informations du groupe ; un changement
  part 30 secondes après la dernière modification, donc des modifications successives partent en une fois. Un nom vide, ou le choix
  « ma photo habituelle », redonne le global. Un changement du nom global part toujours vers les groupes sans nom propre
  et laisse les autres inchangés. Tous voient le nom que le membre a donné au groupe, modérateurs
  compris ; le nom qu'on donne soi-même à un contact (`local_name`) prime sur ses écrans. Les clés, les droits et l'état
  signé n'en dépendent pas.
- **Description** (texte libre, 1 à 300 caractères, absente par défaut) :
  - *Groupe* : champ `description` de `GroupProfileOp` (`group about`), donc signé par
    le créateur, sans rotation ; conservé quand on change seulement le nom ou
    l'image, effacé explicitement par `clear`.
  - *Utilisateur* : champ `description` de la `ContactCard` (§3, donc aussi de
    `identity_update`) et de `member_profile`. Visible des contacts confirmés
    **et** des membres d'un groupe commun, comme le nom et l'image.
  - La description est incluse dans les données signées (`…|ts|description`) : la
    modifier dans une opération capturée invalide la signature. Un profil sans
    description reste valide (champ nul).
  - Commandes : `profile about <texte>|clear`, `group about <g> <texte>|clear`,
    `card <contact>`, `group info <g>`, `group members <g>`.
- Les deux portent la même limite d'image (100 Kio) que la ContactCard (§3).
- Client : une seule liste de types de Letters de groupe
  (`GroupService.GroupLetterTypes`) sert au routage — deux défauts de la console venaient
  d'un type traité par le service mais jamais transmis par le client.

### Rotation de clé — quand
- À chaque **exclusion**, chaque **départ** volontaire, et sur demande d'un
  modérateur (compromission d'un appareil). La rotation périodique reste
  reportée (§12).
- Garantie : un exclu ne peut plus déchiffrer les messages **futurs**, même
  interceptés (relais, réseau), puisqu'il n'a jamais reçu la nouvelle clé.
- Limites assumées, à ne pas présenter comme des garanties :
  - un exclu conserve tout ce qu'il a déjà reçu ;
  - pas de confidentialité persistante *dans* une époque : une clé qui fuit
    expose toute son époque.
- Pas de suppression de groupe : il s'éteint quand tout le monde l'abandonne.
## 8. Multi-serveur par contact (routage)

- Cas d'usage : employé en entreprise (serveur privé, non exposé à internet)
  + amis à l'extérieur (serveur public). Un serveur privé non exposé **ne peut
  pas** être atteint par relais S2S depuis le serveur public — la résolution
  doit donc être côté client, pas côté serveur.
- Le client s'enregistre simultanément sur tous les serveurs de sa liste
  qu'il peut effectivement joindre (même mécanique que le multi-device §1) —
  sans limite de nombre : N serveurs privés (plusieurs entreprises, etc.) +
  M serveurs publics, chacun avec sa propre priorité dans `servers[]`.
- Émetteur : tente les serveurs du destinataire dans l'ordre de `servers[]`, puis nos
  propres serveurs (cascade S2S §10). Pour chacun :
  - **Injoignable réseau** (timeout/DNS) → serveur suivant, tout de suite.
  - **Joignable, destinataire présent** → livré, fin.
  - **Joignable, destinataire absent** → serveur suivant (le destinataire peut n'être
    enregistré que sur un sous-ensemble de ses serveurs).
  - **Fin de liste sans livraison** → l'enveloppe reste dans l'outbox locale et un flag
    WAITING est posé sur **chaque serveur joignable** (on ne sait pas où le destinataire
    viendra lire ses flags). Aucun serveur joignable → outbox seule, sans flag.

### Implémentation client (bibliothèque cliente, `Relay/RelayPool.cs`)

- Un `RelayClient` par URL. Serveurs **propres** (`UserIdentity.Servers`,
  annoncés dans la carte) : enregistrement permanent + reconnexion au tick de
  30 s. Serveurs **de contact** : connexion à la demande au premier envoi,
  abandonnés (pas reconnectés) s'ils tombent ; un serveur injoignable est
  ignoré 30 s (timeout de connexion 5 s) pour ne pas payer un timeout par envoi.
- Envoi : URLs candidates = `servers[]` du destinataire par priorité, puis
  nos propres serveurs en dernier recours (la cascade S2S §10 peut encore
  l'atteindre). Même algorithme que ci-dessus.
- Réception : dédoublonnage `(from, to, ts, crypto)` sur les enveloppes
  arrivant par plusieurs serveurs. Flags adressés à soi : lus sur chacun de
  nos serveurs.
- Self-sync (§1/§5) : la copie va sur le serveur qui a livré (celui qui a
  signalé `self_multi_device`).
- Commandes console : `server list | add <url> [prio] | remove <url>` ;
  chaque changement envoie un `identity_update` aux contacts confirmés.

### Serveurs restreints : qui peut s'inscrire, et comment écrire à un contact

Un serveur dont le mode d'accès est `allowlist` (§ 10, « Contrôle d'accès des clients ») est dit restreint : seules les identités que son administrateur a autorisées s'y inscrivent. Il ne faut pas le confondre avec le statut privé entre serveurs, qui interdit les appels d'un autre serveur et qu'un client ne voit pas.

Chaque entrée de `servers` dans la fiche porte `restricted`, un booléen absent quand il est faux, vrai pour un serveur restreint. Le propriétaire connaît le mode de ses serveurs, car le serveur l'annonce à l'inscription et chaque fois que l'administrateur le change. Il envoie sa fiche à ses contacts quand le mode change. Les serveurs ouverts sont listés avant les serveurs restreints, chaque groupe dans l'ordre de préférence du propriétaire, et les `priority` sont renumérotées en conséquence. Un client qui ne connaît pas `restricted` ignore le champ et suit l'ordre de la liste.

Un serveur restreint ne figure pas dans la fiche par son adresse. À son premier besoin, le serveur tire une clé de 32 octets, la **clé du cercle**, et la donne à chaque identité qui s'y inscrit (`circle_key` dans `registered`, puis dans `server_state` quand l'administrateur la renouvelle). La fiche porte, à la place de l'adresse, `circle = base64(HMAC-SHA256(clé du cercle, "freespeech-circle|" + identité du propriétaire de la fiche))`, avec `url` valant `https://circle.invalid`, une adresse qui ne se résout jamais. Un destinataire qui est membre du même serveur calcule la même valeur pour chacun de ses serveurs restreints : une correspondance désigne un serveur commun, qu'il joint à sa propre adresse. Sans correspondance, l'entrée désigne un serveur restreint inconnu et n'est jamais contactée. L'identifiant dépend du propriétaire de la fiche, si bien que deux fiches du même serveur ne se rapprochent pas. Le contact garde l'identifiant tel quel, et la correspondance est calculée à chaque envoi, donc un serveur ajouté plus tard reconnaît d'un coup tous les contacts déjà importés. Après un renouvellement, le client garde l'ancienne clé 30 jours pour reconnaître les fiches déjà envoyées, et le propriétaire renvoie sa fiche à ses contacts avec l'identifiant de la nouvelle clé. Un serveur dont la clé n'est pas encore connue du client est écrit par son adresse. Les serveurs de l'état d'un groupe suivent la même règle.

Un serveur restreint sur lequel je ne suis pas autorisé n'est jamais contacté : ni connexion, ni refus chez l'administrateur, ni marqueur d'attente. Une fiche qui marque à tort un serveur comme restreint ne gêne que son auteur.

Un serveur restreint commun est un serveur de la fiche du contact sur lequel je suis moi-même inscrit avec le mode `allowlist`, ce mode étant mémorisé avec la liste de mes serveurs. Quand il en existe, l'envoi n'utilise que les serveurs communs, dans l'ordre de la fiche. Il n'y a ni serveur ouvert, ni marqueur d'attente sur un serveur ouvert, ni repli sur mes propres serveurs. Sans serveur restreint commun, l'envoi se fait comme au § 8.

Si le serveur restreint ne répond pas, le message reste dans l'outbox avec la mention « en attente d'un serveur privé ». Un serveur restreint qui répond alors que le contact n'y est pas connecté n'est pas en panne : le contact est simplement absent.

Le repli sur un serveur ouvert n'est possible que si les deux côtés l'ont accepté et si aucun serveur restreint commun ne répond. Le réglage est propre à chaque contact et chacun le fait pour ses propres envois. `Never` est la valeur par défaut et le message attend. Avec `Ask`, l'application pose la question dès le premier échec, une seule fois pour la panne, et la réponse « oui » vaut jusqu'au retour d'un serveur restreint commun, sans nouvelle question. Avec `Auto`, le repli commence après `OpenFallbackDelay`, 2 minutes par défaut. Chaque côté fait connaître son choix par la Letter `route_policy { open_fallback }`, vraie pour `Ask` et `Auto`. Elle suit le chemin normal, donc un serveur restreint commun quand il y en a un, passe par l'outbox et est confirmée comme `identity_update`. L'accord est coopératif : il protège contre un passage silencieux sur un serveur ouvert, pas contre un client modifié.

Le repli n'est pas mémorisé. Chaque envoi, et chaque renvoi de l'outbox, commence par les serveurs restreints communs, et le repli prend fin dès que l'un d'eux remet un message.

Pour un membre de groupe qui n'est pas un contact, les serveurs de l'état du groupe sont utilisés : les serveurs restreints non autorisés sont laissés de côté et les communs passent en premier. Le mode strict ne s'applique pas aux groupes.

Le réveil du téléphone (§ 9) passe par le serveur qui détient le marqueur d'attente. Un serveur restreint doit donc être lié à une passerelle push.

## 9. Notifications push (mobile, app non lancée)

**Principe : le push ne transporte jamais de message, il réveille.** Aucune boîte
aux lettres serveur : le message reste dans l'outbox de l'expéditeur (§5) et n'est
livré que quand les deux parties sont éveillées en même temps.

### Séquence de réveil (A écrit à B, B fermé)

1. Le message de A ne peut pas être livré : le serveur de B pose le flag
   `(A, B)` (WAITING, inchangé) et, si B y a un token et est déconnecté, appelle
   la **passerelle push** (ci-dessous).
2. La passerelle envoie à B un push **data à priorité haute** (FCM) ; l'OS réveille
   l'app sans action de l'utilisateur. B ouvre une session de quelques dizaines de
   secondes : connexion aux serveurs, déclaration READY.
3. Le serveur relaie le READY à A (événement s'il est connecté ; sinon push de
   réveil à A, symétrique, si A a un token). A renvoie aussitôt son outbox au lieu
   d'attendre son tick de 30 s.
4. B reçoit, déchiffre et affiche une **notification locale** (expéditeur et aperçu
   restent sur l'appareil ; Apple/Google ne voient que « réveille-toi »).

Limite assumée : si A est éteint ou arrêté de force juste après l'envoi, le message
attend son retour. B doit rester connecté plus longtemps que le tick de retry de A
(≥ 45 s en pratique) ; A garde son retry actif quelques minutes après un envoi.

### Enregistrement du token

- Token push (FCM/APNs) enregistré par le client auprès de **chacun des serveurs
  sur lesquels il est enregistré** (§8) : un flag peut être posé sur n'importe
  lequel. Jamais dans le ContactCard (qui porte seulement `enable_push`).
- Messages client → serveur (authentifiés comme le reste de la session) :
  - `push_register { platform: "fcm"|"apns", token }` — le client ne nomme aucune
    passerelle : le relais choisit parmi celles qu'il a liées ; remplace tout
    enregistrement précédent de cette identité.
  - `push_unregister {}` — retire le token (désactivation des notifications,
    déconnexion, suppression du compte).
  - `push_refresh { token }` — nouveau token fourni par l'OS ; même effet que
    `push_register`.
- Réponse : `push_result { ok, message? }`. Le token est stocké par identité sur le
  serveur, **en mémoire uniquement** (comme les flags) : le client le ré-enregistre
  à chaque connexion. Jamais relayé à un autre serveur ni à un contact.
- Le serveur n'appelle que les passerelles **liées** (acceptées par les deux administrateurs,
  voir « Liaison entre serveurs ») :
  aucune adresse ne vient du client. Sans passerelle liée, le push est désactivé
  (`push_result.ok = false`) et seul le polling subsiste.
- **Plusieurs passerelles** : plusieurs peuvent être liées (identiques : mêmes
  identifiants, aucun état à garder). Pour un wake-up, le serveur essaie les passerelles
  liées dans un ordre aléatoire, **3 au plus**.
  On passe à la suivante sur 5xx (dont 503 : pas d'identifiants pour cette plateforme
  ici), délai dépassé, ou `429` avec `X-Push-Reason: busy`. Un `429` avec
  `X-Push-Reason: token` (ce token vient d'être réveillé) **n'est pas** une saturation :
  on s'arrête, un autre ne ferait que réveiller deux fois. `410` : token supprimé.
  Autre 4xx : requête invalide, on n'insiste pas.
- **Disjoncteur** : une passerelle qui échoue 3 fois de suite est écartée 45 s ; une qui
  répond « busy » avec `Retry-After: N` l'est tout de suite pendant N s (120 s au plus).
  Une panne coûte donc un délai, pas un par wake-up. Le wake-up reste au mieux : le
  polling est le repli.- `peer_online { discovery_id }` (serveur → client) : un contact vers qui l'on a un
  flag WAITING vient de se connecter ; le client vide son outbox immédiatement **et relance
  sans attendre les téléchargements de pièces jointes** qui attendaient ce contact (§6) :
  un expéditeur éteint, réveillé par push, n'a que quelques secondes pour servir une photo.
  Déclenchement par un wake-up de A si A est hors ligne et a un token.
- Limite de débit : un wake-up par identité toutes les 30 s au plus. Une tentative que **aucune passerelle** n'a prise (panne, saturation) ne
  compte pas : la suivante peut partir aussitôt. La passerelle applique la même règle à son délai par jeton.
- **Présence** : une identité est « en ligne » tant qu'une de ses connexions WebSocket répond. Le relais envoie un ping toutes les 30 s et ferme
  la connexion sans réponse en 45 s (téléphone éteint ou réseau perdu) ; l'identité est alors hors ligne. Un flag WAITING posé **pendant** une
  connexion qui se termine ainsi (flag plus récent que l'ouverture de la connexion) déclenche le wake-up que la présence avait écarté.
- Message FCM : priorité haute, `collapse_key` `wake` (les réveils en attente d'un téléphone injoignable n'en font qu'un) et durée de vie de 7 jours.

### Passerelle push (`freespeech.push`, projet séparé)

- Seule détentrice des identifiants FCM/APNs (jamais dans le dépôt public : fichiers
  de configuration locaux, `.example` versionné). Les serveurs relais n'en ont pas.
- `POST /push { platform, token, kind: "wake" }` ; refuse les appelants non connus,
  limite le débit par token (`X-Push-Reason: token`) et la charge totale (`busy`).
- **Une paire de clés par serveur relais** (appels signés, voir « Liaison entre serveurs ») :
  l'exploitant de la passerelle accepte un relais (`admin accept <id>`), et peut
  le **bannir** (`403`), l'**oublier** (`401`) ou lire ses compteurs (envoyés,
  échecs, occupé, limités, jetons morts) sans toucher aux autres relais. Sans
  aucun relais lié, la passerelle refuse tout (sauf en développement).
- **Réglages à chaud** (`settings.json`) : délai minimal par jeton, nombre de
  réveils simultanés, et **maintenance** (répond `429 busy` à tous : les relais
  essaient une autre passerelle, un redémarrage ne perd rien). API locale
  `/admin/...` et commande `admin ...`, mêmes règles que le serveur relais
  (voir « Exploitation » au §10).
- Charge utile **générique et constante** : aucun expéditeur, aperçu, identité.
- Réponse `410` / token invalide → le serveur relais supprime le token ; le client
  le ré-enregistre à sa prochaine connexion.
- Un serveur ou client forké peut utiliser la passerelle officielle ou la sienne ;
  sans fichiers de configuration push, le code compile et seul le polling subsiste.

### Notifications sur le téléphone — fonctionnement et réglages (Android)

**Deux réglages indépendants** (Réglages de l'appli, tous deux activés par défaut) :

| Réglage | Ce qu'il décide | Désactivé |
|---|---|---|
| **Notifications du système** | afficher une alerte dans la barre du téléphone à l'arrivée d'un message quand l'appli n'est pas ouverte ; l'appli demande l'autorisation Android (Android 13 et plus) | les messages arrivent quand même (appli ouverte ou réveillée), sans alerte ; si le **téléphone** bloque les notifications de l'appli, les Réglages le disent et mènent à ceux du système |
| **Réveil par le réseau (push)** | recevoir appli fermée : le jeton push est donné aux serveurs, et une vérification périodique est planifiée | le jeton est retiré des serveurs (`push_unregister`), la vérification périodique est annulée : les messages n'arrivent qu'à l'ouverture de l'appli |

**Trois situations.**
1. **Appli ouverte** : pas de notification du système ; la liste des discussions montre les compteurs et les pastilles.
2. **Appli en arrière-plan, processus vivant et connecté** : l'appli reçoit en direct et crée elle-même une notification locale
   (canal « messages », importance haute) : titre = expéditeur (et groupe), texte = aperçu. Une seule notification par discussion,
   remplacée par la suivante ; elle est **retirée dès que le message est lu** dans l'appli. Une discussion en sourdine reste silencieuse.
3. **Appli fermée ou processus tué** : voir ci-dessous.

**Appli fermée — réveil par push.** Le serveur de B appelle la passerelle (séquence ci-dessus) ; le message de données FCM démarre le
processus de B, qui lance une **vérification** (`BackgroundPoll`) : charge l'identité, ouvre un client **séparé** de l'interface,
se connecte à ses serveurs et écoute **25 s** à partir de la connexion effective à un serveur (le réseau d'un téléphone qui vient de démarrer n'est pas toujours prêt : la connexion est retentée toutes les 3 s, 60 s au plus) (45 s pour la vérification périodique) ; pendant ce temps, l'expéditeur voit B en ligne
(`peer_online`, ci-dessus) et renvoie ; B déchiffre **localement** et crée sa notification (expéditeur et aperçu ne quittent jamais
l'appareil). Un **verrou** n'autorise qu'une vérification à la fois (la base de données du profil ne s'ouvre pas deux fois) : si la
vérification périodique tient déjà le verrou à l'arrivée du push, le gestionnaire du push **attend sa fin** (60 s au plus) au lieu de
rendre la main, sinon le système fige le processus juste après la connexion. La vérification périodique (`WorkManager`, ~15 min,
réseau requis) sert de **repli** quand le push est refusé, absent ou bloqué.

**Garder le processus en vie (service au premier plan).** Une session de quelques secondes ne suffit pas pour un fichier volumineux
ni pour un message qui attend son destinataire. L'appli démarre alors un **service au premier plan** (type `dataSync`, notification
discrète, canal « transferts ») :
- **expéditeur réveillé qui sert un fichier** : « Envoi d'un fichier en cours », jusqu'à la fin du transfert, **10 min** au plus ;
- **appli qui passe en arrière-plan avec des messages en attente** (outbox non vide) **ou un fichier en cours d'envoi** : « Envoi
  en attente… », jusqu'à livraison, retour dans l'appli, ou **3 min** ; fermer l'appli par balayage n'interrompt plus l'envoi.
Aucune notification de service quand il n'y a rien à envoyer : un simple réveil reste silencieux. Permissions : `FOREGROUND_SERVICE`,
`FOREGROUND_SERVICE_DATA_SYNC`.

**Limites connues.**
- Le serveur n'envoie **pas plus d'un réveil par identité toutes les 30 s** ; un expéditeur qui ferme son appli sans service au premier
  plan laisse son message dans l'outbox jusqu'à sa prochaine ouverture.
- **Renvois de l'outbox** : une lettre que personne n'a prise est retentée à chaque tick (30 s) pendant 10 minutes, puis toutes les 15 minutes
  jusqu'à 24 heures, puis toutes les 6 heures, et **plus du tout après 3 jours** (un contact qui n'existe plus ne tient pas l'appli occupée).
  Elle reste dans l'outbox : elle part, quel que soit son âge, dès que le relais signale le contact en ligne (`peer_online`), tant que le flag
  WAITING posé par le dernier essai vit sur le relais (30 jours, en mémoire). À chaque connexion, le client renvoie aussitôt son outbox, sans attendre le premier
  passage de 30 s : un expéditeur réveillé par push alors que son contact est déjà en ligne ne reçoit pas `peer_online`, et le contact réveillé n'écoute que quelques secondes. Le service au premier plan de l'envoi (Android) ne se déclenche que pour les lettres de moins de 3 jours.
  Une lettre remise (le relais l'a écrite sur la connexion du contact) mais **non confirmée** pose elle aussi le flag WAITING après quelques secondes (3 s) : une connexion morte
  (téléphone en mode avion) accepte l'écriture sans que la lettre arrive. Quand le contact revient sur une nouvelle connexion, peer_online (ou le réveil push de l'expéditeur) fait renvoyer aussitôt
  toutes les lettres non confirmées de ce contact (le destinataire dédoublonne et confirme à nouveau). Le flag est retiré (DELETE /api/flags/{from}/{to}) à la confirmation de la dernière lettre en attente ;
  le mobile le pose sans délai quand l'appli passe en arrière-plan.
  L'utilisateur peut aussi **arrêter l'attente** d'une lettre non remise (mobile : menu « Envois en cours », affiché par un réglage) : la lettre
  reste dans l'outbox et part comme les autres à `peer_online`, mais elle n'est plus retentée à chaque tick et ne déclenche plus le service
  au premier plan ; « Reprendre » rétablit l'attente.
- Un fabricant peut tuer l'appli (par exemple le nettoyage « OneKeyClean » de MIUI, le mode économie d'énergie) : régler la batterie sur
  « Sans restriction » pour l'appli. Une appli **arrêtée de force** par l'utilisateur ne reçoit plus aucun push tant qu'elle n'est pas rouverte
  (sous MIUI, la croix « tout fermer » des applications récentes est un arrêt forcé).
- Sur Android, si la batterie est optimisée pour l'appli, elle propose l'exemption d'optimisation (boîte système Oui/Non) dans une boîte, au premier
  affichage de l'accueil. La boîte n'est montrée qu'une fois, et se rouvre depuis Réglages → « Vérifier les réglages du téléphone ».
- Le push exige les services Google Play et un build avec la configuration Firebase ; sans eux, seule la vérification périodique tourne.
- iOS : pas de service au premier plan ni de réveil silencieux fiable.

**Confidentialité et précautions.**
- Google (FCM) voit : le jeton de l'appareil, le paquet de l'appli, l'heure de chaque réveil — **jamais** un contenu, un nom ou un identifiant
  FreeSpeech (charge utile constante). La passerelle ne voit que le jeton ; le serveur relais ne garde le jeton qu'**en mémoire**.
- Le texte d'une notification (expéditeur, aperçu) s'affiche selon le réglage d'écran de verrouillage du téléphone ; **l'appli ne force
  pas le masquage de l'aperçu** (piste : notification de visibilité « privée »). Conseil : régler Android sur « masquer le contenu
  sensible ».
- La notification d'un service au premier plan ne révèle ni contact ni contenu.
- Les traces de diagnostic du réveil (`adb logcat`, balise `FreeSpeech`) ne contiennent que des étapes (« réveil reçu », « connecté »),
  jamais de contenu ; elles ne sont lisibles qu'avec un câble de débogage.
- Compromis du §9 inchangé : le serveur connaît qui écrit à qui (le flag), jamais le contenu.

### Fallback et iOS

- Fallback obligatoire : polling OS natif (~15 min) si push refusé/absent/bloqué
  par le constructeur.
- iOS : les pushs silencieux (`content-available`) sont limités et jamais livrés
  après un geste de fermeture ; la voie fiable est un push visible générique avec une
  extension de service de notification (≈ 30 s) qui exécute la session de réveil.
  Reporté (§12).
- Compromis assumé : le serveur connaît le graphe social (qui
  parle à qui) par construction du flag, jamais le contenu.
## 10. Côté serveur

- Registre : utilisateurs déclarés / connectés (multi-connexion, cf. §1/§8).
- **Aucun auto-apprentissage de siblings depuis le trafic client.** Le champ
  `to_server` d'un envelope (§8) sert uniquement à router *cet envelope
  précis* — il ne doit jamais alimenter la liste de cascade discovery
  (`KnownServers`/siblings) d'un serveur, privé **ou public**. Cette liste
  n'est modifiée que manuellement par l'admin (cf. la règle ci-dessous).
- Serveur **privé** : n'accepte de requêtes S2S (discovery cascade, forward
  d'envelope, propagation de flag) que d'une liste blanche de siblings
  configurée manuellement — jamais d'un serveur inconnu qui le solliciterait
  spontanément. Un refus ne doit rien laisser transparaître (même réponse
  générique qu'un serveur inexistant) pour ne pas confirmer son existence par
  élimination. But : un inconnu ne doit jamais pouvoir apprendre l'adresse
  d'un serveur privé, ni même son existence, via la cascade discovery.
- Serveur **public** : peut interroger un autre serveur public pour
  synchroniser annuaire clients + annuaire siblings (S2S `relay` /
  `sync_whitelist`). Reste soumis à la règle "pas d'auto-apprentissage"
  ci-dessus.
- Interface web d'admin (ajout/suppression sibling, statut privé/public) —
  hors protocole : une interface n'utilise que les API d'administration
  décrites ci-dessous, rien de plus.
- Commande de désinscription volontaire (suppression de l'annuaire) : non faite.
  Il n'y a pas d'annuaire côté serveur ; seuls les jetons push se retirent
  (`push_unregister`).

### Contrôle d'accès des clients (serveur privé)

Distinct du statut public/privé (qui ne concerne que le trafic **entre serveurs**) :
le **mode d'accès** dit qui peut s'inscrire sur ce serveur.

- `open` (défaut) : tout le monde, sauf les identités bannies.
- `allowlist` : seules les identités **autorisées** par l'administrateur, ou qui
  présentent une **invitation** valable. C'est un serveur privé pour une famille,
  une association, une entreprise.
- **Un ban l'emporte toujours**, y compris en mode `open`, et coupe aussitôt les
  connexions en cours de l'identité. Retirer une identité autorisée les coupe aussi.
- **Invitation** : jeton `fs-inv-<20 hex>`, **à usage unique**, valable 1 à 90 jours
  (7 par défaut), créé par l'admin et affiché une seule fois (le serveur ne garde
  que son empreinte SHA-256, `id` = 8 premiers hex, pour la lister ou la révoquer).
  Le client la joint au `register` (champ `invitation`). Elle n'est consommée
  **qu'après** la preuve de possession de la clé (§11) : l'identité devient alors
  autorisée et ses connexions suivantes n'ont plus besoin du jeton.
- **Refus** : `error` dont le message est le code stable `private_server`
  (inconnu : demander une invitation) ou `access_refused` (banni). Rien d'autre
  n'est dit. Le client affiche le texte adapté (réglages → serveurs) et continue
  de réessayer sans bruit.
- **Demande d'accès** (serveur `allowlist`) : une identité inconnue peut demander à être
  autorisée. Le client ajoute au `register` `request_access: true`, `name` (64 caractères
  au plus) et `note` (200 au plus) — **uniquement après le consentement de l'utilisateur**,
  car son nom quitte l'appareil. Après la preuve de possession des clés, le serveur met la
  demande en file et répond `error` / `access_pending` ; ensuite, **chaque** connexion de
  cette identité reçoit `access_pending` (sans renvoyer le nom) tant que
  l'administrateur n'a pas décidé. Une identité en attente qui redemande **met à jour** sa
  demande (nom, mot, date) : jamais deux lignes. File bornée (`Server:MaxAccessRequests`,
  500), un seul exemplaire par identifiant, expiration (`Server:AccessRequestDays`, 14),
  texte nettoyé (caractères de contrôle retirés). `accept` autorise l'identité (le nom
  devient la note) ; `reject` la retire, avec ou sans ban. Une identité bannie ne peut pas
  se mettre en file (`access_refused`). Le client sait qu'il est accepté parce que sa
  reconnexion suivante réussit (réessai au démarrage et à chaque tick). L'administrateur
  peut fermer la file (`requests close`) : les nouveaux inconnus reçoivent `private_server`.
- Persisté dans `access.json` (`Server:AccessFilePath`), écriture atomique,
  appliqué **sans redémarrage**. Un fichier illisible met le serveur en `allowlist`
  (jamais ouvert par accident). `Server:ClientAccess` fixe le mode de départ d'un
  serveur qui n'a pas encore de fichier.
- Routes (§« Exploitation ») : `GET /api/admin/access`, `POST /api/admin/access/mode`,
  `POST/DELETE /api/admin/clients`, `POST/DELETE /api/admin/clients/ban`,
  `POST/DELETE /api/admin/invitations`, `GET /api/admin/access/requests?name=&older_than_days=&limit=&offset=`,
  `POST /api/admin/access/requests/accept|reject` (`ids` — préfixes de 6 hex et plus —, ou `all` avec
  `name` / `older_than_days` ; `ban`, `reason`) et `POST /api/admin/access/requests/open`, et `GET /api/admin/siblings` (relais liés : `url`).

### Outil d'administration d'un serveur privé (groupes créés pour les membres)

Un serveur privé (`allowlist`, qui n'accepte aucun autre relais) peut **nommer** des identités FreeSpeech comme ses **outils d'administration**
(`provisioners`) : un outil, branché sur l'API d'administration locale, y crée des groupes pour les personnes autorisées sur ce serveur.

- **Une identité ordinaire** : un client FreeSpeech (nom, image et description comme une fiche de contact), propre à ce serveur. Le serveur la laisse
  s'inscrire sans invitation ni autorisation ; elle ne voit que les groupes qu'elle a créés.
- **Clés publiques des membres** : quand une identité autorisée s'inscrit, le serveur retient ses clés publiques (`enc_key`, `sign_key`, celles de la
  fiche de contact), après avoir vérifié que l'identifiant en est l'empreinte. Elles ne sont jamais retenues pour une identité non autorisée, et sont
  effacées avec l'autorisation. `GET /api/admin/clients/keys` → `[ { id, note, enc_key, sign_key } ]` pour les identités autorisées dont il les connaît
  (une personne autorisée qui ne s'est pas encore connectée ne peut pas être mise dans un groupe).
- **Nommer l'outil** : `GET /api/admin/provisioners`, `POST /api/admin/provisioners` `{ id, enc_key?, sign_key? }` (les clés ne sont prises que si elles
  correspondent à l'identifiant), `DELETE /api/admin/provisioners?id=`. Un ban retire le nom.
- **Ce que voient les clients** : `registered` et l'événement `server_state` (voir « Bascule d'un client ») portent `provisioners`
  `[ { id, enc_key, sign_key } ]` ; le client ne retient que les entrées dont les clés correspondent à l'identifiant, et seulement pour les serveurs auxquels
  il est connecté.
- **Règle de confiance** : une lettre venant d'une identité nommée par un de **ses** serveurs, qui n'est pas dans ses contacts, n'est acceptée que si elle
  est de type `group_state` (une invitation), avec la signature vérifiée par les clés annoncées. L'invitation attend toujours l'accord de l'utilisateur
  (un seul geste) et est présentée comme venant de l'administrateur ; le groupe n'est rejoint qu'après. Tout autre type venant de cette identité est ignoré.
- **Adresse publique** : l'outil a besoin de l'adresse que les téléphones utilisent pour joindre le relais (celle des réglages de l'appli) : le relais
  l'indique dans `GET /api/admin/status` (`public_url`, réglage `Peer:PublicUrl`, aussi utilisé pour le lien entre relais).
### Liaison entre serveurs par consentement mutuel (relais ↔ passerelle push, relais ↔ relais)

Aucun secret n'est partagé : un lien n'existe que si **les deux administrateurs** ont accepté, quel que soit celui qui a demandé.

- **Identité** : chaque serveur crée au premier démarrage une paire de clés ECDSA P-256, gardée dans `peer-identity.json` (droits 600).
  Seule la clé publique circule ; son **empreinte** (SHA-256 de la clé, 64 hexadécimaux) est l'identifiant du serveur.
- **Points d'accès publics** (joignables par les autres serveurs, au même titre que `/api/time`) :
  - `GET /peer/identity` → `{ software: "FreeSpeech", kind: "relay"|"push", name, public_key, fingerprint }`.
  - `POST /peer/request` `{ kind, name, url, public_key, ts, sig }` : demande de lien. `sig` signe `peer-request\n<kind>\n<name>\n<url>\n<ts>`
    avec la clé présentée (on ne peut donc pas demander au nom d'une autre clé), `ts` à moins de 5 minutes. Réponses : `pending` (202),
    `accepted` (200), `banned` / `closed` (403), `full` (429), ou 400 (`bad_signature`, `bad_time`, `bad_url`, `bad_key`, `kind_not_accepted`).
    Un relais n'accepte que des demandes de passerelles (`push`), une passerelle que des demandes de relais.
  - `POST /peer/status` (appel signé) → `{ state: "pending" | "accepted" | "banned" | "unknown" }` : où en est ma demande.
- **Appel signé** : en-têtes `X-Peer-Id` (empreinte), `X-Peer-Ts` (secondes Unix, à ±2 minutes) et `X-Peer-Sig` (signature de
  `MÉTHODE\nCHEMIN\nTS\nSHA-256(corps)`). Le récepteur vérifie que l'appelant est un pair **accepté**, le temps, la signature avec la clé
  épinglée, et qu'il ne l'a pas déjà vue (anti-rejeu). Sur la passerelle, `POST /push` n'accepte que l'appel signé d'un relais
  lié. Un pair banni reçoit 403, un inconnu ou une signature fausse 401.
- **États d'un pair** : `pending_out` (j'ai demandé), `pending_in` (il m'a demandé), `accepted`, `refused` (il m'a répondu non) et `banned`.
  Si chacun demande à l'autre, le lien est conclu aussitôt. Le demandeur apprend la réponse en interrogeant `/peer/status` (toutes les 20 s
  tant qu'une demande attend ; une demande qui n'est pas parvenue est renvoyée). Le même appel vérifie les liens conclus : chaque serveur interroge ses voisins toutes les 60 secondes par défaut (`Link:CheckSeconds`, `peer-check`), sauf ceux avec qui un échange a réussi depuis moins que ce délai. Trois échecs de suite préviennent l'administrateur (type `peers`), de même le retour du serveur. Un appel signé par un serveur connu n'est jamais limité en nombre ; la limite de 30 appels par adresse et par 10 minutes ne vise que les inconnus.
- **Qui demande** : une demande porte le nom, l'adresse et un **contact** facultatif (`Peer:Contact` : adresse de courriel, numéro de téléphone, page), le tout signé par la clé qui demande, et le destinataire y ajoute l'adresse d'où la demande est venue. Rien de cela ne prouve quoi que ce soit : le nom, l'adresse et le contact sont ce que l'expéditeur déclare ; l'administrateur le joint par ce contact (ou par tout autre moyen de confiance), se fait lire l'empreinte avec `peer-id` et la compare. Les mêmes lignes figurent dans le courriel envoyé pour une demande (type de notification `peers`).
- **Acceptation** : l'administrateur voit nom, adresse et empreinte (qu'il compare avec celle que l'autre lui lit : `peer-id`) ; à
  l'acceptation, le serveur interroge l'adresse annoncée et refuse si elle ne présente pas la clé de la demande (jamais d'appel vers une adresse
  avant que l'administrateur ait choisi d'accepter). Une adresse injoignable est acceptée « non vérifiée ».
- **Garde-fous** : demandes bornées (`Peer:MaxRequests`, 200), une entrée par clé (redemander la met à jour), expiration (`Peer:RequestDays`, 14),
  limitation par adresse, texte nettoyé, file fermable. `https` exigé pour `add` (http seulement pour la machine ou un réseau privé, ou
  `--insecure`).
- **Persistance** : `peers.json` (écriture atomique) à côté de l'état du serveur ; un redémarrage ne perd aucun lien. Un fichier illisible est
  copié en `.corrupt` et aucun pair n'est reconnu tant qu'il n'est pas réparé.
- **Routes d'administration locales** : `GET /api/admin/peers[?kind=]`, `GET /api/admin/peers/requests`, `POST /api/admin/peers` (ajouter),
  `POST /api/admin/peers/accept|reject`, `DELETE /api/admin/peers?id=`, `POST|DELETE /api/admin/peers/ban`, `POST /api/admin/peers/open`,
  `POST /api/admin/peers/refresh`, `GET /api/admin/peers/identity` (préfixe `/admin` sur la passerelle).
- **Relais voisins** : `add relay <url>` / `accept relay <id>` entre deux relais **publics** (un relais
  privé refuse : il ne prend aucune part au trafic entre serveurs). Un relais lié devient un **voisin** ; ses appels `/s2s/...` portent
  `X-Peer-Id` / `X-Peer-Ts` / `X-Peer-Sig`, et
  `S2SAuthMiddleware` les vérifie (404 sans rien révéler en cas d'échec, quota par voisin, adresse bannie refusée). Les deux relais doivent
  avoir réglé `Peer:PublicUrl` (chacun appelle l'autre) ; un relais qui n'a pas donné d'adresse ne peut pas être accepté. L'interdiction d'une adresse (`server-ban`) l'emporte sur un lien.
- **Un lien oublié ou banni de l'autre côté** est détecté : toutes les 5 minutes (ou à `peer-refresh`), chaque serveur demande aux pairs liés s'ils le
  connaissent encore ; un pair qui répond « inconnu » ou « banni » passe à `refused` (aucun appel ne pourrait aboutir). Une panne réseau ne change rien.

### Annuaire (`freespeech.directory`, projet à part)

Un annuaire indique à un client qui ne connaît aucun relais quels relais acceptent de nouveaux comptes, et répartit les nouveaux arrivants entre eux. Il ne participe pas à l'échange de messages et ne stocke rien sur les clients.

- **Lien** : un relais est lié à un annuaire comme à n'importe quel serveur — par clés, avec l'accord **des deux** administrateurs (`add directory <url>` sur le relais, `accept <id>` sur l'annuaire) ; aucun secret n'est partagé et rien n'est automatique : un relais n'est proposé aux nouveaux arrivants que si l'administrateur d'un annuaire l'a décidé, et un relais ne parle qu'aux annuaires que son administrateur a choisis. Plusieurs annuaires sont indépendants : chaque relais se lie à chacun d'eux.
- **Battement** : toutes les `Directory:HeartbeatSeconds` (120) secondes le relais envoie `POST /directory/heartbeat`, appel signé (voir « Liaison entre serveurs »), corps `{ "country": "FR", "free": 90, "accepts_new": true, "version": "1.2.0", "date": "2026-10-17" }`. `country` est un code ISO 3166 alpha-2 déclaré par l'administrateur du relais (`settings set country`) ; `free` est le nombre de places restantes avant que le relais propose un autre serveur à ses clients (`max-connections` moins les comptes connectés ; -1 quand il n'indique aucune limite) ; `accepts_new` est vrai tant que le relais est public, que l'accès des clients est `open` et qu'il lui reste de la place ; `version` et `date` sont celles du programme du relais, pour l'administrateur de l'annuaire. Le nombre d'utilisateurs n'est jamais envoyé, et l'adresse proposée est celle que l'annuaire a vérifiée quand son administrateur a accepté le relais. Un relais privé n'envoie rien.
- **Vérification de l'adresse** : au premier battement d'un relais puis toutes les `Directory:ProbeMinutes` (30) minutes, l'annuaire demande `GET /peer/identity` à l'adresse du relais ; le relais n'est proposé que tant que cette adresse répond avec la clé acceptée.
- **Relais proposés** : un relais est proposé quand il est lié, a parlé depuis moins de `Directory:ExpiryMinutes` (10) minutes, a été vu à son adresse, accepte de nouveaux comptes et a de la place.
- **Liste** : `GET /directory/servers?country=FR&count=3` (count de 1 à 10, `Directory:ListSize` (3) par défaut) → `{ "ts": 1790000000, "servers": [ { "url": "https://r1.exemple.org", "country": "FR" } ], "sig": "…" }`. Les relais du pays viennent d'abord, les autres complètent ; dans chaque groupe le tirage est aléatoire, pondéré par les places restantes (poids fixe quand le relais n'indique aucune limite), pour que les nouveaux arrivants ne tombent pas tous sur le même relais. `sig` signe `directory-list`, un saut de ligne, `<ts>`, un saut de ligne, puis une ligne `<url>|<country>` par serveur, dans l'ordre, avec la clé ECDSA P-256 de l'annuaire. Plus de 30 requêtes par minute depuis une adresse : 429.
- **Client** : l'application a les annuaires intégrés, chacun avec sa clé publique (`FsDirectories` : `url|clé`, séparés par des points-virgules). Une nouvelle identité les interroge dans un ordre aléatoire, prend la première réponse qui se vérifie (signature bonne, d'au plus un jour, adresses `https` — `http` seulement pour la machine locale) et fait des premiers relais ses serveurs. Une liste qui ne se vérifie pas, ou qui est vide, est ignorée et l'annuaire suivant est interrogé. Sans réponse, l'identité prend l'un des relais de repli intégrés (`FsFallbackServers`, séparés par des points-virgules), au hasard.
- **Confidentialité** : l'annuaire ne conserve rien sur les clients qui l'interrogent et ne journalise aucune adresse. Un client ne lui envoie, au plus, que son pays.
- **Administration** : `servers` (relais connus, avec pays, places restantes, version, s'ils sont proposés), `settings` et les commandes de tous les serveurs : [cli-directory.fr.md](cli-directory.fr.md). Installation : [INSTALL_DIRECTORY.fr.md](INSTALL_DIRECTORY.fr.md).

### Exploitation : administration locale, journaux, suivi

- **Pourquoi à chaud.** Le serveur garde en mémoire la présence, les marqueurs
  d'attente et les jetons push : un redémarrage coupe toutes les connexions et les
  efface. Tout ce qui s'administre (accès, voisins, bans, passerelles, limites,
  niveaux de journal) s'applique donc **sans redémarrer**. Seuls les réglages de
  démarrage (ports, chemins, secret d'administration) l'exigent ; on le précède
  d'un `admin drain` (relais) ou `admin maintenance on` (passerelle).
- **Qui peut administrer.** Il faut le secret (`Admin:Secret`, vide = tout refusé)
  **et** un appel venant de la machine elle-même (loopback ; l'adresse transmise par
  nginx est celle qui compte, donc rien n'est joignable de l'extérieur même si le
  proxy laisse passer ; `Admin:AllowRemote=true` seulement pour un réseau privé).
  Les secrets sont en écriture seule.
- **Commande `admin`** (relais : `dotnet FreeSpeech.Relay.dll admin ...` ;
  passerelle : `dotnet FreeSpeech.Push.dll admin ...`) : parle à l'API locale du
  serveur en marche, sans interface web. Adresse : `--url` / `FS_ADMIN_URL`
  (défaut `ASPNETCORE_URLS`) ; secret : `FS_ADMIN_SECRET` / `Admin:Secret`. Codes de
  sortie : 0 ok, 1 usage, 2 pas de secret, 3 serveur injoignable, 4 refusé ou échec.
- **Niveaux de journal à chaud** : par catégorie (`relay`, `s2s`, `push`, `access`,
  `flags`, `admin`, `http`, ou `all`), `admin log level relay debug`, retour à la
  configuration avec `admin log reset`. `admin log tail [-n N] [--level L] [-f]`
  lit un tampon **en mémoire** (2000 lignes, jamais écrit sur disque par ce code).
- **Suivi d'une identité** (comme `sip set debug peer`) : `admin trace <8+ hex>
  [--minutes N]` écrit dans le tampon ce qui arrive à cette identité (connexion,
  refus, envois, drapeaux, réveil push). **60 minutes au plus**, s'arrête seul, laisse
  une ligne dans le journal.
- **Règles de confidentialité des journaux** (non négociables) : métadonnées
  seulement ; jamais de contenu, de clé, de jeton d'appareil, ni la charge chiffrée
  (le niveau debug donne seulement le mode de chiffrement et la taille) ; les
  identifiants sont raccourcis à 8 caractères ; « qui écrit à qui » n'apparaît pas au
  niveau Information, seulement en debug (raccourci) ou dans un suivi demandé ; la
  production garde `Logging__LogLevel__FreeSpeech=Warning`.
- **Passerelle push** : `GET /admin/status`, `GET /admin/relays`, `/admin/peers…`,
  `GET/POST /admin/settings` ; `log` et `trace`
  comme ci-dessus (`/admin/log...`).
- **Offre du code source (AGPL art. 13)** : `GET /api/info` (public, sans secret) renvoie
  `{ software, license: "AGPL-3.0-or-later", version, source }` ; `source` vient de `Server:SourceUrl`.
  Ce point ne dit rien de l'état du serveur.

### Implémentation (session S2S)

- **Statut public/privé, candidats et adresses bannies** persistés dans
  `siblings.json` (chemin configurable, `Server:SiblingsFilePath`) —
  [Services/SiblingRegistryService.cs](freespeech.relay/Services/SiblingRegistryService.cs).
  Un sibling = un relais lié par clés (`{ url }`, voir « Liaison entre serveurs »).
- **Candidats — jamais auto-promus** (décision explicite prise pendant la
  conception, contre-proposition à "apprentissage via les clients" jugé trop
  risqué : fuite d'adresse de serveur privé + SSRF). Un sibling déjà
  whitelisté peut *suggérer* des URLs via `POST /s2s/siblings/sync`, elles
  atterrissent en file d'attente (`GET /api/admin/candidates`) pour revue
  admin — jamais promues seules. Les clients ne peuvent pas suggérer de
  sibling du tout.
- **Admin** ([Controllers/AdminController.cs](freespeech.relay/Controllers/AdminController.cs)) :
  JSON/HTTP simple (portable, pas d'UI web pour l'instant), protégé par le
  header `X-Admin-Secret` (config `Admin:Secret`, vide = tout refusé par
  défaut) :
  `GET/POST /api/admin/status` (public/privé),
  `GET /api/admin/siblings`,
  `GET/DELETE /api/admin/candidates`,
  `GET/POST/DELETE /api/admin/blacklist` (ban retire aussi du whitelist/
  candidats ; le ban lui-même reste manuel ou déclenché par throttling —
  seul le throttling reste à câbler, hors scope de cette session).
- **S2S entrant** ([Controllers/S2SController.cs](freespeech.relay/Controllers/S2SController.cs)),
  protégé par une **signature de requête** (voir « Authentification S2S »
  ci-dessous) — un refus (signature invalide ou périmée, rejeu, serveur
  privé, URL bannie, corps trop gros) renvoie systématiquement `404`
  générique, jamais de détail :
  `POST /s2s/lookup` (présence locale, **un seul saut** — ne relance jamais
  la cascade vers ses propres siblings, pour rester sans boucle). Le
  `is_online` du transport WebSocket (§11) cascade aussi vers les siblings
  de la même façon — sans ça, un client considère `is_online=false` pour un
  contact joignable seulement via un sibling et abandonne avant même de
  tenter l'envoi (bug trouvé en test manuel : l'échange de contact OTC et le
  flush de l'outbox de messages vérifient `is_online` avant d'envoyer).
  `POST /s2s/relay` (forward d'envelope, livré via le même
  `IMessageDeliveryService` que le WebSocket live),
  `POST /s2s/flag` (propagation WAITING/READY — la confiance est au niveau
  du sibling whitelisté, pas revérifiée au niveau du client `from`),
  `POST /s2s/siblings/sync` (réception de suggestions, cf. ci-dessus).
- **S2S sortant** ([Services/S2SClient.cs](freespeech.relay/Services/S2SClient.cs)) :
  chaque appel est best-effort (timeout 5s, erreurs avalées) — un sibling
  mort ne doit jamais bloquer l'envoi/le flag qui a déclenché la tentative.
  Déclenché depuis `RelaySocketHandler.HandleSendAsync` (cascade sur `send`
  sans destinataire local, dans l'ordre du whitelist, premier sibling qui
  répond présent) et `FlagsController.SetFlag` (propagation systématique à
  tous les siblings quand ce serveur est public).
- Vérifié en HTTP direct (2 instances serveur, admin + S2S) : refus par
  défaut sans lien accepté, refus générique avec une signature fausse, lookup/relay/flag
  fonctionnels entre relais liés, suggestion de candidat jamais
  auto-promue, ban qui retire bien des voisins/candidats. Test bout-en-bout
  avec de vrais clients de chat (contact OTC + `send` à travers deux
  serveurs siblings) **pas encore fait** — nécessite plusieurs fenêtres
  interactives, à valider manuellement.

#### Authentification S2S

Chaque requête `POST /s2s/*` est **signée par la clé du relais appelant** (en-têtes `X-Peer-Id`, `X-Peer-Ts`, `X-Peer-Sig` : voir « Liaison entre serveurs »).
Aucun secret ne circule.

Le récepteur vérifie, dans cet ordre, en renvoyant `404` au moindre échec :
serveur public ; en-têtes présents ; corps ≤ `Server:MaxMessageBytes` + 16 Kio ;
appelant lié et non banni ; signature ; |horloge − timestamp| ≤ 120 s ; signature jamais vue (cache mémoire, rejeu refusé).
Un sibling authentifié qui dépasse `Server:S2SRatePerMinute` (défaut 1200)
reçoit `429` + `Retry-After: 60` (journalisé).

À l'ajout d'un relais voisin : URL en **`https`**, ni
boucle locale ni réseau privé (anti-SSRF).
`Server:AllowInsecureSiblings=true` lève ces règles — dev et
tests uniquement. Le client S2S ne suit pas les redirections.

#### Bascule d'un client vers un sibling (« Moving a client »)

Un serveur saturé — ou qu'un administrateur vide avant maintenance — **conseille**
au client des siblings qui ont de la place ; c'est le client qui décide, et qui
prouve son identité (§11) auprès du nouveau serveur. Rien n'est déplacé de force.

- **Capacité** : `Server:MaxConnections` (0 = illimité). `POST /s2s/capacity`
  (signé, §10) → `{ "free_slots": n }` (`-1` illimité, `0` plein).
- **Choix des siblings** : le serveur interroge ses siblings whitelistés
  (réponse mise en cache 15 s, pour qu'un pic de charge ne crée pas une
  tempête S2S), écarte les pleins et les muets, tire au sort pondéré par la
  place libre, en garde 3 au plus.
- **Événement serveur → client** `server_redirect`
  `{ "servers": ["https://…", …], "reason": "load" | "admin" }`, envoyé juste
  après `registered` quand le serveur dépasse `MaxConnections` (si aucun
  sibling n'a de place, le client reste : le refuser le laisserait sans rien),
  ou à la demande : `POST /api/admin/drain { "count": n }` (X-Admin-Secret).
- **Événement serveur → client** `server_state` `{ "access": "open" | "allowlist", "provisioners": [...] }`, envoyé à
  tous les clients connectés quand l'administrateur change qui peut s'inscrire (`POST /api/admin/access/mode`) ou nomme un outil d'administration : l'appli montre alors le
  serveur comme public (`open`) ou privé (`allowlist`) sans attendre une reconnexion. Les clients hors ligne, en attente ou refusés
  l'apprennent à leur prochaine connexion (`registered` porte déjà `access`).
- **Côté client** : ne suit la suggestion que si elle vient d'un de **ses**
  serveurs, vers une URL `https` (http seulement si l'origine est http), au plus
  **3 fois par 24 h** (anti-rebond entre deux serveurs pleins). Il s'enregistre
  sur le nouveau serveur (preuve §11), remplace l'ancien dans sa liste de
  serveurs (même priorité), le note dans `retired_servers` (le serveur par
  défaut n'est pas rajouté au démarrage suivant), puis envoie un
  `identity_update` à tous ses contacts et groupes (mis en file d'attente
  s'il est hors ligne).
- Un contact qui écrit encore à l'ancien serveur retrouve le client via la
  cascade S2S (§10) tant que les deux serveurs sont siblings.

#### Jeton HTTP de session (`/api/flags`)

La réponse `registered` porte `http_token` (192 bits aléatoires, valable tant
que la connexion WebSocket vit). Les appels `GET/POST/DELETE /api/flags/…`
exigent `X-Discovery-Id` **et** `X-Flag-Token` : connaître un identifiant en
ligne ne suffit plus à lire ou poser ses flags. Le client en ajoute un à
chaque requête (il change à chaque reconnexion).
> **Règle** : `to_server` ne doit jamais toucher `KnownServers`. L'y ajouter
> automatiquement ferait fuiter l'adresse des serveurs privés d'entreprise (§8)
> vers la cascade discovery publique dès qu'un de leurs employés envoie un
> message à un contact externe.

## 11. Transport temps réel

**Principe : aucune dépendance propriétaire.** Tout le protocole doit rester
réimplémentable dans n'importe quel langage sans bibliothèque liée à un
framework précis. **WebSocket brut (RFC 6455)** est utilisé — pas de
protocole de couche supérieure type SignalR : n'importe quel client, dans
n'importe quel langage, avec juste une lib WebSocket standard (`ws` en JS,
`websockets` en Python, `gorilla/websocket` en Go, `System.Net.WebSockets` en
.NET...), peut parler à ce serveur.

- **Endpoint** : `GET /ws/relay` (upgrade WebSocket).
- **Framing** : chaque frame texte transporte un objet JSON unique avec un
  champ `type` qui détermine la suite du schéma. Pas de format binaire, pas
  de sérialiseur spécifique — JSON simple, lisible, débogable avec n'importe
  quel client WebSocket générique (ex. `websocat`, l'onglet réseau d'un
  navigateur).

**Client → serveur** :
```json
{ "type": "register", "request_id": "...", "discovery_id": "...", "enc_key": "base64", "sign_key": "base64", "invitation": "fs-inv-...", "request_access": true, "name": "Anna", "note": "..." }   // invitation : facultative, serveur privé (§10) ; request_access/name/note : demande d'accès, après consentement (§10)
{ "type": "register_proof", "request_id": "...", "signature": "base64" }
{ "type": "send", "request_id": "...", "envelope": { "from": "...", "to": "...", "crypto": 1, "msg": "..." } }
{ "type": "is_online", "request_id": "...", "discovery_id": "..." }
```

**Serveur → client** :
```json
{ "type": "challenge",        "request_id": "...", "nonce": "base64", "discovery_id": "..." }
{ "type": "registered",       "request_id": "..." }
{ "type": "send_result",      "request_id": "...", "delivered": true, "self_multi_device": false }
{ "type": "is_online_result", "request_id": "...", "discovery_id": "...", "online": true }
{ "type": "message",          "envelope": { ... } }   // push, pas de request_id
{ "type": "error",            "request_id": "...", "message": "..." }
```

- `request_id` est choisi par le client (ex. UUID) et recopié tel quel dans
  **toute** réponse à une requête (y compris `registered` et `error`) — c'est
  ce qui permet au client de corréler une requête et sa réponse sur une
  connexion qui reçoit aussi des pushes asynchrones (`message`) entre les
  deux. Seul `message` (un push, jamais une réponse) n'a pas de
  `request_id`. Aucune notion de "canal" ou d'"invocation RPC" au niveau
  transport : juste des messages JSON dans les deux sens.
- **Enregistrement avec preuve d'identité.** `register` doit être le premier
  message envoyé après connexion ; toute autre commande avant reçoit `error`.
  Sans preuve, n'importe qui pourrait s'enregistrer sous l'identifiant d'un autre,
  recevoir ses enveloppes (métadonnées) et faire croire à ses expéditeurs qu'un
  message est livré (donc perdu). Le déroulé est en deux temps :
  1. Le client envoie `register` avec son `discovery_id` et ses **deux clés publiques**
     (`enc_key` X25519, `sign_key` Ed25519, 32 octets chacune, base64). Le serveur vérifie que
     `SHA-256(enc_key ‖ sign_key)` (hexadécimal minuscule) est bien le `discovery_id` (§1),
     puis répond `challenge` avec un `nonce` aléatoire de 32 octets (base64, à usage unique).
  2. Le client signe en Ed25519, avec sa clé de signature, l'octet-chaîne UTF-8
     `freespeech-register-v1|<nonce>|<discovery_id>` et l'envoie dans `register_proof.signature`.
     Le serveur vérifie la signature avec `sign_key` : seulement alors la connexion est
     enregistrée et `registered` est renvoyé. Toute erreur (clés qui ne correspondent
     pas à l'identifiant, signature fausse, `register_proof` sans `register`) renvoie
     `error` et annule l'enregistrement en cours ; le nonce ne sert qu'une fois.
  3. **Contrôle d'accès** (§10), **après** la preuve : un serveur qui restreint les
     inscriptions répond `error` avec le message `private_server` (identité inconnue, il
     faut une invitation : champ `invitation` du `register`) ou `access_refused`
     (identité bannie), ou `access_pending` (demande d'accès déposée, en attente de l'administrateur ;
     champs `request_access`, `name`, `note` du `register`, §10). Ces trois codes sont stables ; le client en tire le texte à afficher.
  Les clés publiques sont publiques : seul le titulaire de la clé privée de signature
  peut répondre au défi. Option **de développement** côté serveur
  (`Server:AllowAnonymousRegister`, désactivée par défaut) : accepte un `register` sans clés,
  pour les scripts de test qui ne savent pas signer ; ne jamais l'activer sur un serveur public.
- **Limites de taille.** Une frame de plus de `Server:MaxMessageBytes` (512 Kio par défaut ;
  un chunk de fichier de 48 Kio ou une photo de profil de 100 Kio tiennent largement) ferme la
  connexion (code WebSocket 1009), au lieu de remplir la mémoire du serveur. Côté client, un texte
  de message est limité à 8 000 caractères (`msg_too_long`) ; un message reçu au-delà de quatre fois
  cette limite est ignoré. Les images envoyées depuis le mobile sont réduites (1 600 px, JPEG) avant
  l'envoi, comme les vidéos (720p). Côté nginx : limites de connexions et de requêtes par adresse.
- **Limites de volume.** Le relais ne distingue pas un chunk de fichier d'un message (enveloppes
  opaques) : il limite les **octets envoyés**, par identité et par adresse réseau, pour qu'un client
  ne le sature pas. Un quota quotidien par identité (`Server:MaxBytesPerIdentityPerDay`, 1 Gio sur
  24 heures glissantes), un débit par identité (`Server:MaxBytesPerIdentityPer10Minutes`, 300 Mio :
  plus vite est ralenti, retenu 20 s au plus, pas coupé) et un quota quotidien par adresse
  (`Server:MaxBytesPerAddressPerDay`, 5 Gio, pour les identités derrière une même adresse) ; `0`
  désactive une limite, ce que fait un serveur privé pour servir de gros fichiers. Un `send` refusé
  répond `error` avec le code stable `quota_exceeded` ou `rate_limited` et `retry_after` (secondes) ;
  la connexion reste ouverte. `registered.daily_quota` et `send_result.quota_left` permettent au client
  d'afficher ce qui reste. La limite d'un fichier est celle du client : c'est un réglage de
  l'utilisateur, qui confirme avant d'en envoyer un plus gros (§6).
- `send` échoue en `error` si `envelope.from` ne correspond pas au
  `discovery_id` enregistré sur cette connexion (anti-usurpation, cf. §5) ;
  sinon répond `send_result.delivered = true` si au moins une connexion du
  destinataire a reçu le push `message`, `false` sinon (le client bascule
  alors sur WAITING/READY, §9). `send_result.self_multi_device = true` si
  l'expéditeur a au moins une autre connexion active que celle qui vient
  d'envoyer — signal utilisé côté client pour le self-sync conditionnel
  (§1/§5), le serveur ne rejouant plus lui-même l'enveloppe chiffrée vers
  les autres devices de l'expéditeur (illisible sans la bonne clé privée).
- Une identité peut enregistrer plusieurs connexions simultanées
  (multi-device, §1) ; un push `message` est envoyé à **toutes** les
  connexions actives du destinataire.
- Les endpoints HTTP (`/api/flags`, `/api/time`) restent du JSON sur HTTP
  classique — déjà portable nativement, aucun changement.
- **Reconnexion (bibliothèque cliente)** : une perte de connexion au relais lui-même
  (pas seulement destinataire hors-ligne — coupure réseau, redémarrage
  serveur, cf. §9 mobile) est traitée comme le cas destinataire hors-ligne :
  le message part dans la file locale (outbox), le flag WAITING est tenté
  en best-effort (silencieusement ignoré s'il échoue aussi). Le tick
  périodique (§9) retente la reconnexion du WebSocket en premier, avant de
  retenter contacts en attente et outbox — `RelayClient.TryReconnectAsync`.

## 12. Points reportés

- Rotation de clé de groupe périodique (hors exclusion/départ/compromission, déjà couverts au §7).
- `send` serveur multi-destinataires (une seule copie envoyée, le serveur fait le fan-out) pour alléger l'émission de groupe (§7).
- Groupes : chiffrement à clés par expéditeur avec cliquet / MLS si des groupes de plusieurs milliers de membres apparaissent.
- Confidentialité persistante (cliquet) pour les messages 1-à-1 ; chiffrement des pièces jointes au repos.
- **Téléphone volé : effacement et révocation à distance** (conception arrêtée ; **(1) et (2) sont implémentés**, voir §1 ; (3) et (4) restent à faire). (1) Une **clé de révocation** dérivée de
  la graine des phrases par un autre libellé HKDF ; les appareils n'en gardent que la clé publique, la privée n'existe que pendant la
  saisie des phrases. (2) Lettre `wipe` à soi-même, signée par cette clé (une signature par la clé d'identité ne suffirait pas : le voleur
  l'a dans la base du téléphone) ; les appareils installés **avant** son horodatage s'effacent, les autres l'ignorent. Remise « au mieux » :
  le relais ne stocke rien, elle n'arrive que si l'expéditeur est joignable quand l'appareil volé se connecte. (3) `identity_moved
  { nouveau_discovery_id }` signée par la clé de révocation (publiée dans la fiche de contact), pour migrer contacts et groupes vers une
  nouvelle identité (nouvelles phrases) : le `discovery_id` dérive des clés, une rotation de clé *est* un changement d'identité. Si les phrases
  ont fuité, aucune rotation automatique n'est sûre. (4) Rotation périodique des clés de **réception** (sous-clés annoncées, signées) =
  confidentialité persistante partielle ; demande une synchronisation entre appareils. Verrouillage par PIN : voir §1.
- Messages éphémères : non retenus (« supprimer pour tous » suffit ; un délai ne lie pas un destinataire malveillant).
- **Migration d'identité `identity_moved`** (**implémentée le 2026-10-06**, voir §1 « Téléphone volé : migration » ; le détail ci-dessous est la conception d'origine, le §1 fait foi).
  - **Principe** : après un wipe, l'utilisateur passe à une **nouvelle identité** (nouvelles phrases, à sauvegarder comme à la création).
    Lettre `identity_moved { old_id, new_card, ts, sig }`, `sig` = signature de la **clé de révocation de l'ancienne identité** sur
    `"FreeSpeech-moved-v1|<old_id>|<new_id>|<ts>"`, envoyée **depuis l'ancien identifiant** (restauré avec les anciennes phrases sur le
    nouvel appareil). Un `identity_update` ordinaire ne convient pas : les clés y sont immuables, et il est signé par la clé de signature
    que le voleur détient.
  - **Clé de révocation publique** : **épinglée à la première réception, immuable** (fiche de contact, `identity_update`, et pour les membres
    de groupe qui ne sont pas contacts le `member_profile`, signé par le membre). **Jamais** prise dans le `GroupState` : l'identifiant
    (`SHA-256(enc ‖ sign)`) ne la couvre pas, un modérateur pourrait la substituer. Sans clé épinglée (contacts d'avant la fonction) : aucune
    migration, jamais acceptée sur parole ; échange d'une nouvelle fiche.
  - **Contacts** : mécanique automatique (fiche importée, contact créé, historique lié), **décision manuelle** en un geste, avec le numéro de
    sécurité. Réglage « accepter automatiquement » désactivé par défaut, jamais appliqué à un contact marqué ✔. Un seul `moved` par ancien
    identifiant (le suivant est signalé comme conflit) ; lié à `old_id` et `ts` contre le rejeu. Pas de `identity_revoked` automatique : qui détient
    les phrases pourrait s'en servir pour bloquer son propriétaire ; au plus un avertissement.
  - **Groupes — avant le `moved`, l'assistant** : pour chaque groupe où l'on est **créateur**, demande un successeur (modérateurs d'abord, sinon
    un membre ; avertissement : il aura tous les pouvoirs) et émet le transfert ; pour chaque groupe où l'on est **modérateur**, retire son
    rôle ; sans attendre d'accusé (les états circulent de pair à pair). L'ancien identifiant devient un simple membre. Ordre : `wipe`, puis
    transferts, puis `moved`. Un groupe laissé de côté reste exposé (le téléphone volé garde ses droits).
  - **Groupes — à la réception** : un modérateur qui reçoit un `moved` dont la signature est **vérifiée** exclut l'ancien identifiant avec
    **rotation de clé** dans un seul état (réglage « exclure automatiquement un compte migré », activé par défaut) : la nouvelle clé n'est pas
    remise à l'ancien identifiant, qui perd la lecture des messages suivants (une rotation seule ne suffirait pas : il figurerait encore dans
    `members`). Signature non vérifiable : demande manuelle dans « Demandes ». Le **nouveau compte n'est réinvité qu'après validation**, et
    accepte l'invitation (§7, règle 1) ; les rôles ne sont pas repris. Un identifiant **banni** ne peut pas migrer (l'avis est refusé). Le type de lettre est ajouté à `GroupService.GroupLetterTypes`. Affichage : « ancien compte » sur l'ancien identifiant.
  - **Limites** : si les **phrases** ont fuité, rien n'est protégé (même signature valide) ; le voleur peut agir avant l'assistant (mêmes clés
    d'identité) ; remise au mieux (le relais ne stocke rien) ; une exclusion se répare par une réinvitation, d'où l'exclusion automatique
    acceptée malgré le risque de blocage par un détenteur des phrases.
  - **Tests prévus** : migration d'un contact (signature valide, fausse, sans épingle, rejeu, conflit) ; transfert de créateur puis retrait de
    rôle ; exclusion automatique avec rotation ; créateur seul ; banni qui migre ; signature non vérifiable.
- Vote de retrait d'un modérateur.
- Suppression par un modérateur d'un message d'autrui dans un groupe ; limite de durée pour modifier ou supprimer pour tous ; synchronisation des modifications/suppressions entre appareils d'une même identité.
- Format exact des serveurs "de confiance" publics par défaut en prod.

## 13. Sécurité : précautions et failles connues

Index des risques, avec renvoi vers la section qui détaille la mesure. « Reste » = ce qui n'est pas couvert aujourd'hui.

| Risque | Mesure | Reste |
|---|---|---|
| Quelqu'un prend le téléphone déverrouillé et cherche les phrases | les phrases ne sont jamais stockées ; la page Sauvegarde ne les propose pas (§1, §2) | **pas de verrouillage de l'appli** (code, biométrie) : les discussions restent lisibles ; piste |
| Quelqu'un voit l'écran de sauvegarde, ou l'appli dans la liste des applications récentes | écran unique, sans retour (§1) ; réglage **écran protégé** (Android, désactivé par défaut) : ni capture d'écran ni aperçu dans les applications récentes (`FLAG_SECURE`) | réglage désactivé : captures d'écran possibles ; regard par-dessus l'épaule |
| Fuite des phrases par presse-papiers, spouleur, PDF, cloud | avertissements à l'écran et sur la feuille (§1) | à la charge de l'utilisateur |
| Vol des phrases ou du QR code papier | papier en lieu sûr | **aucune détection ni révocation** : la même identité peut tourner sur un autre appareil sans que rien ne le montre |
| Fichier de clé volé | chiffré par mot de passe, Argon2id 64 Mio, coût borné, AEAD (§1) | mot de passe faible ou court ; mot de passe non récupérable |
| Fichier de sauvegarde volé | chiffré par une clé dérivée de l'identité (§2) | quiconque a les phrases le lit ; copie en cache de l'appli |
| Phrases saisies à la main trop faibles | 75 caractères au moins, Argon2id (§1) | force réelle laissée à l'utilisateur |
| Usurpation d'identifiant sur un serveur | enregistrement avec preuve de possession de la clé de signature (§11) | — |
| Rejeu, falsification entre serveurs | S2S signé, fenêtre de 120 s, nonce, quota (§10) | liste noire automatique des serveurs fautifs (reporté) |
| Observation par le fournisseur de push | message de réveil constant, sans contenu (§9) | Google voit jeton et heures de réveil |
| Aperçu lisible sur l'écran verrouillé | la notification de réveil n'a aucun contenu (§9) ; réglage **écran verrouillé** (Android, activé par défaut) : une notification faite par l'appli n'affiche que « Nouveau message » tant que le téléphone est verrouillé | réglage désactivé : expéditeur et texte visibles sur l'écran verrouillé |
| Clavier qui suggère ou apprend de ce qui est tapé | réglage **clavier discret** (Android, désactivé par défaut) : ni suggestions ni apprentissage personnalisé (`IME_FLAG_NO_PERSONALIZED_LEARNING`) dans les champs de l'appli | c'est le clavier qui décide d'obéir ; un clavier malveillant lit tout ce qui est tapé, quel que soit le drapeau |
| Logiciel espion ou téléphone compromis qui lit ce que l'appli stocke | contenu de la base chiffré champ par champ (`Vault`) ; identifiants et horodatages lisibles, clé dans le stockage sécurisé de la plateforme, pas de sauvegarde cloud des données de l'appli | les fichiers joints et leurs caches sont **en clair** dans le dossier privé de l'appli ; un système compromis, ou un accès root, lit ce que l'appli lit elle-même |
| Téléphone volé ou prêté, déverrouillé | verrouillage par PIN optionnel, effacement après N échecs optionnel, effacement à distance puis migration vers une nouvelle identité avec exclusion des groupes (§1) | PIN désactivé par défaut et contourné par root ; l'ancienne identité reste valide pour qui a lu les clés tant que les contacts n'ont pas suivi la migration ; rien ne protège si les phrases ont fuité |
| Serveur curieux | ne voit jamais le contenu, ni les phrases (§1, §5) | voit **qui écrit à qui** et quand (flags) |
| Confidentialité persistante | — | pas de cliquet (Double Ratchet reporté, §12) |

