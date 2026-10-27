# Échanges de messages : tous les cas

Ce document suit un message dans chaque situation, d'abord avec un contact (un à un) puis dans un groupe. Il complète [HOW_IT_WORKS.fr.md](HOW_IT_WORKS.fr.md), qui décrit le rôle de chaque composant. Les formats exacts sont dans [PROTOCOL.fr.md](PROTOCOL.fr.md) et [PROTOCOL_API.fr.md](PROTOCOL_API.fr.md). Dans les schémas, `Rx` désigne un relais et `A`, `B`, `C` des appareils.

Trois règles valent dans tous les cas. Le relais ne garde aucun message : un message qui n'est pas remis reste dans la file d'envoi de l'expéditeur (écran « Envois en cours »), sur son appareil. Le relais considère un message comme remis dès qu'il l'a écrit sur la connexion du destinataire, même si cette connexion est morte sans qu'il le sache. Le destinataire confirme donc chaque remise par un accusé de réception chiffré, et sans accusé l'expéditeur renvoie le même message, avec le même identifiant, après 30 s, 2 min, 10 min, 1 h puis 6 h ; le destinataire ne le traite qu'une fois. Un message que le relais n'a pas pu remettre parce que le destinataire est absent n'est pas concerné : il attend dans la file et part quand le destinataire se manifeste (réveil par push ou connexion). En secours, l'application réessaie à chaque cycle de 30 secondes pendant les 10 premières minutes, puis toutes les 15 minutes jusqu'à 24 heures, puis toutes les 6 heures jusqu'à 3 jours. Enfin tout est signé et chiffré de bout en bout : le relais lit l'enveloppe (expéditeur, destinataire, date) et jamais le contenu.

## Partie 1. Avec un contact

### 1.1 Devenir contacts

```
 Alice                                              Bob
   │ fiche : clés publiques, pseudo, serveurs, code à usage unique
   │ ─────── QR code (en personne) ou message (à distance) ───────►│
   │                                                               │ importe la fiche
   │◄───── fiche de Bob, chiffrée avec le code, par un relais ─────│
   │ confirme (automatique avec un QR, à la main à distance)       │
   │ ─────────────────── confirmation ────────────────────────────►│
   ▼ contacts confirmés : chacun connaît les serveurs de l'autre   ▼
```

Le code d'un QR est valable 10 minutes et l'import est automatique. Le code d'un partage à distance (copier-coller, courriel) est valable un mois et demande une validation manuelle. L'échange passe par les relais, donc le contact n'est confirmé que lorsque les deux sont en ligne ; une demande en attente est renvoyée à chaque cycle de l'application. Pour s'assurer que personne n'a glissé ses propres clés dans l'échange, les deux comparent un numéro de sécurité en dehors de l'application (écran « Vérifier la sécurité »).

### 1.2 Les deux sont en ligne, sur le même serveur

```
 A ── enveloppe ─► R1 ── écrit sur la connexion de B ─► B
 A ◄── accusé (chiffré) ── R1 ◄────────────────────────── B
```
A voit ✓ (envoyé) quand le relais a l'enveloppe, puis la confirmation de B. B conserve le message, le déchiffre et l'affiche.

### 1.3 Les deux sont sur des serveurs différents

```
 A ─ connecté à R1          R2 ─ B (B annonce R2 dans sa fiche)
 A ─── enveloppe, connexion directe à R2 ───►│── B
 A ◄─────── accusé, par un serveur de A ◄─────│◄─ B
```
A ouvre elle-même une connexion vers le serveur de B ; R1 et R2 n'ont pas besoin de se connaître. B répond de la même façon sur un serveur de A. Si B annonce plusieurs serveurs, A les essaie dans l'ordre de priorité et passe tout de suite au suivant quand un serveur ne répond pas ou que B n'y est pas.

### 1.4 B est hors ligne, application fermée, avec push

```
 A ─► R2 : B est absent. R2 note « A attend B » et appelle la passerelle push
 R2 ─► passerelle ─► Google/Apple ─► téléphone de B (réveil invisible, sans contenu)
 B : se réveille, se connecte à ses serveurs, se déclare prêt
 R2 ─► A : « B est prêt »   →   A renvoie aussitôt sa file
 A ─► R2 ─► B : déchiffre, notification locale (nom et aperçu produits sur l'appareil)
```
Tant que B n'était pas là, le message n'a pas quitté l'appareil de A.

### 1.5 B est hors ligne, sans push

Même séquence sans le réveil. Le message attend dans la file de A. B se connecte à la prochaine interrogation du système (environ 15 minutes sur Android) ou à l'ouverture de l'application, A est prévenu et renvoie. Le client de bureau reste connecté tant qu'il tourne.

