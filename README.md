# LloydScan — numérisation optimisée et routage Vie / Lloyd

Application Windows (C# .NET 10 LTS, WPF) qui :

1. **numérise** par lots sur **tous les scanners pilotables sous Windows** : TWAIN (pilote du fabricant), WIA (pilote intégré à Windows), et copieurs multifonctions réseau via un **dossier surveillé** (PDF, TIFF, JPEG) ;
2. **nettoie** automatiquement chaque page (bords noirs du scanner, trous de perforation, redressement) et **découpe** le lot en documents grâce aux **pages séparatrices à QR code** ;
3. **route** chaque document : *QR contenant « VIE » → WebDAV Vie, sinon → WebDAV Lloyd* ;
4. **optimise** chaque page (compression MRC) : PDF 5 à 10 fois plus légers, texte net comme l'original ;
5. **dépose** les PDF sur le bon serveur WebDAV **avec le compte personnel de l'agent** (ELISE Lloyd, Lloyd Vie), avec file d'attente locale en cas de panne et journal d'audit ;
6. **verrouille** les paramètres de numérisation et d'optimisation : les agents choisissent un profil, ils ne peuvent pas le modifier.

---

## 1. Flux de traitement

```
Scanner TWAIN ──► page reçue ──► lecture QR + détection page blanche (150 dpi)
                                   │
                                   ├─ page blanche ............ retirée (récupérable avant envoi)
                                   ├─ page séparatrice (QR) ... ouvre un nouveau document
                                   └─ page utile .............. optimisée immédiatement (MRC)
                                                                  │
fin du lot ──► découpe en documents ──► routage VIE / LLOYD ──► PDF ──► contrôle opérateur ──► WebDAV
                                                                                      │
                                                                     échec réseau ──► file d'attente locale
```

Chaque page est compressée **dès sa réception** (en parallèle) : la mémoire reste maîtrisée même sur des lots de plusieurs centaines de pages.

## 2. Règle de routage (règle unique)

> **Toute page portant un QR code est une page séparatrice.**
> **Si le QR contient « VIE » → profil WebDAV Lloyd Vie ; sinon → profil WebDAV ELISE Lloyd.**

| QR de la page séparatrice | Destination |
|---|---|
| contient `VIE` (majuscules ou minuscules : `CASIER_DG_VIE`, `LLOYD-SEP;ENTITE=VIE`, `dossier vie 2026`…) | profil **Vie** (Lloyd Vie) |
| ne contient pas `VIE` (`CASIER_SINISTRES_AUTO`, `LLOYD-SEP;ENTITE=LLOYD`…) | profil **Lloyd** (ELISE Lloyd) |
| pages scannées **avant** tout QR | profil **Lloyd** + **alerte** (ligne orange, confirmation avant envoi) |

Cela fonctionne avec les **pages de séparation imprimées depuis ELISE** (QR = casier, ex. `CASIER_DG_VIE`) comme avec les feuilles LloydScan (`separateurs/Separateurs_LLOYD.pdf`). **La fiche séparatrice est conservée : elle reste la 1re page du PDF déposé** (QR toujours lisible après compression), ce qui permet à ELISE de la relire et garde la trace de l'orientation. Elle ne peut être ni supprimée ni déplacée par l'agent. `Separator.RemoveSeparatorPages` = `true` la retirerait, sur décision MOA uniquement. Testé : `CASIER_DG_VIE` → Vie, `CASIER_SINISTRES_AUTO` → Lloyd, `LLOYD-SEP;ENTITE=VIE` → Vie, `LLOYD-SEP;ENTITE=LLOYD` → Lloyd, `dossier vie 2026` → Vie.

Réglages disponibles si besoin (`appsettings.json`) : `Routing.MatchMode` = `WholeWord` pour exiger le **mot** VIE (évite qu'un casier contenant « SERVIETTE » parte vers Vie) ; `Separator.Pattern` pour ne reconnaître que certains QR (ex. `^CASIER_`) si des documents portent leur propre QR code ; `FolderPattern` avec `{qr}` pour un sous-dossier par casier ; `OperatorOverride` pour autoriser ou non l'agent à corriger la destination.

## 3. Optimisation : pourquoi les PDF sont légers *et* nets

Le mode **Auto** (profil « Standard optimisé ») applique la technique **MRC** (*Mixed Raster Content*), celle des solutions de capture professionnelles :

| Couche | Contenu | Résolution | Compression |
|---|---|---|---|
| Texte | masque 1 bit des caractères et traits | 300 dpi | CCITT G4 (sans perte) |
| Fond | papier, logos, aplats, tampons pâles | 150 dpi | JPEG q60 |
| Couleur du texte | encre bleue des signatures, tampons | 150 dpi | JPEG q65 (seulement si la page est en couleur) |

Le texte garde la netteté d'un scan 300 dpi, le reste de la page coûte quelques Ko. Règles complémentaires :

**Trois niveaux de compression** (mesurés sur 5 pages A4 couleur : contrat, sinistre signé et tamponné, photo) :

| Niveau | Fond / couleur | Photos | Taille moyenne | Usage |
|---|---|---|---|---|
| Compact | 100 dpi q45 / 100 dpi q50 | 200 dpi q70 | 92 Ko/page | volumétrie maximale (franges visibles autour des logos) |
| **Équilibré** (profil « Standard optimisé ») | 150 dpi q60 / 150 dpi q65 | 200 dpi q80 | 123 Ko/page | courrier, contrats, formulaires |
| Haute fidélité (profil du même nom) | 200 dpi q75 / 200 dpi q75 | 300 dpi q85 | 225 Ko/page | contrats signés, pièces à valeur de preuve |

Le texte noir est restitué avec la teinte réelle du cœur des traits (5e percentile), et la couleur de l'encre est mesurée sur le cœur du trait, pas sur son bord clair : signatures et tampons gardent leur bleu.


- page **sans couleur** → MRC en niveaux de gris (texte d'une teinte unie, fidèle à l'encre réelle) ;
- page **à dominante photo** (constat, photos de véhicule) → JPEG classique, le MRC n'apportant rien sur une photo ;
- **pas de JBIG2 avec perte** : cette compression peut remplacer un chiffre par un autre (6 ↔ 8), un risque inacceptable sur des montants ou des numéros de contrat.

### Résultats mesurés (page A4, 300 dpi couleur, banc d'essai)

| Page | Brut | JPEG 300 dpi q75 | JPEG 200 dpi q70 | **LloydScan** |
|---|---|---|---|---|
| Contrat dense + logo + signature bleue + tampon | 24,9 Mo | 770 Ko | 354 Ko | **72 à 80 Ko** (MRC couleur) |
| Déclaration de sinistre avec photo | 24,9 Mo | 454 Ko | 174 Ko | **154 Ko** (photo détectée → JPEG) |
| Contrat, profil « Texte noir et blanc » | 24,9 Mo | — | — | **55 Ko** (G4) |

Les profils sont ajustables dans `appsettings.json`, rubrique `Profiles`, sans recompiler.

### Texte interrogeable (OCR) et PDF/A — exigence ENF-06

Chaque page numérisée passe par **Tesseract 5.4** (Apache 2.0, livré dans `tools\tesseract` avec les modèles haute précision **français et arabe**). Le texte reconnu est ajouté au PDF en **couche invisible** : l'aspect de la page ne change pas (rendu identique au pixel près), mais le document devient **recherchable, copiable et indexable** dans ELISE (EF-24, EF-25).

| Mesure (spécification GED ELISE scannée, 14 pages) | Résultat |
|---|---|
| Mots retrouvés par l'OCR | **99,6 %** (1 982 / 1 990) |
| Arabe (page de contrat de test) | lignes reconnues dans le bon ordre de lecture (« اسم المؤمن له : محمد بن علي ») |
| Surcoût de taille | environ +4 Ko par page (540 → 591 Ko pour 14 pages) |
| Temps | environ 2 à 3 s par page, en parallèle |

Les documents numérisés sont produits en **PDF/A-2b** (métadonnées XMP, profil de couleur sRGB intégré, polices conformes) : format d'archivage à long terme exigé par la spécification. Réglages : `Ocr` (activation, langues, confiance minimale) et `Pdf.PdfA`. L'OCR est désactivé pour le profil « Photos sinistre ». S'il est indisponible (Windows 32 bits, dossier absent), les PDF sont produits sans texte, sans bloquer l'envoi.

### PDF importés (bouton « Importer », dossiers surveillés)

L'application reconnaît automatiquement deux sortes de PDF :

| PDF reçu | Traitement | Exemple mesuré |
|---|---|---|
| **PDF natif** (créé par un logiciel : Word, Averroès, navigateur… texte sélectionnable) | conservé tel quel et **optimisé sans perte** (qpdf : regroupement et recompression maximale des objets) ; rendu identique au pixel près, texte et liens conservés | spécification GED ELISE, 14 pages : **494 Ko → 152 Ko** (−68 %) |
| **PDF scanné** (scanner, copieur : une image par page) | traité comme une numérisation : nettoyage, découpe QR, compression MRC | PDF de copieur en JPEG 300 dpi, 14 pages : **11,6 Mo → 564 Ko** (−95 %) |

Un PDF natif forme son propre document ; il prend la destination de la page séparatrice qui le précède, et cette fiche est insérée en 1re page devant le PDF d'origine (fusion sans recompression par qpdf, texte et pages du PDF intacts) ; sinon il est signalé en alerte (destination par défaut). Les retouches de pages ne s'appliquent pas aux PDF natifs. Un PDF protégé par mot de passe est transmis tel quel. Réglage `Pdf.NativePdfMode` : `Optimize` (par défaut) ou `Rasterize`.

Remarque : un PDF de copieur « avec OCR » (couche texte invisible) est traité comme un scan, et la couche texte n'est pas conservée.

## 4. Nettoyage et retouches avant envoi

### Nettoyage automatique (réglé par profil, non modifiable par l'agent)

| Traitement | Ce qu'il corrige |
|---|---|
| Bords sombres | fond noir du scanner visible autour d'une page de travers ou plus petite (jusqu'à 20 mm depuis chaque bord) |
| Trous de perforation | taches rondes de 4 à 9 mm dans les 25 mm de marge, remplacées par la couleur du papier |
| Redressement | inclinaison jusqu'à ±5°, en complément du redressement du pilote |

Le nettoyage s'applique avant la détection des pages blanches et l'optimisation. Il est désactivé pour les profils « Photos sinistre » et « Pièce d'identité ».

**Pages pâles et pages blanches.** Une page est « blanche » si elle n'a presque aucun trait plus foncé que le papier autour (contraste ≥ 25 niveaux) : une écriture pâle (fax délavé, crayon, encre bleu clair, photocopie faible, papier jaune) compte comme du contenu et la page est gardée ; le grain du papier et le léger transfert du verso restent ignorés. Ces pages pâles sont **rehaussées** à la compression (`Output.EnhanceFaintText`, teinte du texte ≤ `MaxTextTone` = 70) : le trait entier est capté dans la couche texte et restitué foncé, en gardant la couleur de l'encre. Filet de sécurité : les pages retirées comme blanches sont conservées jusqu'à l'envoi ; le bouton **Pages blanches retirées…** les réintègre à leur place d'origine pour vérification (retouche tracée dans le journal), et « Rétablir le lot » revient à l'état initial. Testé : fax délavé, crayon, bleu clair, papier jaune, mention courte « Lu et approuvé » gardés ; page vierge et verso avec transfert retirés.

### Retouches manuelles (écran de contrôle, page sélectionnée)

| Bouton | Effet |
|---|---|
| ⟲ 90° / ⟳ 90° / 180° | rotation **sans perte** : la page n'est pas recompressée (instruction de rotation dans le PDF) |
| ◀ / ▶ | change l'ordre des pages |
| Nettoyer | nettoyage complet de la page depuis sa copie de numérisation, puis nouvelle optimisation |
| Supprimer | retire la page (un document vidé disparaît) |
| Découper ici | la page et les suivantes forment un nouveau document (page séparatrice oubliée) |
| Fusionner avec le suivant | réunit deux documents de même destination (séparatrice en trop) |
| Rétablir le lot | annule toutes les retouches du lot |

Chaque retouche régénère immédiatement le PDF (taille et nom mis à jour). Le journal d'envoi indique les retouches effectuées. Une fois envoyé ou mis en file d'attente, un document n'est plus modifiable.

Pour permettre le bouton « Nettoyer », une copie de chaque page est gardée dans `%LOCALAPPDATA%\LloydScan\Travail`. Elle est **supprimée dès l'envoi** (ou en vidant la liste, en quittant l'application, et au plus tard 2 jours après en cas d'arrêt brutal). Réglage `KeepSourceForEditing` par profil.

## 5. Comptes des agents et verrouillage des paramètres

### Un compte WebDAV par agent et par instance

Chaque destination définit son mode d'authentification (`Targets.<nom>.Authentication`) :

| Mode | Usage | Où est le mot de passe ? |
|---|---|---|
| `Agent` (par défaut) | chaque agent a son compte **ELISE Lloyd** et son compte **Lloyd Vie** | saisi par l'agent dans **Mes identifiants**, chiffré DPAPI dans *sa* session Windows (`%APPDATA%\LloydScan\identifiants.json`) |
| `Windows` | serveurs WebDAV intégrés à l'Active Directory | aucun : session Windows de l'agent (Kerberos/NTLM) |
| `Service` | compte technique commun | `appsettings.json`, chiffré avec `--protect` |

- Au **premier lancement**, la fenêtre *Mes identifiants* s'ouvre : l'agent saisit ses deux comptes, clique sur **Tester** (contrôle immédiat sur le serveur), puis **Enregistrer**.
- Un autre agent sur le même poste ne voit ni n'utilise ces comptes (chiffrement lié à la session Windows ; journal, file d'attente et identifiants sont propres à chaque profil Windows).
- Si un mot de passe expire, l'envoi échoue avec « identifiants refusés » et le document reste **en file d'attente**. L'agent met à jour *Mes identifiants*, puis **Renvoyer la file**.
- Le **journal** trace pour chaque dépôt l'agent Windows **et** le compte WebDAV utilisé.

