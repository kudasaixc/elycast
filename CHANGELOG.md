# Changelog

Toutes les modifications notables d'ElyCast sont consignées ici. Le format suit
[Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le versionnage
[SemVer](https://semver.org/lang/fr/).

## [1.3.0-canary] — 2026-07-12

### Ajouté
- **Importer des fichiers** : en plus de l'import de dossier, un nouveau bouton
  « Importer des fichiers » (côtés Musique et Vidéo) ouvre l'explorateur en
  sélection multiple, avec un filtre adapté au type de média de la section.
- **Glisser-déposer** : on peut désormais déposer un ou plusieurs sons/vidéos —
  et même des dossiers — directement dans la liste de gauche. Chaque fichier est
  routé automatiquement vers la bonne bibliothèque selon son extension, si bien
  qu'un même dépôt peut mêler musique et vidéo. Les fichiers non reconnus sont
  ignorés avec un message plutôt qu'une erreur.

### Interne
- Mutualisation du moteur d'import parallèle entre dossier et fichiers
  (`LocalLibraryService.BuildItems`, `ImportFilesAsync`).

## [1.3.0] — 2026-07-12

### Ajouté
- **AudioCore+** : nouvelle scène de visualiseur audio rendue nativement en
  D3D11 via ELYCORE (fond, flou gaussien séparable, dimmer, zoom/pan/parallaxe,
  barres, particules et ondes rastérisées en shaders). Sélectionnable face au
  renderer WPF classique, avec bascule automatique vers le WPF si le pipeline
  natif est indisponible.
- Le renderer classique WPF et AudioCore+ partagent une **simulation unique** :
  positions, couleurs quantifiées, épaisseurs et réglages sont identiques, ce
  qui garantit une parité visuelle exacte entre les deux.

### Modifié
- **Cadence du visualiseur décorrélée de l'affichage** : la scène AudioCore+ se
  rythme elle-même jusqu'à la cible FPS configurée (30–360) au lieu d'être
  plafonnée au rafraîchissement de l'écran par le compositeur WPF.
- Le rendu natif snapshote l'état poussé sous un verrou court puis exécute tout
  le travail GPU sans verrou : l'envoi des primitives depuis le thread UI ne
  bloque plus sur la swapchain.
- FRUC et RTX VSR sont automatiquement contournés pendant la scène audio.

### Corrigé
- **Parallaxe du fond fluide** : la parallaxe ne se réinitialise plus par
  à-coups. Le pointeur est lu via `GetCursorPos` + `PointFromScreen` (fiable
  même sous la fenêtre native) au lieu de `IsMouseOver`, et l'offset est lissé
  dans le temps (indépendamment du frame-rate).
- **Cadrage du fond** corrigé : suppression d'une marge interne qui sur-zoomait
  l'image d'environ 10 % et décalait l'application du zoom/pan.
- La console de débogage se dégrade proprement quand l'allocation de console
  échoue au lieu de faire échouer le démarrage.

### Interne
- Refactor RAII de `SystemMediaTransport` (règle des cinq, `unique_ptr` à la
  place des `new`/`delete` explicites) et passage des opérations atomiques du
  renderer en `seq_cst`.

## [1.2.0]

### Ajouté
- Espace **Musique locale** repensé : navigation par albums, artistes, genres
  et playlists sous forme de groupes avec pochettes, panneau de détail et
  lecteur contextuel.
- **Éditeur de métadonnées** écrivant les tags (titre, artiste, album, genre,
  numéro de piste, pochette) directement dans les fichiers.
- **AudioCore+** (fondations) : sélection du renderer natif D3D11 pour le
  visualiseur audio, gating ELYCORE et contournement FRUC pour l'audio.

### Modifié
- Import audio non bloquant avec enrichissement des métadonnées en arrière-plan
  et fusion par chemin dans la liste vivante.
- Regroupement des artistes robuste (séparation par point-virgule) pour ne plus
  fragmenter les collaborations.

## [1.1]

### Ajouté
- **ELYSMART** : détection matérielle, benchmark, recommandations expliquées et
  supervision de la santé du lecteur avec historique et diagnostic exportable.
- **Onboarding** au premier lancement (profil, détection, benchmark, choix du
  moteur, tests de compatibilité).
- Lecteur audio repensé : visualiseur FFT temps réel, particules, palettes
  extraites de la pochette, fonds animés, VSync et cibles jusqu'à 360 FPS.
- Bibliothèques locales audio et vidéo séparées ; contrôles multimédias Windows
  pour l'audio local uniquement.
- Identité applicative : exécutable `ElyCast.exe`, icône, AppUserModelID et
  raccourci Shell.

[1.3.0]: https://github.com/kudasaixc/elycast/releases/tag/v1.3.0
[1.2.0]: https://github.com/kudasaixc/elycast/releases/tag/v1.2.0
[1.1]: https://github.com/kudasaixc/elycast/releases/tag/v1.1
