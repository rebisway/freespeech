# Fonctionnalités des applications

Ce document liste ce que font les applications mobile (Android) et de bureau (Windows, Linux ; le code est aussi prévu pour macOS). Les deux partagent la même bibliothèque (`freespeech.client`) : les messages, les groupes, les fichiers et la sécurité sont donc les mêmes ; seule l'interface et ce que permet l'appareil changent. Le fonctionnement des serveurs est dans [HOW_IT_WORKS.fr.md](HOW_IT_WORKS.fr.md), un message suivi dans tous les cas dans [MESSAGE_FLOWS.fr.md](MESSAGE_FLOWS.fr.md), les formats dans [PROTOCOL.fr.md](PROTOCOL.fr.md).

## Plates-formes

| | Mobile | Bureau |
|---|---|---|
| Système | Android 7 et plus (APK, Google Play) | Windows (installateur MSI, par utilisateur, sans droits d'administrateur) · Linux (AppImage) |
| iOS, macOS | iOS : non publié · macOS : même code, pas de paquet | |
| Lancement | une instance | un profil par nom : `FreeSpeech.exe --profile nom --server url` |
| Affichage | un écran à la fois | deux colonnes (liste, conversation) ; une seule colonne sous 700 px |
| Langues | allemand, anglais, espagnol, français, italien, néerlandais, polonais, portugais, russe, turc, plus toute langue importée | les mêmes |

## Identité et démarrage

| Fonction | Mobile | Bureau |
|---|---|---|
| Première connexion : un prénom, trois phrases tirées d'un livre du domaine public dans la langue du système | oui | oui |
| Aucun compte, ni numéro de téléphone, ni adresse de courriel | oui | oui |
| Écran « Sauvegardez vos trois phrases » (afficher, copier, imprimer une feuille, fichier de clé `.fskey` protégé par mot de passe) | oui | oui |
| Restaurer son identité : trois phrases saisies, collées, lues sur la feuille imprimée ou dans le fichier de clé ; avec ou sans fichier de sauvegarde | oui (lecture de la feuille par la caméra) | oui (fichier de clé, photo de la feuille) |
| Vérifier qu'un fichier de clé ouvre bien son identité | oui | oui |
| Choisir son serveur dès le départ (mode avancé) | oui | oui |
| Serveur trouvé tout seul par les annuaires intégrés, sinon un relais de repli | oui | oui |

## Messages

| Fonction | Mobile | Bureau |
|---|---|---|
| Texte, réponse citée, transfert vers une autre discussion | oui | oui |
| Mise en forme (titres, listes, gras, italique, barré, code, citation, liens) quand la première ligne est `md` | oui | oui |
| Réactions par emoji | oui | oui |
| Modifier un message | oui | oui |
| Supprimer chez soi / pour tous (le contenu est effacé chez tous) | oui | oui |
| Copier, partager le texte | oui | oui (copie) |
| Recherche dans une conversation | oui | oui |
| Séparateur « nouveaux messages », chargement des messages précédents en remontant | oui | oui |
| Épingler un message (modérateurs, 3 au plus) | oui | oui |
| Mettre une discussion en sourdine | oui | oui |
| Accusé de lecture (✓ envoyé, ✓✓ dorée lu), désactivable | oui | oui |
| Indicateur « écrit… », désactivable | oui | oui |
| Les liens s'ouvrent après confirmation montrant l'adresse réelle | oui | oui |
| Écran « Envois en cours » : messages en attente, « Ne plus attendre », reprendre, nombre de renvois | oui | oui |
| Message vocal (enregistrement et lecture) | oui | non |

## Fichiers et médias

| Fonction | Mobile | Bureau |
|---|---|---|
| Envoyer un fichier jusqu'à 2 Gio, avec confirmation au-delà d'une taille réglable | oui | oui |
| Un fichier reçu affiche sa taille ; téléchargement à la demande ou automatique jusqu'à une taille réglable | oui | oui |
| Téléchargement par wifi seulement, ou données mobiles, ou petits fichiers seulement | oui | non |
| Vignette des images et des vidéos | oui | oui (images) |
| Visionneuse d'images | oui | non |
| Lecture d'une vidéo | oui | non |
| Album (plusieurs images en mosaïque) | oui | non |
| Prendre une photo, filmer, avec compression d'une vidéo avant l'envoi | oui | non |
| Recevoir du contenu partagé depuis une autre application | oui | non |
| Retirer un fichier envoyé (pour tous) | oui | oui |
| Fichier repris en cas de coupure, plusieurs détenteurs dans un groupe | oui | oui |
| Archiver les fichiers selon leur date, leur type ou leur taille pour libérer de la place, puis les recharger | oui | non |

## Contacts

| Fonction | Mobile | Bureau |
|---|---|---|
| Ajouter par QR code montré ou scanné | oui (montrer et scanner) | oui (montrer) |
| Ajouter par fiche à usage unique : copier, coller, envoyer par SMS, courriel ou messagerie | oui | oui (copier, coller, courriel) |
| Demandes en attente : accepter ou refuser | oui | oui |
| Renommer un contact (nom que vous seul voyez) | oui | oui |
| Vérifier le numéro de sécurité (✔ doré dans les listes) | oui | oui |
| Supprimer un contact (la discussion reste en lecture dans les archives) | oui | oui |
| Photo de profil, nom, description : modifier et envoyer aux contacts et aux groupes | oui | oui |

## Groupes

| Fonction | Mobile | Bureau |
|---|---|---|
| Créer un groupe en choisissant des contacts, nommer, décrire, mettre une image | oui | oui |
| Invitation à accepter ou refuser, renvoyer ou annuler une invitation | oui | oui |
| Rôles : créateur, modérateurs ; nommer, retirer, céder le rôle de créateur | oui | oui |
| Exclure, exclure et bannir, débannir (le créateur) | oui | oui |
| Proposer un de ses contacts aux modérateurs | oui | oui |
| Mon nom et ma photo dans ce groupe | oui | oui |
| Quitter ; groupe archivé en lecture seule ; ré-invitation | oui | oui |
| Rattrapage des messages manqués auprès des autres membres | oui | oui |

## Serveurs

| Fonction | Mobile | Bureau |
|---|---|---|
| Plusieurs serveurs en même temps, publics et privés ; état connecté / hors ligne de chacun | oui | oui |
| Ajouter ou retirer un serveur | oui | oui |
| Serveur privé : utiliser une invitation, ou demander l'accès (avec un code de vérification à donner à l'administrateur) | oui | oui |
| Le serveur peut conseiller un déplacement quand il est plein (3 fois par jour au plus) | oui | oui |
| Bandeau « Limite du serveur » quand le volume quotidien est atteint | oui | oui |
| Serveur restreint commun avec un contact : seul chemin utilisé par défaut ; réglage par contact « Si le serveur privé ne répond pas » (attendre, me demander, utiliser un serveur ouvert) ; « Envoyer par un serveur ouvert » dans « Envois en cours » | oui | oui |