### Choix du profil : règles de l'administrateur LLOYD

Bloc `ProfilePolicy` de `appsettings.json` (fichier protégé, modifiable uniquement par l'administrateur) :

| Réglage | Effet |
|---|---|
| `AgentProfiles` | profils que l'agent peut choisir ; livré avec Standard optimisé, Texte noir et blanc, Photos sinistre, Pièce d'identité |
| `Assignments` | profil attribué par poste (`"Computer": "LLOYD-JUR-*"`) ou par agent (`"User": "rbouraoui"`), jokers `*` et `?` ; la première ligne qui correspond s'applique, et ce profil devient le profil de départ, même s'il est réservé |
| `AgentCanChooseProfile` | `false` : liste verrouillée, le profil attribué au poste est imposé (grisé, info-bulle « Profil imposé par l'administrateur LLOYD ») |

Les deux profils sont conservés : **Standard optimisé** (Équilibré) pour tous, **Haute fidélité** sur attribution. Dans Haute fidélité, `ImageDpi` fixe le traitement des photos : 300 (option A, fidélité maximale, livrée) ou 200 (option B, ≈ 3 × plus léger). Un nom de profil mal saisi dans `ProfilePolicy` bloque le démarrage avec un message explicite. Testé : poste d'agence → 4 profils ; poste `LLOYD-JUR-03` → Haute fidélité au départ ; choix fermé → un seul profil.

### Ce que l'agent peut et ne peut pas faire

| L'agent peut | L'agent ne peut pas |
|---|---|
| choisir un scanner et un **profil parmi ceux ouverts par l'administrateur** (`ProfilePolicy.AgentProfiles`) | modifier résolution, couleur, recto-verso, pages blanches, compression |
| — | choisir « **Haute fidélité** » : ce profil est **réservé**, attribué par l'administrateur LLOYD à un poste ou à un agent (`ProfilePolicy.Assignments`) |
| saisir et tester **ses** identifiants | ouvrir l'interface du pilote TWAIN (désactivée : les réglages viennent uniquement du profil) |
| contrôler les documents, ouvrir le PDF, envoyer | modifier la règle VIE / LLOYD, les URL WebDAV, le nommage |
| corriger la destination **si l'administrateur l'autorise** (`OperatorOverride`) | changer de destination un document routé par son QR (réglage par défaut) |

Le bandeau « 🔒 Profil verrouillé » affiche en lecture seule les réglages du profil choisi.

**Protection du fichier de configuration** : `appsettings.json` doit être installé dans un dossier en lecture seule pour les agents (`C:\Program Files\LloydScan`, droits par défaut de Windows). Au démarrage, l'application vérifie que l'agent connecté ne peut pas modifier ce fichier ni son dossier ; sinon, la version Release **refuse de démarrer** (« installation non conforme »). En Debug, un simple avertissement s'affiche pour permettre les tests.

## 6. Compatibilité scanners et postes

### Scanners : trois voies d'acquisition

| Voie | Matériel couvert | Remarques |
|---|---|---|
| **TWAIN** (recommandé) | scanners professionnels : Fujitsu/Ricoh, Kodak Alaris, Canon DR, Epson DS, Brother ADS, Avision, Plustek… | toutes les options (recto-verso, redressement, détection des bords) ; pilote TWAIN 32 bits pour la version x86 |
| **WIA** | tout scanner reconnu par Windows, y compris sans pilote TWAIN (multifonctions USB, modèles grand public) | pilote intégré à Windows, indépendant du 32/64 bits ; options selon le pilote (recto-verso parfois absent) |
| **Dossier surveillé** | copieurs multifonctions réseau (Canon, Ricoh, Xerox, Konica, Sharp…) en « scan vers dossier » | le copieur dépose un PDF/TIFF/JPEG ; LloydScan le découpe (QR), l'optimise et l'envoie ; profil du copieur à régler en 300 dpi |
| Import manuel | tout fichier PDF, TIFF, JPEG, PNG, BMP | bouton « Importer des images… » |

La liste « Scanner » affiche chaque appareil avec sa voie, par exemple « fi-7160 (TWAIN) » ou « EPSON ET-4850 (WIA) ». Un même scanner peut apparaître deux fois : il faut alors préférer TWAIN.

**Ce qui reste hors périmètre** : les scanners sans pilote Windows (ni TWAIN ni WIA) et sans fonction « scan vers dossier » (ISIS seul, matériel très ancien). Ils sont rares.

### Postes

| | Pris en charge |
|---|---|
| Système | Windows 10 (version 1809 ou plus) et Windows 11 |
| Architecture | version x86 : PC 32 bits, 64 bits et ARM (émulation) ; version x64 en option |
| Runtime .NET | **embarqué** avec `publier.cmd` : rien à installer |
| Hors périmètre | Windows 7 / 8.1 (non pris en charge par .NET 10), macOS, Linux |

## 7. Structure de la solution

```
LloydScan.sln
├─ src/LloydScan.Core        (net10.0, aucune dépendance externe — testable hors Windows)
│  ├─ Configuration/          profils, routage, WebDAV, nommage
│  ├─ Imaging/                seuillage local, détection couleur/photo/page blanche, encodeur CCITT G4, nettoyage (bords, trous, redressement)
│  │  └─ Qr/                  lecteur de QR code intégré (repérage, perspective, Reed-Solomon, données) : aucune bibliothèque externe
│  ├─ Ocr/                    moteur OCR Tesseract (mots + positions)
│  ├─ Pdf/                    écrivain PDF (couche texte invisible, PDF/A-2b), (images insérées sans recompression), QpdfTool (PDF natif / scanné, optimisation sans perte)
│  ├─ Processing/             pipeline, optimiseur MRC, découpe par séparatrices, retouches (DocumentEditor), nommage, dossiers surveillés
│  ├─ Routing/                règle VIE / LLOYD
│  └─ Upload/                 client WebDAV (MKCOL/PUT/HEAD), file d'attente, journal CSV
├─ src/LloydScan.App         (net10.0-windows, WPF)
│  ├─ Services/TwainScanner   pilotage TWAIN (NTwain, MIT)
│  ├─ Services/WiaScanner     pilotage WIA (bibliothèque Windows, thread STA)
│  ├─ Services/PdfRasterizer  lecture des PDF de copieurs (moteur PDF de Windows)
│  ├─ Services/WindowsPdfTool qpdf + vignette de la 1re page des PDF natifs
│  ├─ Services/WicImageCodec  lecture BMP/TIFF/JPEG/PNG, encodage JPEG (WIC, intégré à Windows)
│  ├─ Services/PasswordProtector  chiffrement DPAPI (machine pour le compte de service, utilisateur pour les agents)
│  ├─ Services/AgentCredentialStore  comptes WebDAV personnels de l'agent
│  ├─ Services/ConfigGuard    contrôle « configuration non modifiable par l'agent »
│  ├─ CredentialsWindow       fenêtre « Mes identifiants »
│  └─ ViewModels, MainWindow  écran agent
├─ tools/qpdf/                qpdf 12.4.2 pour Windows (Apache 2.0, licence et NOTICE inclus)
├─ tools/tesseract/           Tesseract 5.4 (64 bits) + modèles fra/ara haute précision (Apache 2.0, NOTICE inclus)
└─ separateurs/               feuilles séparatrices à imprimer
```

Licences des dépendances : NTwain (MIT), System.Security.Cryptography.ProtectedData (MIT), qpdf (Apache 2.0, avec les DLL d'exécution Visual C++ redistribuables), Tesseract et modèles tessdata_best (Apache 2.0, bibliothèques jointes sous licences libres : voir `tools/tesseract/NOTICE.txt`). Aucune licence commerciale.

## 8. Compilation et déploiement

Prérequis : SDK **.NET 10** ou **.NET 11** (gratuit), ou Visual Studio 2026 (charge de travail « Développement .NET Desktop »), Windows 10/11.

**Version de .NET** : l'application cible **.NET 10 (LTS, supportée jusqu'au 14 novembre 2028)** ; .NET 8 n'est plus supporté depuis le 10 novembre 2026. Avec seulement le SDK .NET 11 installé, elle se compile et se lance quand même (`RollForward=Major`). Pour la cibler explicitement en .NET 11 (STS, même fin de support), remplacer `net10.0` par `net11.0` dans les deux fichiers `.csproj`.

Test rapide sans publication : `dotnet run --project src\LloydScan.App` (mode développement).

**Poste verrouillé (aucune installation possible)** : la compilation peut se faire ailleurs, puis le dossier obtenu est simplement copié.
- **Dans le cloud GitHub** : déposer le projet dans un dépôt GitHub **privé** ; le fichier `.github/workflows/compilation.yml` compile automatiquement sur un serveur Windows de GitHub. Télécharger ensuite l'artefact **LloydScan-TEST-portable-x86** (onglet *Actions*), le dézipper dans *Documents* et lancer `LloydScan.exe`.
- **Sur un autre PC** disposant du SDK .NET 10 : `publier.cmd test`, puis copier le dossier `publish\test-x86`.

La version de test fonctionne depuis n'importe quel dossier (avertissement « configuration modifiable » seulement) ; la version finale exige l'installation dans `C:\Program Files\LloydScan`. Si la politique de sécurité du poste bloque les programmes non signés (AppLocker, SmartScreen), l'exécution nécessite une autorisation de la DSI.

Double-cliquer sur **`publier.cmd`** à la racine (ou `publier.cmd x64`). Le dossier `publish\x86` contient l'application **avec son runtime .NET** : il n'y a rien d'autre à installer sur les postes.

- **32 / 64 bits** : l'application est compilée en **x86**, car tous les pilotes TWAIN existent en 32 bits, alors que les pilotes 64 bits ne sont pas garantis. Si tous les scanners du parc ont un pilote TWAIN 64 bits, publier avec `publier.cmd x64`. Les scanners WIA et les dossiers surveillés fonctionnent avec les deux versions.
- Déployer le dossier `publish` **dans `C:\Program Files\LloydScan`** (MSI, Intune ou GPO), avec `appsettings.json` à côté de `LloydScan.exe`. Les agents doivent avoir les droits *Lecture et exécution* seulement.

## 9. Configuration (`appsettings.json`)

1. **URL WebDAV** des deux instances dans `Targets.Vie.Url` et `Targets.Lloyd.Url`.
2. **Authentification** (`Authentication`) : `Agent` (compte personnel, par défaut), `Windows` ou `Service` (voir §4). Pour `Service`, lancer sur le poste cible `LloydScan.exe --protect "MotDePasse"`, puis coller la valeur `dpapi:…` dans `Password`.
   `DisplayName` donne le libellé affiché aux agents (« ELISE Lloyd », « Lloyd Vie »).
3. **Arborescence distante** : `FolderPattern`, par défaut `{date:yyyy}/{date:MM}/{date:dd}`.
4. **Nom des fichiers** : `Naming.FilePattern`, par défaut `20261120-143005_Vie_LLOYD-SEP-ENTITE-VIE_001.pdf`.
5. **Règle de routage** : `Separator.Pattern` vide (tout QR code est une page séparatrice) et `Routing.VieKeyword` = `VIE` (voir §2). Si des documents portent leur propre QR code, limiter les séparatrices avec un motif, par exemple `^CASIER_`.

Le contenu du QR est aussi écrit dans les métadonnées du PDF (`Keywords`, `LloydQr`, `LloydEntite`, `LloydLot`), exploitable pour l'indexation dans la GED.

## 10. Utilisation (agent)

0. Au premier lancement : renseigner et tester ses comptes dans **Mes identifiants**.
1. Choisir le **scanner** et le **profil**, poser le lot avec une feuille séparatrice avant chaque document.
2. Cliquer sur **Numériser**. Les documents apparaissent avec leur destination, leur taille et le gain obtenu.
3. Lire la **taille en sortie** : pour chaque document, les colonnes *Avant* (image du scanner ou fichier importé), *Après (PDF)*, *Par page* et *Gain* ; le total du lot s'affiche en bas de l'écran (ex. « Taille : 11,3 Mo → 615 Ko (−94,7 %, 44 Ko par page) ») et le journal enregistre `OctetsAvant` / `OctetsApres`.
4. Contrôler : les lignes en **orange** signalent une alerte (pages sans séparatrice). Vérifier les vignettes et l'aperçu PDF, retoucher si besoin (rotation, ordre, suppression, découpe, nettoyage) ; corriger la destination si l'administrateur l'autorise (par défaut : seulement pour les pages sans séparatrice).
5. Cliquer sur **Envoyer tout**. Si un serveur est injoignable, le document passe **en file d'attente** : rien n'est perdu. Le bouton **Renvoyer la file** le dépose plus tard, y compris après un redémarrage.

Le bouton **Importer des images…** traite des PDF, TIFF, JPEG ou PNG déjà numérisés. Les **dossiers surveillés** font la même chose automatiquement : l'agent n'a qu'à valider et envoyer, ou rien du tout si `AutoSend` est activé pour les documents sans alerte.

## 11. Exploitation

| Élément | Emplacement |
|---|---|
| Journal des envois (CSV, s'ouvre dans Excel) | `%LOCALAPPDATA%\LloydScan\Journal\journal-AAAA-MM.csv` |
| File d'attente (PDF + fiche JSON) | `%LOCALAPPDATA%\LloydScan\Outbox\<destination>\` |
| Copie locale optionnelle (`KeepLocalCopy`) | `%LOCALAPPDATA%\LloydScan\Archive\` |
| Erreurs inattendues | `%LOCALAPPDATA%\LloydScan\erreurs.log` |
| Comptes WebDAV de l'agent (chiffrés) | `%APPDATA%\LloydScan\identifiants.json` |

Le journal contient une ligne par envoi : date, poste, agent Windows, lot, destination, compte WebDAV utilisé, fichier, QR, pages, taille, statut et URL.

## 12. Ce qui a été vérifié — et ce qui reste à recetter

**Vérifié sur banc d'essai** (pages A4 300 dpi simulées : papier teinté, bruit, logo, signature bleue, tampon, photos, versos blancs) :
- découpe par séparatrices, retrait des pages blanches, fiche séparatrice conservée en 1re page (QR relu dans le PDF final), routage VIE / LLOYD, alertes (pages sans séparatrice, séparatrice en fin de lot) ;
- lecture des QR avec le **lecteur intégré** (aucune bibliothèque QR externe) : 207/207 matrices de test (versions 1 à 40, niveaux L/M/Q/H, erreurs injectées, UTF-8 et arabe) ; 126/126 pages synthétiques (modules de 2 à 10 px, rotations de 0 à 360°, flou, bruit, JPEG, impression pâle, papier gris) ; 28/28 pages réelles (fiches ELISE, feuilles LLOYD, pages sans QR : aucune fausse lecture) ; sur 200 pages volontairement dégradées, 172 lues contre 167 avec ZXing.Net ;
- PDF valides (`qpdf --check`), rendus contrôlés avec deux moteurs (Poppler et Ghostscript) ; encodeur G4 identique au pixel près à la version sans perte ;
- dépôt WebDAV réel (création des dossiers, PUT, vérification HEAD, authentification Basic), file d'attente sur serveur coupé puis reprise, journal CSV ;
- comptes par agent : refus sans identifiants, mot de passe erroné → « identifiants refusés » et mise en file d'attente, reprise après correction, compte WebDAV tracé dans le journal ;
- coffre d'identifiants (enregistrement / relecture) et contrôle de configuration : dossier protégé → démarrage autorisé, dossier modifiable par l'utilisateur → refus ;
- dossier surveillé : fichier en cours d'écriture ignoré, fichier stable traité puis déplacé dans `Traites`, fichier sans document dans `Erreurs`, fichiers non images ignorés ;
- nettoyage sur une page scannée de travers (2,3°) avec fond noir et 2 trous : bords effacés, 2 trous effacés, angle mesuré 2,25°, page redressée ; aucune modification sur une page propre ;
- retouches : rotation (instruction /Rotate présente, rendu vérifié), déplacement, découpe, fusion, nettoyage manuel, suppression, puis rétablissement complet du lot ; PDF valides à chaque étape ;
- OCR et PDF/A : 99,6 % des mots de la spécification retrouvés après numérisation simulée, arabe extrait dans le bon ordre, rendu identique avec et sans couche texte, métadonnées XMP cohérentes avec le dictionnaire Info (identification PDF/A-2b, OutputIntent sRGB) ; la **validation formelle PDF/A par veraPDF** (outil gratuit) reste à faire sur Windows ;
- PDF importés (avec la spécification GED ELISE Contrat Auto) : PDF natif reconnu et optimisé sans perte (494 → 152 Ko, 14 pages identiques au pixel près, texte conservé), rattachement à la séparatrice précédente (fiche insérée en page 1 : 15 pages), PDF de copieur reconnu comme scan et compressé (11,6 Mo → 564 Ko) ; redressement mesuré à moins de 0,1° près de l'angle réel sur 14 pages.

**À recetter sur un poste Windows** : le projet WPF (dont la fenêtre *Mes identifiants*), le pilotage **WIA** et la lecture des **PDF**, qui dépendent de composants propres à Windows, et le module TWAIN n'ont pas pu être compilés dans l'environnement de développement, qui est sous Linux. Le code suit l'API publique de NTwain 3.7 et de WPF, mais une compilation sous Visual Studio et une recette sur les vrais scanners sont nécessaires. Points de recette :
1. compilation, puis numérisation sur chaque modèle de scanner du parc (pilote 32 bits, recto-verso, chargeur vide, bourrage) ;
2. connexion aux deux WebDAV réels (Kerberos/NTLM ou compte de service, HTTPS, proxy éventuel) ;
3. lot réel d'une centaine de pages de chaque type (contrat, sinistre, pièce d'identité) : lisibilité validée par les gestionnaires, tailles obtenues ;
4. réglage de `BlankInkRatio` sur des versos réels (transparence du recto, papier recyclé).

## 13. Points de vigilance et évolutions

- **Standardiser le QR** des séparatrices (`LLOYD-SEP;ENTITE=…`) : le mode `Contains` envoie vers Vie tout QR contenant « VIE », y compris dans un mot comme « SERVIETTE ». Passer en `WholeWord` si le QR contient du texte libre.
- **PDF/A** : valider un échantillon avec veraPDF pendant la recette. Les PDF natifs importés sont conservés tels quels (pas de conversion PDF/A).
- **OCR** : Tesseract est livré en 64 bits ; sur un Windows 32 bits, l'OCR est désactivé automatiquement. La qualité dépend de la numérisation (300 dpi recommandé).
- **Évolutions possibles** : déploiement MSIX/GPO, fichier d'index (JSON ou XML) déposé à côté de chaque PDF si la GED l'exige.
