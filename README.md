# MEGA for Jellyfin

Plays your [MEGA](https://mega.io) cloud drive in [Jellyfin](https://jellyfin.org), with nothing downloaded ahead.

- **End-to-end decryption on the fly.** Files stay encrypted on MEGA and are decrypted by your server while they play.
- **Virtual libraries.** Pick MEGA folders and the plugin creates the Jellyfin libraries for you. Jellyfin sees one tiny `.strm` file per video or song, so it identifies movies and shows, fetches artwork and tracks watch progress as usual.
- **Fast streaming.** Direct play, transcoding and seeking all work. Several connections fetch each file in parallel. The start and end of each file are cached on disk, so playback starts and scans run fast.
- **Fast thumbnails.** The previews MEGA made at upload time become item images at once. Online artwork (TMDb and others) still wins when Jellyfin finds it.
- **Follows your drive.** New, renamed, moved and deleted files show up within minutes. The folder tree is cached on disk, encrypted, so restarts are instant.
- **Account or public link.** Log in with e-mail and password (2FA supported), paste a MEGAcmd session, or add a public folder link. Incoming shares work too.

Supports **Jellyfin 12.x** and **Jellyfin 10.11**.

[Version française plus bas](#français).

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (or **Manage repositories**) and add this repository:
   - Name: `MEGA`
   - URL: `https://raw.githubusercontent.com/Sounne/jellyfin-plugin-mega/main/manifest.json`
2. Open **Catalog**, select **MEGA** and install it.
3. Restart Jellyfin.

## Set up

Open **Dashboard → Plugins → MEGA**.

1. **Add a source.** Choose one of these:
   - **Account (e-mail):** your e-mail, password and 2FA code if you use it. The password is used once to log in and is never stored. Only the session is kept.
     MEGA sometimes asks for a proof of work before it accepts a login (an anti-abuse check). The plugin computes it and the page shows the progress. This takes seconds on a recent CPU, but can take several minutes on a small NAS. If it takes too long, use a MEGAcmd session instead.
   - **Session (MEGAcmd):** the output of `mega-session`.
   - **Public folder link:** a `https://mega.nz/folder/…#…` link that includes its key.
2. **Add a library.** Choose the source, click **Browse** to pick a MEGA folder, give the library a name and a type (Movies, Shows, Music…), then click **Save**.

The library appears in Jellyfin after the first synchronization. With a large drive, the first load of the folder tree can take a minute or two. Later starts read it from the local cache.

For shows, point the library at a folder of shows. A folder that holds a single show (season folders or episodes) also works.

### Settings

| Setting | Default | What it does |
|---|---|---|
| Check MEGA for changes every | 10 min | How often the plugin asks MEGA for changes. It only downloads what changed. |
| Read media details after each scan | on | Reads duration, codecs and tracks of new items (a few MB per file). |
| Use MEGA previews | on | Uses MEGA's own previews as images. |
| Extract a frame when MEGA has no preview | on | Lets ffmpeg grab a frame instead (a few MB per file). |
| Read-ahead per stream | 48 MB | How far ahead of playback the plugin downloads. The window grows only during steady playback. |
| Parallel connections per stream | 4 | MEGA limits the speed of each connection, so several are used per file. |
| Disk cache | 2048 MB | Keeps the start and end of files for fast probing and playback start. |
| ffmpeg analysis of MEGA files (Advanced) | 10 s | How much of a MEGA file ffmpeg reads to describe it before playback, a transcode or a seek. Jellyfin's own setting (200 s) applies to your other files. 0 = use Jellyfin's setting for MEGA files too. |

A full resynchronization also runs once a day (**Dashboard → Scheduled tasks → MEGA**). The **Synchronize now** button on the plugin page runs one at once.

## How it works

- The plugin talks to the MEGA API directly. It decrypts the folder tree with your account keys, including shared folders and their keys.
- Each library is a folder of `.strm` files in Jellyfin's data directory, under `data/mega/libraries`. Each `.strm` holds a signed URL of this server (`/MegaFs/stream/…`). Small side files sit next to them: subtitles, `.nfo`, posters.
- When Jellyfin or a client reads a file, the plugin downloads only the requested byte ranges from MEGA and decrypts them with AES-CTR. Seeks jump straight to the right block.
- On playback requests, the plugin tells Jellyfin the item is a local file with its real size. Clients can then direct play with byte ranges, and Jellyfin's usual access control applies.
- Jellyfin runs ffprobe and ffmpeg with a 200-second analysis window. When a track has no full description in the file header (PGS subtitles in MKV, for example), ffmpeg then reads about 350 MB of a 1080p movie before it starts. For MEGA items only, the plugin shortens that window to 10 seconds: playback info, transcode start and seek take about a second instead of 10 to 25.
- When a client cannot take embedded subtitles with the video (a Chromecast or Android TV transcode, subtitles drawn by the web player, burned-in text), Jellyfin extracts them with ffmpeg, which reads the whole file: minutes and gigabytes of quota before the first line shows. For MEGA MKV files, the plugin reads only the subtitle blocks instead, located through the file's index (cues), and writes them to Jellyfin's subtitle cache: a few seconds and a few MB (an episode with 3 tracks: 6 s, 4 MB; a movie with 7 tracks: 11 s, 15 MB). This starts with the playback info, and subtitle requests wait for it. It is done once per file.

## Security

- The MEGA session is stored in the plugin configuration (`plugins/configurations/Jellyfin.Plugin.Mega.xml`), like any Jellyfin plugin setting. Anyone who can read your Jellyfin data folder can use it. Remove the source in the plugin page to revoke it there, and end the session in MEGA's settings (Security → Sessions).
- The internal stream URLs are signed with a server secret. By default only the server itself can use them.
- The cached folder tree is encrypted with a key derived from the session.

## Limitations

- **MEGA transfer quota.** Everything you watch counts against your MEGA transfer quota. That includes reads by Jellyfin: probing, frame extraction and subtitle extraction. Free accounts have a small quota.
- Jellyfin probes a `.strm` item again each time playback starts. This adds a fraction of a second, mostly served from the disk cache.
- Trickplay images and chapter images are not generated for these items, because they would read whole files.
- Embedded subtitles are read from the subtitle blocks only in MKV files whose index lists them, which mkvmerge and ffmpeg do. For other files (older MKVs, MP4, VobSub tracks), Jellyfin's extraction reads the whole file the first time a client needs them as a separate file. External subtitle files next to the video are always fast.
- Jellyfin applies no bitrate limit to these items. Clients on slow links should pick a quality by hand.
- A file that the plugin ignored because of its name (for example `film.mkv.part`) and that is later renamed to a media name shows up only after the daily full resynchronization.
- Not affiliated with MEGA or Jellyfin.

## Build

Needs the .NET 10 SDK.

```bash
dotnet test
pwsh scripts/package.ps1 -Version 1.0.1 -Changelog "What changed"
```

The script writes one zip per Jellyfin version to `artifacts/` and updates `manifest.json`. Publish the zips as release `v1.0.1`, then commit `manifest.json`:

```bash
gh release create v1.0.1 artifacts/*.zip --title 1.0.1 --notes "What changed"
```

The live tests run only when `MEGA_TOML` points to a file that contains `session = "…"` or `folder_link = "…"`.

## License

GPL-3.0. See [LICENSE](LICENSE).

---

## Français

Lit votre cloud [MEGA](https://mega.io) dans [Jellyfin](https://jellyfin.org), sans rien télécharger à l'avance.

- **Déchiffrement de bout en bout, à la volée.** Les fichiers restent chiffrés sur MEGA. Votre serveur les déchiffre pendant la lecture.
- **Bibliothèques virtuelles.** Choisissez des dossiers MEGA : le plugin crée les bibliothèques Jellyfin. Jellyfin voit un petit fichier `.strm` par vidéo ou morceau. Il identifie donc films et séries, récupère les visuels et suit la progression comme d'habitude.
- **Streaming rapide.** Lecture directe, transcodage et déplacement dans la vidéo fonctionnent. Plusieurs connexions lisent chaque fichier en parallèle. Le début et la fin des fichiers restent en cache disque : démarrage et analyse rapides.
- **Miniatures rapides.** Les aperçus créés par MEGA à l'envoi servent d'images tout de suite. Les visuels en ligne (TMDb…) restent prioritaires quand Jellyfin les trouve.
- **Suit votre cloud.** Fichiers ajoutés, renommés, déplacés ou supprimés : pris en compte en quelques minutes. L'arborescence est gardée en cache chiffré sur disque : redémarrage instantané.
- **Compte ou lien public.** Connexion par e-mail et mot de passe (2FA géré), session MEGAcmd ou lien de dossier public. Les partages entrants fonctionnent aussi.

Compatible **Jellyfin 12.x** et **Jellyfin 10.11**.

### Installation

1. Dans Jellyfin, ouvrez **Tableau de bord → Extensions → Dépôts** (ou **Gérer les dépôts**) et ajoutez ce dépôt :
   - Nom : `MEGA`
   - URL : `https://raw.githubusercontent.com/Sounne/jellyfin-plugin-mega/main/manifest.json`
2. Ouvrez le **Catalogue**, choisissez **MEGA** et installez-le.
3. Redémarrez Jellyfin.

### Configuration

Ouvrez **Tableau de bord → Extensions → MEGA**.

1. **Ajoutez une source.** Au choix :
   - **Compte (e-mail) :** e-mail, mot de passe et code 2FA si activé. Le mot de passe sert une fois pour se connecter et n'est jamais enregistré. Seule la session est gardée.
     MEGA demande parfois une preuve de travail avant d'accepter une connexion (contrôle anti-abus). Le plugin la calcule et la page affiche l'avancement. Cela prend quelques secondes sur un processeur récent, mais peut prendre plusieurs minutes sur un petit NAS. Si c'est trop long, utilisez plutôt une session MEGAcmd.
   - **Session (MEGAcmd) :** le résultat de `mega-session`.
   - **Lien de dossier public :** un lien `https://mega.nz/folder/…#…` avec sa clé.
2. **Ajoutez une bibliothèque.** Choisissez la source, cliquez sur **Browse** pour choisir un dossier MEGA, donnez un nom et un type (Films, Séries, Musique…), puis cliquez sur **Save**.

La bibliothèque apparaît après la première synchronisation. Pour un gros cloud, le premier chargement de l'arborescence peut prendre une ou deux minutes. Les démarrages suivants la lisent depuis le cache local.

Pour les séries, pointez la bibliothèque sur un dossier de séries. Un dossier qui contient une seule série (dossiers de saisons ou épisodes) fonctionne aussi.

Les réglages sont décrits dans le tableau [Settings](#settings) plus haut. Une resynchronisation complète tourne aussi une fois par jour (**Tableau de bord → Tâches planifiées → MEGA**). Le bouton **Synchronize now** en lance une tout de suite.

### Sécurité

- La session MEGA est stockée dans la configuration du plugin (`plugins/configurations/Jellyfin.Plugin.Mega.xml`), comme tout réglage de plugin Jellyfin. Quiconque peut lire le dossier de données de Jellyfin peut l'utiliser. Pour la révoquer, supprimez la source dans la page du plugin, puis fermez la session dans les réglages MEGA (Sécurité → Sessions).
- Les adresses de flux internes sont signées avec un secret du serveur. Par défaut, seul le serveur lui-même peut les utiliser.
- L'arborescence en cache est chiffrée avec une clé dérivée de la session.

### Limites

- **Quota de transfert MEGA.** Tout ce que vous regardez compte dans votre quota de transfert MEGA. Les lectures de Jellyfin comptent aussi : analyse, extraction d'images et de sous-titres. Les comptes gratuits ont un petit quota.
- Jellyfin analyse de nouveau un élément `.strm` à chaque début de lecture. Cela ajoute une fraction de seconde, servie en grande partie depuis le cache disque.
- Jellyfin lance ffprobe et ffmpeg avec une fenêtre d'analyse de 200 secondes. Quand une piste n'est pas entièrement décrite dans l'en-tête du fichier (sous-titres PGS dans un MKV, par exemple), ffmpeg lit alors environ 350 Mo d'un film 1080p avant de démarrer. Pour les éléments MEGA seulement, le plugin réduit cette fenêtre à 10 secondes (réglage avancé **ffmpeg analysis of MEGA files**, 0 = réglage de Jellyfin).
- Quand un client ne peut pas prendre les sous-titres intégrés avec la vidéo (transcodage Chromecast ou Android TV, sous-titres affichés par le lecteur web, texte incrusté), Jellyfin les extrait avec ffmpeg, qui lit tout le fichier. Pour les MKV sur MEGA, le plugin lit seulement les blocs de sous-titres, repérés par l'index du fichier (cues), et les écrit dans le cache de sous-titres de Jellyfin : quelques secondes et quelques Mo, une seule fois par fichier.
- Pas d'images trickplay ni d'images de chapitres pour ces éléments : il faudrait lire les fichiers en entier.
- Ce raccourci demande un MKV dont l'index liste les blocs de sous-titres (cas de mkvmerge et ffmpeg). Pour les autres fichiers (anciens MKV, MP4, pistes VobSub), l'extraction de Jellyfin lit tout le fichier la première fois qu'un client a besoin des sous-titres à part. Les sous-titres externes à côté de la vidéo sont toujours rapides.
- Jellyfin n'applique pas de limite de débit à ces éléments. Sur une connexion lente, choisissez la qualité à la main.
- Un fichier ignoré à cause de son nom (par exemple `film.mkv.part`) puis renommé en nom de média n'apparaît qu'après la resynchronisation complète quotidienne.
- Projet non affilié à MEGA ni à Jellyfin.