### 1.6 A n'a aucun serveur joignable

Le message reste dans la file de A et rien n'est déposé sur un serveur. L'application se reconnecte toutes les 30 secondes et la file repart dès qu'un serveur répond. On peut arrêter l'attente d'un message (« Ne plus attendre ») puis la reprendre.

### 1.6 bis Le serveur restreint commun ne répond pas

```
 A et B ont en commun le serveur restreint P (et chacun a aussi des serveurs ouverts)

 P répond, B y est connecté        : A ─► P ─► B                      (seul chemin utilisé)
 P répond, B n'y est pas connecté  : le message attend chez A         (B est simplement absent)
 P ne répond pas                   : le message attend chez A, « en attente du serveur privé »
   si A et B ont accepté le repli (route_policy) :
      Ask  : l'application de A pose la question tout de suite, une seule fois ; sur « oui » les
             messages suivants passent par un serveur ouvert de B, sans nouvelle question, jusqu'au retour de P
      Auto : après 2 minutes sans réponse de P, A ─► serveur ouvert de B ─► B  (avis « envoyé par un serveur ouvert »)
   B répond de la même façon, selon son propre réglage (en mode Ask, la question lui est posée une fois de son côté)
 P revient                         : chaque envoi recommence par P ; dès qu'il remet un message, le repli est terminé
```
Sans l'accord des deux, ou sans serveur restreint commun, il n'y a pas de repli : le message attend ou suit le chemin ordinaire.

### 1.7 Coupures et confirmations perdues

```
Cas a. coupure pendant l'envoi        : A ne reçoit pas d'accusé → renvoi (30 s, 2 min, 10 min, 1 h, 6 h)
Cas b. coupure pendant la réception   : B ne reçoit jamais un morceau de message (messages complets seulement) ;
                                         A renvoie comme au cas a
Cas c. message reçu, accusé perdu     : A renvoie ; B reconnaît le message, ne l'affiche pas deux fois
                                         et renvoie l'accusé
Cas d. A éteint juste après l'envoi   : la file est gardée dans la base de A ; elle repart au retour de A
```
Après le dernier délai (6 h), un message qui n'a pas été confirmé n'est plus renvoyé.

### 1.8 Lu, « écrit… », réactions

Quand B ouvre la conversation, son application envoie un accusé de lecture qui couvre tous les messages précédents, et chez A le ✓ devient un ✓✓ doré. Cet accusé part sans file d'attente et B peut le désactiver. L'indicateur « écrit… » est un signal éphémère, qui n'est ni gardé ni renvoyé et qui se désactive aussi. Une réaction est un message comme les autres, avec la file d'envoi et la confirmation.

### 1.9 Modifier, supprimer

| Action | Ce qui part | Si B est hors ligne |
|---|---|---|
| Modifier | une modification, la plus récente l'emporte | file d'envoi ; un message pas encore livré est simplement modifié dans la file |
| Supprimer chez moi | rien (local) | sans objet |
| Supprimer pour tous | une suppression ; la ligne reste « message supprimé » | file d'envoi ; un message pas encore livré est retiré de la file, rien ne part |

Seul l'auteur peut modifier ou supprimer son message. L'ordre d'arrivée n'est pas garanti : une modification qui arrive avant le message attend, et une suppression l'emporte sur toute modification.

### 1.10 Un fichier

```
 A ─► B : annonce (nom, taille, vignette) comme un message
 B décide : télécharger ?   non → rien   oui → ─► A : demande
 A ─► B : morceaux de 48 Kio, chacun chiffré et prouvé par l'arbre de Merkle
 B : vérifie chaque morceau, assemble
```
Le fichier ne part que si les deux sont en ligne. Si A est absent, la demande attend et le fichier arrive quand A revient. Côté B, on règle le téléchargement automatique selon la taille, le wifi seul ou les données mobiles, et la confirmation avant d'envoyer un gros fichier. Un fichier que A a retiré n'est plus servi et B voit « Fichier retiré par l'expéditeur ». Un serveur qui limite le volume par jour suspend l'envoi jusqu'à la fin du délai indiqué (écran « Limite du serveur »).

### 1.11 B change de serveur

B envoie à ses contacts une mise à jour de son profil avec sa nouvelle liste de serveurs ; elle est mise en file pour un contact hors ligne. Entre-temps, un message envoyé au serveur que B a quitté est retrouvé si les deux serveurs sont liés. Sinon A le renvoie quand elle connaît la nouvelle adresse.

### 1.12 B a plusieurs appareils

Les appareils d'une même identité sont connectés ensemble. Un message de A est remis à chacun de ceux qui sont en ligne. Un message envoyé depuis un appareil de B est copié sur ses autres appareils. Un appareil restauré demande l'historique à un autre, avec un code de contrôle.

