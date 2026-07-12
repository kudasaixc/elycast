<p align="center">
  <img src="docs/images/elycast-logo2.png" alt="ElyCast — lecteur IPTV et multimédia pour Windows" width="100%">
</p>

<p align="center">
  <strong>La télé en direct, les films, les séries et vos fichiers personnels dans un seul lecteur Windows.</strong>
</p>

<p align="center">
  ElyCast se connecte à un service IPTV ou à une playlist M3U, organise vos contenus et exploite le GPU pour une image plus nette, des mouvements plus fluides et un son réglable en direct.
</p>

<p align="center">
  <a href="https://github.com/kudasaixc/elycast/actions/workflows/build.yml"><img alt="Build" src="https://github.com/kudasaixc/elycast/actions/workflows/build.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/kudasaixc/elycast/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/kudasaixc/elycast/actions/workflows/codeql.yml/badge.svg?branch=main"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast"><img alt="Quality Gate Status" src="https://sonarcloud.io/api/project_badges/measure?project=kudasaixc_elycast&metric=alert_status"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast"><img alt="Security Rating" src="https://sonarcloud.io/api/project_badges/measure?project=kudasaixc_elycast&metric=security_rating"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast"><img alt="Maintainability Rating" src="https://sonarcloud.io/api/project_badges/measure?project=kudasaixc_elycast&metric=sqale_rating"></a>
</p>

<p align="center">
  <img alt="Windows x64" src="https://img.shields.io/badge/Windows-x64-0078D4?logo=windows11&logoColor=white">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="ElyCast 1.3" src="https://img.shields.io/badge/ElyCast-1.3-27c4e8">
  <a href="LICENSE"><img alt="License MPL-2.0" src="https://img.shields.io/github/license/kudasaixc/elycast?color=2ea44f"></a>
  <a href="https://github.com/kudasaixc/elycast"><img alt="Open source" src="https://img.shields.io/badge/Open%20Source-GitHub-181717?logo=github"></a>
</p>

---

## Aperçu

ElyCast est un lecteur multimédia Windows qui réunit trois usages dans une seule interface : la **télévision en direct** (IPTV), la **VOD** (films et séries) et vos **fichiers locaux** (musique et vidéos). Il s'appuie sur libmpv pour la lecture et sur un renderer natif D3D11 pour tirer parti du GPU — mise à l'échelle, interpolation d'images, shaders et traitement du son, le tout pilotable à chaud.

Le premier lancement configure la machine tout seul : détection CPU/GPU, téléchargement des dépendances, benchmark et choix du moteur adapté à vos contenus. Chaque technologie GPU est optionnelle et retombe automatiquement sur une solution compatible si elle est indisponible — vous n'avez jamais à choisir le « bon » moteur avant une lecture.

> ElyCast ne fournit aucun abonnement, chaîne ni contenu. Vous êtes responsable d'utiliser des services et des médias auxquels vous avez légalement accès.

## Fonctionnalités

### Sources et bibliothèques
- **IPTV** via Xtream Codes ou playlist M3U, direct classé par catégories, EPG.
- **VOD** : films, séries, saisons et épisodes dans la même navigation.
- **Vidéothèque locale** : import récursif de dossiers, lecture via mpv ou VLC.
- **Bibliothèque musicale** : import récursif, tri automatique par artiste / album / genre à partir des tags, pochettes, playlists, file d'attente, lecture aléatoire et répétition.
- **Favoris, reprise de lecture, catégories et recherche** transverses.
- **Éditeur de métadonnées** : titre, artiste, album, genre, numéro de piste et pochette écrits directement dans les fichiers.
- Pistes audio multiples et sous-titres sélectionnables.
- **Contrôles multimédias Windows** (SMTC) pour l'audio local uniquement : titre, artiste, album et pochette. Le direct et les vidéos ne créent jamais de session système.

