# Fonctionnement : serveurs, clients, réveil du mobile

Ce document décrit comment les programmes de FreeSpeech coopèrent. Le détail des trames est dans [PROTOCOL.fr.md](PROTOCOL.fr.md) et [PROTOCOL_API.fr.md](PROTOCOL_API.fr.md) ; les renvois du type « § 8 » désignent les sections du protocole. Les fonctions des applications sont listées dans [FEATURES.fr.md](FEATURES.fr.md), le trajet d'un message dans chaque situation dans [MESSAGE_FLOWS.fr.md](MESSAGE_FLOWS.fr.md), l'organisation du code dans [ARCHITECTURE.fr.md](ARCHITECTURE.fr.md).

## 1. Les quatre composants

| Composant | Rôle | Ce qu'il conserve |
|---|---|---|
| Application (mobile, bureau, console) | Calcule l'identité, chiffre, signe, garde l'historique | Clés, contacts, messages et fichiers, dans une base chiffrée sur l'appareil |
| Relais (`freespeech.relay`) | Reçoit une enveloppe chiffrée et la transmet à la connexion du destinataire | En mémoire seulement : les appareils connectés et quelques marqueurs d'attente. Pas de boîte aux lettres |
| Passerelle push (`freespeech.push`) | Envoie un signal de réveil sans contenu à un téléphone dont l'application est fermée | Les jetons de réveil qu'elle reçoit et un compteur par relais. Les identifiants Google (FCM) et Apple (APNs) ne sont que chez elle |
| Annuaire (`freespeech.directory`) | Dit à une application qui ne connaît aucun relais lesquels acceptent de nouveaux comptes | Les relais liés, avec leur pays et leurs places disponibles. Rien sur les utilisateurs, aucune adresse dans les journaux |

Les composants sont indépendants. Un relais fonctionne sans passerelle ni annuaire ; dans ce cas les téléphones viennent vérifier périodiquement s'ils ont des messages. Une application n'a besoin d'aucun serveur tant qu'elle n'envoie ni ne reçoit rien.

## 2. Un message, du début à la fin

```
 Alice (téléphone)            Relais de Bob             Bob (téléphone)
        │  1. enveloppe chiffrée   │                          │
        │ ───────────────────────► │  2. Bob est-il connecté ?│
        │                          │ ── oui : la transmet ──► │  3. déchiffre, conserve
        │                          │ ◄── accusé (chiffré) ─── │
        │ ◄── accusé retransmis ── │                          │
```

1. L'application d'Alice signe le message, le chiffre pour Bob avec une clé éphémère neuve et en fait une enveloppe qui contient l'expéditeur, le destinataire, l'horodatage et le contenu chiffré (§ 5). Le relais lit l'enveloppe pour l'acheminer et ne lit pas le contenu.
2. Alice dépose l'enveloppe sur un des serveurs de Bob, tels que sa fiche de contact les annonce. Elle les essaie dans l'ordre de priorité. Si un serveur ne répond pas, ou si Bob n'y est pas connecté, elle passe au suivant (§ 8).
3. Si Bob est connecté quelque part, le relais transmet l'enveloppe sur sa connexion. Bob la déchiffre, vérifie la signature, conserve le message et renvoie un accusé de réception chiffré. Le relais croit le message remis dès qu'il l'a écrit sur la connexion de Bob, même si cette connexion est morte sans qu'il le sache. Sans accusé, Alice garde donc le message dans sa file d'envoi et le renvoie après 30 s, 2 min, 10 min, 1 h puis 6 h. Un message déjà reçu n'est pas traité deux fois, mais il est acquitté de nouveau (§ 5, « Livraison confirmée »).
4. Si Bob n'est connecté sur aucun de ses serveurs, le message reste chez Alice, dans sa file d'envoi (écran « Envois en cours »). Alice dépose un marqueur d'attente sur chaque serveur de Bob qu'elle peut joindre. Quand Bob se connecte, le relais le signale à Alice, dont l'application renvoie sa file sans attendre le prochain essai.

Un message n'est donc remis que si les deux appareils sont connectés en même temps. Les relais n'ont rien à sauvegarder ni à purger, et ne détiennent aucun message qu'une autorité pourrait leur réclamer.

## 3. Les contacts n'ont pas besoin d'être sur le même serveur