## Partie 2. Dans un groupe

Un groupe n'existe sur aucun serveur. C'est un état signé (membres, modérateurs, clé de l'époque) que chaque membre garde sur son appareil. Chaque changement de composition ou de rôle crée une nouvelle époque, avec une nouvelle clé.

### 2.1 Création et invitation

```
 Créateur C                                        Invité D
   │ crée l'état (époque 1), tire la clé
   │ ── état signé + clé chiffrée pour D (un à un, mis en file) ─►│
   │                                                              │ voit l'invitation, rien n'est rejoint
   │                                         accepter ─► rejoint, demande le rattrapage aux membres
   │                                         refuser  ─► l'invitation est supprimée, C n'en sait rien
```
Un modérateur invite un de ses contacts. Un membre qui n'est pas modérateur peut proposer un de ses contacts aux modérateurs, qui l'invitent avec la preuve de ce parrainage.

### 2.2 Un message, tous les membres en ligne

```
 A ── une enveloppe par membre (même contenu, chiffré avec la clé du groupe) ──►
   ├─► Rb ─► B          chaque enveloppe suit les serveurs du membre visé
   ├─► Rc ─► C          (comme en un à un : des serveurs différents sont possibles)
   └─► Rd ─► D
```
Le contenu est chiffré avec la clé de l'époque et signé par l'auteur. Le relais ne voit ni le nom ni l'identifiant du groupe, seulement une étiquette opaque qui désigne la clé. Un membre qui n'est pas contact d'un autre peut lui écrire, puisque l'état du groupe contient les clés publiques et les serveurs de chaque membre. Il n'y a ni accusé de réception ni accusé de lecture dans un groupe, seulement le compteur de messages non lus.

### 2.3 Un membre est hors ligne : le rattrapage

```
 A écrit les messages 3 à 6 puis se déconnecte.        B a 4-6, C a 3-6.
 D se connecte : « j'ai jusqu'à 2 de A »  ─►  B et C (jusqu'à 3 pairs connectés)
 B répond 4-6 ; C répond 3-6 ; D ignore les doublons et se retrouve complet.
```
Il n'y a pas de file par destinataire. Chaque membre garde un journal des messages du groupe et le sert aux autres, même si l'auteur est éteint. Quand un membre voit qu'un autre a plus de messages que lui, il les demande à son tour, de sorte que les membres convergent sans dépendre de l'auteur. La signature de l'auteur, à l'intérieur du chiffré, empêche un pair de fabriquer un message ; il peut seulement le rejouer.

### 2.4 Arrivée, départ, exclusion

```
 modérateur ─► nouvel état signé (époque n+1) + nouvelle clé ─► chaque membre restant
 membre exclu ─► reçoit l'état SANS la clé : il sait qu'il est sorti et ne lira pas la suite
```
Un membre exclu garde ce qu'il a déjà reçu, mais il ne peut pas lire les messages suivants, même interceptés. Un message d'un membre retiré est refusé en livraison directe. Il n'est accepté que s'il est relayé par un membre actuel et s'il date d'une époque où l'auteur était membre. Si deux modérateurs agissent en même temps, l'état dont l'empreinte est la plus petite l'emporte ; l'autre est conservé, sa clé reste lisible, et son modérateur renvoie son changement par-dessus. Renommer le groupe ou changer son image ne change pas la clé.

### 2.5 Modifier, supprimer, épingler, bannir

Modifier et supprimer sont les mêmes opérations qu'en un à un, envoyées comme messages de groupe, et seul l'auteur peut les faire. Un modérateur peut épingler un message (trois au plus) et bannir une personne. Le bannissement est une liste noire que les membres appliquent d'eux-mêmes, ce qui diffère de l'exclusion, qui change la clé.

### 2.6 Un fichier dans un groupe

```
 A ─► groupe : annonce du fichier (empreinte de contenu, taille, nom)
 D veut le fichier : demande un à un à n'importe quel membre qui l'a en entier
 B (qui l'a déjà téléchargé) et A servent des morceaux à D en parallèle
```
D vérifie chaque morceau avec l'arbre de Merkle et écarte un pair qui envoie un morceau erroné. L'auteur n'a pas besoin d'être en ligne tant qu'un autre membre détient le fichier, et chaque détenteur vérifie que le demandeur est membre de l'époque actuelle.

### 2.7 Quitter, revenir

Quitter change l'état du groupe comme une exclusion, avec une nouvelle clé. Le groupe reste lisible dans les archives, en lecture seule. Un modérateur peut réinviter la personne, qui accepte ou refuse ; si elle accepte, le groupe redevient actif avec son historique.
