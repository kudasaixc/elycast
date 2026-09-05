# Audit ElyCast · GPT 6 ASTRA

Date : 5 septembre 2026. Périmètre : application WPF, connexion IPTV, catalogues, bibliothèque locale, files audio, persistance, transitions de lecture, réglages et validation automatisée.

Ce rapport décrit les changements réalisés dans le dépôt. Les niveaux de gravité expriment l'impact du scénario identifié dans le code ; ils ne constituent pas une mesure de fréquence chez les utilisateurs. La validation matérielle NVIDIA complète est distinguée des tests C# et des observations de l'interface.

## 10 améliorations concrètes

| # | Parcours utilisateur | Résultat |
|---|---|---|
| 1 | Retrouver une chanson sans connaître ses accents | La recherche ignore les accents et la casse. |
| 2 | Chercher un titre et un artiste en une saisie | Chaque mot peut correspondre à un champ différent ; les espaces superflus sont ignorés. |
| 3 | Accéder rapidement à la recherche | Ctrl+F sélectionne le champ du catalogue ou celui du groupe musical ouvert. |
| 4 | Continuer à naviguer pendant un import | L'import ne ramène plus automatiquement vers sa section à la fin. |
| 5 | Utiliser « Suivant » après avoir ajouté un morceau à la file | La file manuelle est prioritaire, comme à la fin naturelle d'un morceau. Les fichiers absents sont ignorés. |
| 6 | Créer une playlist portant déjà un nom existant | Un message clair évite les doublons ambigus et sélectionne le nom à corriger. |
| 7 | Ajouter plusieurs morceaux déjà partiellement en file | Le message indique le nombre réellement ajouté. |
| 8 | Retirer un morceau d'une playlist filtrée | La recherche reste active et les numéros sont recalculés. |
| 9 | Changer de moteur pendant une pause | La position et l'état de pause sont restaurés dès que le nouveau média est prêt. |
| 10 | S'orienter dans la navigation et les paramètres | « Settings » remplace « Controls » ; « Sign out » décrit la déconnexion. Les explications vidéo parlent du résultat et du coût pour le PC. |

Autres améliorations : albums homonymes séparés par artiste d'album, refus explicite d'un fichier local absent avant de remplacer la lecture, suppression de la connexion automatique vers un profil supprimé, détails de série en erreur avec une indication de nouvelle tentative, libellés EPG et file traduits.

## 5 améliorations transversales

1. **Actions cohérentes.** Entrée dans la liste suit le même parcours que le double-clic ; ouvrir une série présente ses épisodes ; « Suivant » respecte la file.
2. **Intention conservée.** Les opérations qui se terminent tard ne doivent ni modifier un autre compte, ni revenir sur une navigation, ni déplacer une nouvelle lecture.
3. **Erreurs récupérables.** Une mauvaise ligne EPG n'efface plus le programme entier ; une sauvegarde endommagée peut retrouver sa dernière copie valide.
4. **Langage plus direct.** Les paramètres n'affichent plus le paragraphe promotionnel « ingénieur de performance », les détails de passes GPU ni la liste de fonctions promises pour plus tard.
5. **Saisie respectée.** Les champs de texte gardent leurs espaces et flèches. Les identifiants de connexion ne peuvent plus être modifiés pendant la requête en cours.

## 5 changements de fond

1. **Acquisition mieux séparée.** `PlaylistParser` lit le M3U avec annulation et résolution relative. `FlexibleStringConverter` adapte les identifiants Xtream numériques ou textuels. Les connexions ne publient leur service qu'après succès.
2. **Persistance commune.** `ProtectedJsonStore<T>` remplace deux implémentations dupliquées : DPAPI, remplacement atomique, sauvegarde `.bak`, récupération et protection contre l'écrasement après une lecture impossible. Les sauvegardes issues d'une migration JSON sont également chiffrées.
3. **Identité uniforme des médias.** `PlayItem.IdentityKey` aligne favoris et comparaisons : chemin Windows sans distinction de casse pour le local, adresse réelle pour les chaînes M3U.
4. **Travail coûteux hors du fil de l'interface.** Une découverte de fichiers partagée dessert dossiers et glisser-déposer, avec annulation, exclusion des répertoires inaccessibles et points de jonction, et dédoublonnage. La recherche du catalogue est temporisée à 160 ms. Le nettoyage des retraits utilise un ensemble de chemins au lieu d'une recherche imbriquée.
5. **Frontière moteur/interface et validation.** Les événements des moteurs sont transmis sans attente synchrone du fil WPF et vérifiés à réception. Les remplacements sont regroupés ; les restaurations différées sont bornées et vérifient moteur/génération. Un nouveau programme de régression utilise le code de production, des réponses HTTP synthétiques et des fichiers temporaires ; il est ajouté à la CI.