## Notifications et réveil

| Fonction | Mobile | Bureau |
|---|---|---|
| Notification du système avec expéditeur et aperçu produits sur l'appareil | oui | non |
| Réveil de l'application fermée par un push sans contenu (réglable) | oui | non (reste connecté tant qu'il tourne) |
| Interrogation périodique (environ 15 min) si le push est absent ou refusé | oui | non |
| Vérification de la fiabilité des notifications (économiseur de batterie du constructeur) | oui | non |
| Aperçu masqué sur l'écran verrouillé | oui | non |
| Popup propre à l'application, coin bas droit, avec champ de réponse | non | oui |
| Icône de la zone de notification : 12 dernières discussions, Ouvrir, Réglages, Quitter ; fermer la fenêtre la masque | non | oui |

## Sécurité et confidentialité

| Fonction | Mobile | Bureau |
|---|---|---|
| Base locale chiffrée | oui (clé dans le stockage sécurisé du téléphone) | oui (clé protégée par DPAPI sous Windows ; sinon trois phrases à chaque démarrage) |
| Verrouillage par code de 4 à 8 chiffres, délai réglable (tout de suite à une semaine) | oui | oui |
| Effacement des données après un nombre de codes faux (5 ou 10) | oui | oui |
| Code oublié : retour par les trois phrases | oui | oui |
| Écran protégé (ni capture, ni aperçu dans les applications récentes) | oui | non |
| Clavier discret | oui | non |
| Effacement à distance d'un téléphone volé (activé avec les trois phrases) | oui | oui |
| Changer d'identité après un vol (avis aux contacts, passation des groupes) | oui | oui |

## Sauvegarde et plusieurs appareils

| Fonction | Mobile | Bureau |
|---|---|---|
| Sauvegarde chiffrée (sans pièces jointes, 6 derniers mois, ou toutes), enregistrée où l'on veut | oui | oui |
| Restaurer : contacts, groupes, historique, serveurs, profil | oui | oui |
| Même identité sur plusieurs appareils en même temps | oui | oui |
| Copier l'historique d'un autre appareil de l'identité (code de contrôle à six chiffres) | oui | oui |

## Réglages et apparence

| Fonction | Mobile | Bureau |
|---|---|---|
| Thème sombre, clair, ou celui du système | oui (au redémarrage) | oui (tout de suite) |
| Choix de la langue, import d'un fichier de langue (JSON), modèle à traduire, retrait | oui | oui |
| Journal de diagnostic à partager | oui | oui |
| Version et date affichées | oui | oui |