### Image
- **ELYCORE** — renderer natif : libmpv → OpenGL → texture D3D11 partagée (sans lecture CPU) → présentation DXGI.
- **RTX Video Super Resolution** (GPU NVIDIA RTX) pour gagner en netteté et en définition.
- **ELYFLOW** — interpolation d'images via NVIDIA Optical Flow (FRUC) pour des mouvements plus fluides.
- **ELYCOLOR** — profils d'image : couleurs, contraste, gamma, corrections et chaînes de shaders applicables pendant la lecture.
- Support des shaders GLSL et de Magpie pour l'upscaling.

### Son
- **ELYSOUND+** — graphe audio libmpv stable, piloté à chaud sans seek ni rechargement : égaliseur en dB réels, préamplification, compression douce, limiteur anti-clipping et largeur stéréo.

### Visualiseur audio
- Visualiseur FFT temps réel : barres, particules, ondes réactives aux basses et au rythme, palette extraite de la pochette, fonds animés, flou et assombrissement réglables.
- **AudioCore+** — la même scène rendue en D3D11 sur le GPU (via ELYCORE) : cadence décorrélée de l'affichage (jusqu'à 360 FPS visés), VSync optionnel. Le renderer WPF classique et AudioCore+ partagent **exactement** la même simulation, les mêmes couleurs et les mêmes réglages ; bascule automatique vers le WPF si le pipeline natif est indisponible.

### Assistance & supervision
- **Onboarding** au premier lancement : profil d'usage, détection matérielle, benchmark, recommandation de moteur et tests de compatibilité.
- **ELYSMART** : mesure les capacités réelles de la machine, explique ses recommandations, surveille la santé du lecteur (historique, diagnostic exportable, notifications non intrusives) et distingue les baisses durables des pics ponctuels.

## Backends vidéo

Vous choisissez un backend ; ElyCast bascule seul vers une alternative compatible si la technologie demandée n'est pas disponible.

| Backend | Fonctionnement | Idéal pour |
| --- | --- | --- |
| **ELYCORE** | libmpv + OpenGL/D3D11 + VSR/FRUC + DXGI | Le meilleur pipeline NVIDIA disponible |
| **RTX SDK** | mpv `gpu-next` + traitement vidéo D3D11 NVIDIA | Profiter de RTX VSR sans ELYCORE |
| **mpv GPU** | mpv `gpu-next`, décodage matériel et scalers avancés | La lecture GPU générale |
| **VLC** | Décodage LibVLC vers une surface WPF | Le fallback de compatibilité |

```text
libmpv
  → rendu OpenGL
  → texture D3D11 partagée sans lecture CPU
  → RTX Video Super Resolution (optionnel)
  → NVIDIA FRUC / ELYFLOW (optionnel)
  → présentation DXGI dans le player
```

## Formats

- **Vidéo** : tout ce que gèrent mpv ou VLC (MKV, MP4, TS, HLS, etc.).
- **Audio** : MP3, FLAC, WAV, AAC, M4A, OGG, Opus, WMA, ALAC, AIFF, APE.

## Installation