## Bugs corrigés et preuves

« Test » signifie une assertion automatique exécutée contre le code de production. « Revue » signifie un scénario établi dans le code, dont la correction compile mais dont toute la combinaison de conditions n'a pas été reproduite en interaction réelle.

| # | Impact et scénario avant correction | Correction | Validation |
|---|---|---|---|
| B01 | Bloquant : caractères `/`, `?`, `#`, `%` dans les identifiants Xtream cassent les URL de lecture. | Encodage indépendant des segments utilisateur/mot de passe. | Test URL exacte. |
| B02 | Bloquant : catégories Xtream dupliquées provoquent une exception et empêchent le chargement. | Construction tolérante de la table des catégories. | Test HTTP avec doublon. |
| B03 | Bloquant : un `category_id` numérique fait échouer la désérialisation du catalogue. | Convertisseur nombre/texte explicite. | Test HTTP numérique. |
| B04 | Bloquant : un identifiant d'épisode numérique empêche la récupération des épisodes. | Même conversion pour `Episode.Id`. | Test JSON. |
| B05 | Critique pour les données : une sauvegarde illisible est remplacée ensuite par les valeurs par défaut. | Récupération de `.bak` ; refus d'écriture si aucune copie n'est lisible. | Tests DPAPI, corruption et non-écrasement. |
| B06 | Critique pour les données : un profil ou média `null` fait échouer la normalisation de tout l'état. | Réparation des collections imbriquées avant utilisation. | Tests JSON contenant des valeurs nulles. |
| B07 | Bloquant : un gros dossier déposé est parcouru sur le fil WPF ; un sous-dossier inaccessible peut lever une exception avant le `try`. | Découverte asynchrone, filtrée et protégée, import unique et annulable. | Tests découverte/annulation ; revue du déplacement hors WPF. |
| B08 | Grave : deux connexions partagent des identifiants mutables, et le mode local peut hériter d'un ancien compte. | Service distinct par tentative, publication après succès et remise à zéro à la déconnexion. | Revue des séquences asynchrones. |
| B09 | Grave : le résultat d'un ancien catalogue est affecté avant le contrôle d'annulation, y compris après un changement de compte. | Résultat local puis vérifications avant publication ; ouverture des réglages annule aussi le chargement. | Revue des points d'attente. |
| B10 | Grave : un callback natif attend WPF pendant que WPF peut attendre la destruction du moteur ; un ancien callback peut également toucher le nouveau lecteur. | Envoi asynchrone avec identité du moteur et génération à réception. | Revue du cycle de vie ; compilation ; smoke de lecture. |
| B11 | Grave : une restauration différée peut déplacer une nouvelle lecture du même objet média. | Vérification de la génération et du moteur, délai borné, conservation de la pause. | Revue des scénarios de remplacement rapide. |
| B12 | Grave : réordonner un M3U peut déplacer les favoris vers une autre chaîne. | Identité fondée sur `DirectUrl`, utilisée aussi pour marquer les favoris. | Tests réordonnancement et remplacement d'URL. |
| B13 | Bloquant pour ces listes : les chemins relatifs d'un M3U sont remis au lecteur sans résolution. | Résolution contre le dossier ou l'URL de la playlist. | Tests local et distant. |
| B14 | Grave pour la saisie : les raccourcis du lecteur interceptent les touches d'autres champs que la recherche principale. | Protection de tous les champs éditables et des combinaisons de touches. | Revue du routage ; Ctrl+F observé dans l'interface. |
| B15 | Bloquant pour ces abonnements : un compte Xtream sans chaînes live ne peut pas accéder à ses films/séries. | Authentification explicite ; catalogue live vide autorisé après authentification. | Tests compte refusé et compte authentifié vide ; revue UI. |
| B16 | Fonctionnel : les M3U sans `EXTINF` disparaissent ; une virgule coupe le nom affiché. | Entrées simples acceptées et recherche du séparateur hors guillemets. | Tests M3U. |
| B17 | Fonctionnel : une mauvaise date EPG fait perdre tous les programmes valides ; les dates numériques échouent. | Validation par entrée, conversion tolérante et tri chronologique. | Test EPG mixte. |
| B18 | Fonctionnel : deux variantes de casse d'un chemin peuvent dupliquer un favori ou faire planter la résolution d'une playlist. | Identité locale uniforme et index tolérant les doublons. | Tests chemins et playlists. |
| B19 | Fonctionnel : suppression locale laisse des éléments « reprendre » pointant vers un média retiré et décale le curseur automatique. | Nettoyage des reprises de tous les profils, ajustement du curseur, arrêt des mises à jour obsolètes. | Revue du retrait groupé. |
| B20 | Fonctionnel : des albums de différents artistes portant le même titre sont fusionnés. | Clé album + artiste d'album. | Test albums homonymes. |