Une application s'enregistre à la fois sur tous les serveurs de sa liste qu'elle peut joindre, sans limite de nombre, par exemple le serveur privé d'une entreprise et un serveur public (§ 8). Sa fiche de contact annonce cette liste avec une priorité par serveur.

Alice n'a pas besoin d'être sur le serveur de Bob, et son serveur n'a pas besoin de connaître celui de Bob.

```
 Alice ── relais X          relais Z ── Bob
   │                           ▲
   └── elle écrit directement ─┘     X et Z peuvent s'ignorer
```

Alice se connecte elle-même au serveur de Bob pour y déposer l'enveloppe. La connexion est établie à la demande et abandonnée si le serveur tombe. Bob répond de la même façon, sur un des serveurs d'Alice. Le choix du serveur est fait par l'application, parce que seule elle sait quels serveurs elle atteint depuis son réseau : un serveur privé peut se trouver dans le réseau d'une entreprise, hors de portée d'un autre serveur, comme sur internet. Si Bob n'est connecté que sur une partie de ses serveurs, Alice essaie les suivants.

Un lien entre deux relais est facultatif (§ 10). Il sert à passer une enveloppe à un relais voisin où le destinataire est connecté, par exemple quand un contact écrit au serveur que le destinataire a quitté. Il sert aussi à conseiller à un client de passer sur un voisin qui a de la place (§ 10, « déplacement d'un client ») et à s'inscrire auprès d'un annuaire. Seul l'administrateur d'un relais modifie la liste de ses voisins.

## 4. Serveur privé et serveur public

Deux réglages distincts portent le mot « privé » (§ 10).

Le mode d'accès dit qui peut s'inscrire sur le serveur. En mode `open`, tout le monde, sauf les identités bannies. En mode `allowlist`, seules les identités que l'administrateur a autorisées, par une invitation à usage unique ou en acceptant une demande. Un serveur en `allowlist` est appelé ici serveur privé ou serveur restreint.

Les liens entre serveurs disent avec qui le serveur communique. Un relais privé n'accepte d'appels que de ses voisins explicitement liés, les annuaires ne le proposent pas et il ne leur envoie rien.

Un usage courant consiste à ouvrir un serveur privé pour ses proches et à rester joignable sur un serveur public pour les autres.

```
                       votre fiche de contact : serveurs = [ public (priorité 0), privé (priorité 1) ]
   proches ──────────► serveur privé ◄──────── vous (inscrit sur les deux à la fois)
   (invités)                                    ▲
   connaissances ────► serveur public ──────────┘
   (sans accès au privé)
```

Vous êtes inscrit sur les deux serveurs et vous recevez ce qui arrive sur l'un comme sur l'autre. Votre fiche indique quels serveurs sont restreints (propriété `restricted`), en listant les serveurs ouverts d'abord. Un serveur restreint sur lequel le lecteur de la fiche n'est pas autorisé n'est jamais contacté : l'application ne tente pas la connexion et l'administrateur du serveur ne voit aucun refus. La fiche ne donne pas l'adresse d'un serveur restreint, seulement un identifiant que les membres de ce serveur reconnaissent : une personne qui reçoit votre fiche sans en être membre ne l'apprend pas, et un membre qui ajoute ce serveur reconnaît d'un coup tous ses contacts qui y sont.

Quand deux contacts ont un serveur restreint en commun, ils ne s'écrivent que par ce serveur, ou par tous ceux qu'ils ont en commun, dans l'ordre de la fiche. Si le serveur ne répond pas, le message attend dans la file d'envoi avec la mention « en attente du serveur privé ». Il ne passe pas par un serveur ouvert sans l'accord des deux. Avec une simple connaissance, qui n'a aucun serveur restreint en commun avec vous, l'envoi suit l'ordre de sa fiche, par ses serveurs ouverts.

Le repli sur un serveur ouvert est un réglage par contact, que chacun fait pour ses propres envois : attendre (`Never`, par défaut), être consulté (`Ask`) ou laisser faire (`Auto`). Chaque côté communique son choix par un petit message chiffré, `route_policy`, qui passe par le serveur restreint commun. Le repli n'a lieu que si les deux ont accepté. En mode `Ask`, l'application pose la question dès que le serveur restreint ne répond pas, une fois par panne, et la réponse « oui » vaut jusqu'au retour du serveur. En mode `Auto`, le repli commence après 2 minutes sans réponse. Le contact répond de la même façon, selon son propre réglage.

Le repli n'est jamais mémorisé. Chaque envoi, et chaque renvoi de la file, essaie d'abord le serveur restreint, et dès que celui-ci remet un message on l'utilise de nouveau. L'application affiche un avis quand le repli commence et quand il se termine. Pour chaque destinataire, l'application utilise la liste de ses serveurs, sans rien demander à l'utilisateur.

La liste des discussions ne distingue pas les serveurs. L'état de chacun (connecté, privé, en attente de validation, refusé) se voit dans les réglages, et le réglage « Si le serveur privé ne répond pas » se trouve dans la liste des contacts. Un serveur privé peut être installé dans le réseau d'une entreprise ou sur internet. Seuls les appareils qui l'atteignent s'y connectent, et les autres passent par le serveur suivant de la liste. Les membres d'un groupe peuvent eux aussi être répartis entre un serveur privé et des serveurs publics.

Les échanges avec les proches ne passent que par un serveur administré par soi-même ou par une personne de confiance. Si un serveur public tombe ou est bloqué, ces échanges continuent. Si le serveur privé change d'adresse, l'application envoie la nouvelle liste aux contacts (§ 10, « déplacement d'un client » ; MESSAGE_FLOWS, cas 1.11).

L'administrateur d'un serveur voit les métadonnées de ceux qui s'y connectent : qui, quand, et l'adresse IP pendant la connexion. Il ne voit jamais le contenu. Le serveur public où se trouvent les connaissances voit de même les métadonnées de ces échanges. Rien n'est prévu pour dissimuler le trafic à un réseau qui filtre : une connexion WebSocket en HTTPS vers un domaine peut être bloquée d'après son adresse ou son nom. Les contacts qui ont un autre serveur dans leur liste restent alors joignables, et pour les autres les messages attendent dans la file de l'expéditeur.

## 5. Réveiller un téléphone dont l'application est fermée

Un téléphone ne garde pas de connexion ouverte quand l'application est fermée. Il est réveillé par une notification push sans contenu (§ 9).

```
 Alice          Relais de Bob        Passerelle push        Google/Apple        Bob (appli fermée)
   │ message         │                      │                    │                  │
   ├────────────────►│ Bob est absent : il  │                    │                  │
   │                 │ note « en attente »  │                    │                  │
   │                 │ et possède un jeton  │                    │                  │
   │                 │ de Bob ─────────────►│  push « réveille » │                  │
   │                 │                      ├───────────────────►│ ────────────────►│ le système
   │                 │                      │                    │                  │ réveille l'appli
   │                 │◄─────────────────────────────── Bob se connecte et se déclare prêt
   │◄── « prêt » ────┤
   │ renvoie sa file │
   ├────────────────►│ ───────────────────────────────────────────────────────────► │ déchiffre,
   │                 │                                                               │ notifie
```

La notification ne transporte aucun message. Sa charge utile est la même à chaque fois, sans expéditeur, sans aperçu et sans identité, de sorte que Google et Apple voient seulement un ordre de réveil. L'application est réveillée sans intervention de l'utilisateur, par un message de données à priorité haute. Elle ouvre une session de quelques dizaines de secondes, se connecte à ses serveurs et se déclare prête.

Le relais prévient alors Alice que Bob est prêt. Si Alice a aussi fermé son application, son téléphone est réveillé de la même manière. Alice renvoie sa file d'envoi, Bob déchiffre le message et affiche une notification locale. Le nom de l'expéditeur et l'aperçu sont produits sur l'appareil et ne passent par aucun service de Google ou d'Apple.

Seule la passerelle détient les identifiants FCM et APNs. Un relais lui est lié par des clés, avec l'accord des deux administrateurs, et une passerelle sans relais lié refuse toute demande. Quand il n'y a pas de notification push (jeton refusé, téléphone qui bloque l'application, aucune passerelle liée), l'application interroge ses serveurs environ toutes les 15 minutes avec le planificateur du système (WorkManager sous Android). Si Alice éteint son téléphone juste après l'envoi, le message attend son retour.

Le client de bureau n'utilise pas ce mécanisme. Il reste connecté tant que le programme tourne, et fermer la fenêtre le réduit dans la zone de notification.

## 6. Quand la connexion est coupée

WebSocket transporte des messages entiers. Chaque trame indique sa longueur et si elle est la dernière du message, il n'y a donc pas de caractère de fin à chercher dans le JSON. L'application et le relais assemblent les trames d'un message et ne décodent le JSON qu'une fois le message complet (§ 11).

Si la connexion se coupe pendant une réception, la partie déjà reçue est abandonnée et aucune application ne voit de message tronqué. Un message dont la réception n'a pas été confirmée reste dans la file d'envoi de l'expéditeur et repart (§ 2, point 3). L'application se reconnecte toutes les 30 secondes tant que le serveur ne répond pas, et laisse de côté pendant 30 secondes un serveur injoignable pour que chaque envoi n'attende pas la fin d'un délai. Un relais qui reçoit un message plus gros que sa limite ferme la connexion (code 1009) au lieu de saturer sa mémoire.

## 7. Fichiers, groupes, plusieurs appareils

Le relais ne stocke pas les fichiers. Un fichier est annoncé dans la conversation, le destinataire le demande quand il le souhaite, et l'expéditeur l'envoie par morceaux de 48 Kio, chacun vérifié avec un arbre de Merkle, tant que les deux sont en ligne. Dans un groupe, les membres qui possèdent déjà le fichier le fournissent aux autres (§ 6).

Un groupe n'existe sur aucun serveur. C'est un état signé, avec une clé partagée qui change à chaque modification des membres ou des rôles, et les membres se l'échangent comme des messages. Un membre resté hors ligne récupère ce qu'il a manqué auprès des autres membres (§ 7).

Une même identité peut fonctionner sur plusieurs appareils connectés en même temps. Un message envoyé depuis l'un est copié sur les autres. Un appareil restauré peut demander l'historique à un autre, avec un code de contrôle à six chiffres (§ 1).

## 8. Première connexion et choix du serveur

Une application qui crée une identité n'a aucun serveur. Elle prend ses premiers serveurs dans cet ordre (§ 4) :

1. Le serveur choisi par l'utilisateur en mode avancé, qui est alors conservé.
2. Sinon, les relais proposés par l'un des annuaires intégrés à l'application, parmi ceux qui acceptent des comptes et ont de la place, en commençant par le pays de l'appareil. Les annuaires sont interrogés dans un ordre aléatoire, une seule fois.
3. Si aucun annuaire ne répond, un des relais de repli intégrés, tiré au hasard.

Ces serveurs initiaux peuvent changer une fois. Une sauvegarde restaurée qui liste des serveurs joignables les adopte, et le tout premier contact aussi quand il est sur d'autres serveurs joignables, de façon que les deux soient sur le même relais.

Une application qui dispose d'un relais ne contacte pas d'annuaire. La bibliothèque cliente sait chercher un relais quand aucun de ceux de l'identité ne répond, d'abord parmi ceux des contacts puis dans les annuaires, mais aucune application ne l'appelle automatiquement.

## 9. Ce que chaque composant connaît

| | Relais | Passerelle push | Annuaire | Google / Apple |
|---|---|---|---|---|
| Contenu des messages | non | non | non | non |
| Qui écrit à qui, et quand | oui (enveloppes et marqueurs d'attente) | non | non | non |
| Adresse IP des appareils | oui, pendant la connexion (aucun journal d'accès dans l'exemple nginx) | celle des relais seulement | aucune conservée | celle du téléphone |
| Jeton de réveil | oui | oui | non | oui |
| Identité de l'utilisateur | un identifiant calculé, sans nom ni numéro | non | non | non |

Un relais privé ne répond qu'aux comptes que son administrateur a autorisés et à ses voisins liés. Sa réponse à une adresse inconnue ne se distingue pas de celle d'une adresse qui n'existe pas (§ 10).

## 10. Liens entre serveurs

Les liens entre serveurs (relais et relais, relais et passerelle push, relais et annuaire) reposent sur une paire de clés par serveur et sur l'accord des deux administrateurs, donné avec les commandes `add`, `requests` et `accept`. Aucun secret n'est partagé et chaque appel entre serveurs est signé. Une demande indique le nom, l'adresse et le contact que son auteur déclare, et l'administrateur vérifie l'empreinte de la clé par un autre moyen avant d'accepter (§ 10, « Liaison entre serveurs par consentement mutuel »).