Téléchargez la dernière version depuis la page [Releases](https://github.com/kudasaixc/elycast/releases), décompressez l'archive et lancez `ElyCast.exe`.

**Prérequis :**
- Windows 10 / 11 en 64 bits.
- [.NET Desktop Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0) (les archives « framework-dependent » ne l'embarquent pas).
- 7-Zip (utilisé par ElyCast pour installer libmpv au premier lancement).
- Un GPU NVIDIA RTX avec pilote récent pour RTX VSR et ELYFLOW/FRUC.

Les dépendances téléchargées par l'application (libmpv, shaders, Magpie) sont installées dans `%APPDATA%\ElyCast\tools`, jamais dans le dépôt.

## Compiler depuis les sources

Le composant natif se compile avant l'application WPF :

```powershell
cmake -S native/ElyFlow.Native -B native/ElyFlow.Native/build -A x64
cmake --build native/ElyFlow.Native/build --config Release
dotnet restore "ElyCast TV Player.csproj"
dotnet build "ElyCast TV Player.csproj" -c Release -p:Platform=x64
```

Ou via le script fourni :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release
```

**Prérequis de build :**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio avec **Desktop development with C++** et CMake
- NVIDIA Optical Flow SDK 5.0.7 + runtime NvOFFRUC pour activer FRUC (optionnel)

Sans le SDK NVIDIA, ELYCORE se compile et fonctionne, mais son adaptateur FRUC reste indisponible. Pour activer FRUC, placez le SDK de façon à obtenir ce chemin avant la configuration CMake :

```text
native/NVIDIA-Optical-Flow-SDK-5.0.7/
  Optical_Flow_SDK_5.0.7/NvOFFRUC/Interface/NvOFFRUC.h
```

Le SDK NVIDIA et ses DLL propriétaires ne sont volontairement pas distribués dans ce dépôt.

## Architecture du dépôt

```text
App.xaml(.cs)                 Démarrage, ressources et styles globaux
MainWindow.xaml               Shell visuel principal et surfaces du player
MainWindow.*.cs               Coordination par domaine : catalogue, playback, settings, OSD, fonctions ELY
Models/                       Réglages, profils et modèles multimédias
Services/Audio/               Métadonnées, analyse FFT, pochettes et ELYSOUND+
Services/ElySmart/            Benchmark, scoring, recommandations et supervision runtime
Services/                     IPTV, état, thème, console et services Windows
Services/Video/               Backends mpv/VLC, HWND, shaders et interop natif
native/ElyFlow.Native/        Renderer ELYCORE C++20, scène AudioCore+ et adaptateur NVIDIA FRUC
Assets/                       Ressources visuelles de l'application
scripts/                      Commandes de build reproductibles
tests/                        Régressions audio/playback et probe RTX VSR
AGENTS.md                     Carte technique complète et invariants du projet
```

La documentation technique détaillée est dans [`AGENTS.md`](AGENTS.md) : flux de lecture, responsabilités des fichiers, threading, airspace WPF/Win32, renderer natif et règles de modification sûres.

## Qualité et sécurité

- Compilation Windows x64 vérifiée à chaque push et pull request.
- Analyse statique CodeQL sur le C# et le C++.
- Quality Gate, sécurité et maintenabilité suivies sur [SonarQube Cloud](https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast).
- Profils et état local protégés par Windows DPAPI, écritures de configuration atomiques.
- Aucun SDK propriétaire, média, playlist, profil ou binaire versionné.
- Les logs évitent volontairement les URL de lecture complètes (elles peuvent contenir des identifiants).

## Données locales et confidentialité

Profils, favoris, réglages et éléments de bibliothèque restent sous `%APPDATA%\ElyCast`. Les profils et l'état sérialisé sont chiffrés par Windows DPAPI pour l'utilisateur courant. Le `.gitignore` exclut playlists M3U, médias, journaux, secrets, certificats, SDK NVIDIA, outils téléchargés et sorties de compilation.

## Contribuer

1. Lisez [`AGENTS.md`](AGENTS.md) avant une modification importante.
2. Conservez les fallbacks quand mpv, NVIDIA, FRUC, les shaders ou Magpie sont absents.
3. Compilez le renderer natif puis l'application en Release x64.
4. Pour le renderer ou l'interface, testez aussi le redimensionnement, le plein écran, le HUD et les changements de focus.
5. Ne publiez jamais d'identifiants IPTV ni d'URL de flux complètes.

## Licence

ElyCast est distribué sous [Mozilla Public License 2.0](LICENSE). Les bibliothèques, SDK et runtimes tiers restent soumis à leurs licences respectives.

<p align="center">
  <strong>ElyCast — vos contenus, votre matériel, votre expérience.</strong>
</p>