## Nettoyage des paramètres et du code

- Suppression du contrôle de durée de console dans les paramètres usuels ; conservation de `BootSeconds` pour lire les configurations existantes.
- Suppression du bloc « COMING SOON » et de sa liste de promesses.
- Réécriture des descriptions AudioCore+, moteur vidéo, netteté, ELYCOLOR, ELYFLOW et ELYSMART ; traduction française des nouveaux textes.
- RTX VSR est présenté comme un réglage indépendant de la fluidification.
- Suppression de l'ancien parseur M3U, des deux méthodes d'écriture atomique dupliquées, d'un ancien relais d'import inutilisé, du filtre de groupe remplacé et d'une sauvegarde redondante après retrait.
- Les moteurs de compatibilité et les champs persistés historiques utiles sont conservés. Le code natif GPU et l'ABI n'ont pas été modifiés.

## Validation

- Compilation gérée Release x64 : réussie, zéro avertissement et zéro erreur.
- Programme de régression existant : 10 groupes de contrôles réussis (terminaison de lecture, ELYSOUND+, localisation).
- Nouveau programme : 33 contrôles réussis, incluant DPAPI réel, migrations, sauvegardes corrompues, M3U, Xtream synthétique, recherche, identité et découverte locale.
- Inspection WPF : lecture d'un WAV silencieux de trois minutes, compteur de lecture observé en progression, réglages lisibles après leur animation, descriptions de lecture actualisées, Échap et Ctrl+F opérationnels.
- Les tests locaux utilisent des données synthétiques ; aucun identifiant, média personnel ou fichier d'état utilisateur n'est inclus dans ce rapport ou dans le dépôt.
- `scripts/build.ps1` signale désormais le prérequis C++ manquant et choisit explicitement le générateur Visual Studio disponible, au lieu de laisser CMake associer NMake et `-A x64`.

### Limites à conserver dans l'interprétation

Cette machine ne dispose pas de Visual Studio C++ Build Tools ni du SDK Windows. La compilation C# locale utilise la DLL native de la distribution ElyCast déjà présente sur la machine. Elle ne prouve pas une recompilation native. La CI Windows reste chargée de compiler le natif depuis les sources.

Le smoke local utilise VLC. Les combinaisons mpv/ELYCORE, VSR/FRUC, les changements rapides de moteur pendant une vraie lecture vidéo, le redimensionnement sous HWND et les fournisseurs IPTV réels doivent encore être éprouvés dans une campagne matérielle dédiée. La fermeture graphique de la session a rencontré une fenêtre minimisée et des interactions concurrentes ; le processus de test isolé a été arrêté avant la recompilation.

Aucun pourcentage de gain de FPS, RAM ou CPU n'est revendiqué : les optimisations d'algorithme et de réactivité ont été implémentées, mais aucun benchmark comparatif matériel avant/après n'a été réalisé. Cette intervention ne constitue pas un audit exhaustif de toutes les lignes de l'application.

## Reproduire les contrôles

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Release
dotnet run --project tests/ElyCast.Regression/ElyCast.Regression.csproj -c Release
dotnet run --project tests/ElyCast.CoreRegression/ElyCast.CoreRegression.csproj -c Release -p:Platform=x64
```

Pour un smoke isolé, fournir un WAV synthétique existant dans `ELYCAST_DIAGNOSTIC_FILE`, puis définir `ELYCAST_DIAGNOSTIC_CLEAN=1` et `ELYCAST_DIAGNOSTIC_BACKEND=vlc`. La session démarre en mode local, muette, sans charger les profils ni sauvegarder les préférences.
