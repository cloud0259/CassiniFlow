/**
 * CassiniFlow Web — Bilingual Translation Dictionary (FR / EN)
 */
const i18nData = {
  fr: {
    page_title: "CassiniFlow — Suite d'Imagerie Planétaire, Lunaire & Solaire | Prétraitement & Empilement Multi-Zones",
    meta_desc: "CassiniFlow est une suite logicielle open-source haute performance dédiée au prétraitement et à l'empilement multi-zones (AP, Drizzle 1.5x/3x, ADC) pour l'astrophotographie planétaire, lunaire et solaire.",
    og_title: "CassiniFlow — Prétraitement & Empilement Planétaire Haute Résolution",
    og_desc: "Triez, stabilisez, calibrez et empilez vos séquences planétaires, lunaires et solaires. Multiplateforme Windows & Linux.",

    brand_subtitle: "Planetary Imaging Suite",
    nav_modes: "Modes & Workflow",
    nav_features: "Fonctionnalités",
    nav_stacking: "Empilement",
    nav_themes: "Interface & Thèmes",
    nav_download: "Téléchargement",
    nav_guide: "Guide",
    nav_guide_cli: "Guide & CLI",
    btn_download: "Télécharger",
    badge_latest: "Dernière version",

    hero_badge: "CassiniFlow — Dernière version stable disponible",
    hero_title: `La suite moderne pour l'imagerie <span class="block mt-2 pb-3 bg-gradient-to-r from-sky-400 via-blue-500 to-amber-300 bg-clip-text text-transparent">planétaire, lunaire et solaire.</span>`,
    hero_subtitle: `Triez le seeing atmosphérique, stabilisez, calibrez et <strong class="text-white font-semibold">empilez vos images en multi-zones (AP) avec Drizzle sub-pixel</strong>. Du fichier SER brut au master final rehaussé, ou en export optimisé vers AutoStakkert!, Siril et WinJUPOS.`,
    hero_download_win: "Télécharger la dernière version (Windows x64)",
    hero_download_linux: "Télécharger la dernière version (Linux .deb)",
    hero_all_packages: "Tous les packages (Linux, Portable, CLI)",

    trust_open_source: "100% Gratuit & Open-Source (MIT)",
    trust_standalone: "Exécutables autonomes sans dépendances",
    trust_ram: "Mémoire RAM bornée < 900 Mo",
    trust_formats: "Compatible SER 8/16-bit, FITS & WinJUPOS",
    zoom_tooltip: "Ouvrir en plein écran (1870×1079)",
    fullscreen_btn: "Plein écran",

    modes_tag: "Polyvalence & Liberté",
    modes_title: "Deux façons d'utiliser CassiniFlow",
    modes_desc: "Que vous cherchiez un pipeline 100% autonome ou un pré-filtre haute performance pour vos logiciels existants, CassiniFlow s'adapte sans compromis.",

    mode1_badge: "Autonome & Direct",
    mode1_title: "1. Mode Tout-en-Un Direct (Flux Direct Intégral)",
    mode1_subtitle: "Transformez vos séquences brutes volumineuses en un master final empilé en une seule passe sans étape intermédiaire.",
    mode1_item1: "<strong>Ingestion universelle :</strong> SER 8/16-bit, FITS, AVI, MP4 sans conversion préalable.",
    mode1_item2: "<strong>Centrage & Tri de turbulence :</strong> Élimination automatique des mauvaises frames et centrage CoG sub-pixel.",
    mode1_item3: "<strong>Empilement Multi-Zones (AP) :</strong> Découpage en tuiles locales avec compensation de la turbulence résiduelle.",
    mode1_item4: "<strong>Restauration finale :</strong> Drizzle 1.5x/3x, correction de dispersion atmosphérique (ADC) et rehaussement netteté en direct.",
    mode1_footer_wf: "Workflow: Flux Direct Intégral",
    mode1_footer_out: "Sortie: FITS / TIFF 16-bit",

    mode2_badge: "Pipeline Modulaire",
    mode2_title: "2. Mode Prétraitement Seul (PreProcessing)",
    mode2_subtitle: "Préparez, allégez et calibrez vos séquences pour alimenter vos logiciels d'empilement ou de dé-rotation préférés.",
    mode2_item1: "<strong>Calibrations optiques :</strong> Soustraction de Master Dark, division Flat, correction des pixels chauds (Hot Pixels).",
    mode2_item2: "<strong>Réduction de poids :</strong> Recadrage dynamique centré sur la cible pour réduire la taille des fichiers de 70% à 90%.",
    mode2_item3: "<strong>Nommage WinJUPOS Automatique :</strong> Fichiers SER générés selon la convention <code>YYYY-MM-DD-HHMM_S</code> pour la dé-rotation planétaire.",
    mode2_item4: "<strong>Mode Join & Batch :</strong> Fusionnez plusieurs vidéos d'observation consécutives en un unique fichier SER horodaté.",
    mode2_footer_wf: "Workflow: Prétraitement Seul",
    mode2_footer_out: "Vers: AutoStakkert! / Siril / WinJUPOS",

    stacking_tag: "Moteur Algorithmique Intégré",
    stacking_title: "Empilement Multi-Zones Haute Définition",
    stacking_desc: "Un moteur développé avec traitement zero-allocation en mémoire pour rivaliser avec les meilleurs empileurs planétaires.",

    stack_c1_title: "Grille AP Multi-Échelle",
    stack_c1_desc: "Génération automatique de points d'alignement avec tuiles configurables (24, 48, 104 px). Élagage automatique des zones vides ou sous le seuil de luminosité.",
    stack_c2_title: "Drizzle Sub-Pixel",
    stack_c2_desc: "Suréchantillonnez vos images grâce aux modes Drizzle 1.5x et 3.0x pour extraire les fins détails martiens ou les festons de Jupiter au-delà de la matrice du capteur.",
    stack_c3_title: "Double-Stack Référence",
    stack_c3_desc: "Pré-empilement automatique d'une trame de référence ultra-propre pour guider le repérage de phase des poses suivantes, même à fort bruit ou gain élevé.",
    stack_c4_title: "ADC & Sharpening",
    stack_c4_desc: "Correction logicielle de la dispersion chromatique (Atmospheric Dispersion Corrector) et export simultané des versions brutes et accentuées avec ondelettes.",

    features_tag: "Performance & Rigueur Scientifique",
    features_title: "Toutes les fonctionnalités de CassiniFlow",
    feat1_title: "Codecs SER & FITS Haute Vitesse",
    feat1_desc: "Lecture et encodage SER 8-bit et 16-bit (Mono, RAW Bayer, RGB) avec préservation exacte des horodatages UTC par frame. Support universel MP4, AVI, MOV via OpenCV & FFmpeg.",
    feat2_title: "Centrage & Suivi Sub-Pixel",
    feat2_desc: "Détection automatique du limbe planétaire, estimation du centre de gravité (Center of Gravity - CoG) et mesure du diamètre en pixels pour un suivi continu sans dérive.",
    feat3_title: "Tri de Seeing Bounded Min-Heap",
    feat3_desc: "Mesure de netteté par variance laplacienne et GradientSharpness. Algorithme de tri à tas min borné pour traiter des dizaines de milliers de frames sous 900 Mo de RAM.",
    feat4_title: "Démosaïçage RAW de Pointe",
    feat4_desc: "Débayérisation haute fidélité pour matrices RGGB, BGGR, GBRG, GRBG avec les algorithmes Bilinéaire rapide, High-Quality Linear et Malvar-He-Cutler (gradient-corrected).",
    feat5_title: "Calibration Complète (Dark/Flat)",
    feat5_desc: "Soustraction de Master Dark, division de Master Flat et élimination dynamique des pixels chauds (Hot Pixels) pour purifier les données avant l'alignement.",
    feat6_title: "CassiniFlow.Cli Headless",
    feat6_desc: "Automatisez vos traitements par script bash ou PowerShell grâce à l'exécutable CLI fourni dans tous les packages. Idéal pour les observatoires automatisés.",

    themes_tag: "Ergonomie & Confort Visuel",
    themes_title: "Pensé pour le terrain au télescope",
    themes_desc: "Basculez instantanément entre les 3 thèmes conçus pour s'adapter à la nuit noire, au laboratoire ou au bureau.",
    btn_theme_dark: "Deep Space (Sombre)",
    btn_theme_night: "Night Vision (Rouge Astro)",
    btn_theme_light: "Clean Lab (Clair)",

    theme_dark_title: "Thème : Deep Space (Dark)",
    theme_dark_desc: "Optimisé pour les sessions nocturnes et le contraste planétaire.",
    theme_night_title: "Thème : Night Vision (Rouge Astro)",
    theme_night_desc: "Préserve la vision scotopique (rhodopsine) lors du contrôle au télescope.",
    theme_light_title: "Thème : Clean Lab (Light)",
    theme_light_desc: "Haute clarté pour l'analyse en bureau, cours ou salles éclairées.",

    dl_tag: "Installation Rapide",
    dl_title: "Télécharger CassiniFlow",
    dl_desc: "Exécutables autonomes sans runtime à installer. Aucune dépendance externe requise pour démarrer.",
    dl_latest_label: "Dernière version :",
    dl_latest_tag: "Dernière version stable",
    dl_date_fallback: "Mise à jour récente",

    pkg_win_setup_title: "Windows Setup",
    pkg_win_portable_title: "Windows Portable",
    pkg_linux_deb_title: "Linux Debian / Ubuntu",
    pkg_linux_tar_title: "Linux Portable & CLI",

    badge_recommended: "Recommandé",
    pkg_win_setup_desc: "Installateur complet avec raccourcis bureau et désinstallation propre.",
    pkg_win_portable_desc: "Archive ZIP sans droits administrateur. Idéal pour clé USB de terrain.",
    pkg_linux_deb_desc: "Paquet .deb natif compatible Ubuntu 22.04+, Debian, Linux Mint.",
    pkg_linux_tar_desc: "Archive autonome avec script install.sh et CassiniFlow.Cli.",
    file_label: "Fichier :",
    btn_dl_exe: "Télécharger (.exe)",
    btn_dl_zip: "Télécharger (.zip)",
    btn_dl_deb: "Télécharger (.deb)",
    btn_dl_tar: "Télécharger (.tar.gz)",

    dl_history_question: "Vous cherchez une version antérieure ou les logs détaillés des modifications ?",
    dl_history_link: "Consulter toutes les versions sur GitHub Releases →",

    qs_tag: "Prise en Main",
    qs_title: "Démarrer en 3 étapes simples",
    qs_step1_title: "Importez votre séquence",
    qs_step1_desc: "Glissez-déposez votre vidéo (SER, FITS, AVI, MP4) dans l'interface. L'AstroViewer affiche immédiatement la première frame avec histogramme et dimensions.",
    qs_step2_title: "Choisissez votre profil",
    qs_step2_desc: "Sélectionnez la cible (Jupiter, Saturne, Lune, Soleil) et votre mode : export SER prétraité pour AutoStakkert! ou empilement direct tout-en-un avec tuiles AP.",
    qs_step3_title: "Lancez le traitement",
    qs_step3_desc: "Cliquez sur \"Lancer le traitement\". Suivez la progression en temps réel et récupérez vos fichiers centrés ou votre master 16-bit Drizzlé.",
    cli_box_title: "Automatisation via CassiniFlow.Cli",

    cli_code: `# Exemple : Tri de seeing, centrage et recadrage 512x512
cassiniflow-cli --input "raw_jupiter.ser" \\
                --crop 512x512 \\
                --quality-top 25 \\
                --debayer malvar \\
                --output "jupiter_centered_top25.ser"

# Exemple : Empilement direct tout-en-un avec Drizzle 1.5x
cassiniflow-cli --input "raw_saturn.ser" \\
                --workflow direct-stream \\
                --drizzle 1.5 \\
                --stack-percent 30 \\
                --output "saturn_master_drizzle.fits"`,

    footer_copy: "© 2026 <strong>CassiniFlow</strong> — Projet Open-Source sous licence MIT.",
    footer_github: "Dépôt GitHub",
    footer_issues: "Signaler un Bug",
    footer_license: "Licence MIT"
  },

  en: {
    page_title: "CassiniFlow — Planetary, Lunar & Solar Imaging Suite | Preprocessing & Multi-Zone Stacking",
    meta_desc: "CassiniFlow is a modern high-performance open-source suite for planetary, lunar and solar imaging preprocessing and multi-zone stacking (AP, 1.5x/3x Drizzle, ADC).",
    og_title: "CassiniFlow — High-Resolution Planetary Preprocessing & Stacking",
    og_desc: "Sort, stabilize, calibrate, and stack your planetary, lunar, and solar sequences. Cross-platform Windows & Linux.",

    brand_subtitle: "Planetary Imaging Suite",
    nav_modes: "Modes & Workflow",
    nav_features: "Features",
    nav_stacking: "Stacking",
    nav_themes: "Interface & Themes",
    nav_download: "Download",
    nav_guide: "Guide",
    nav_guide_cli: "Guide & CLI",
    btn_download: "Download",
    badge_latest: "Latest Release",

    hero_badge: "CassiniFlow — Latest stable release available",
    hero_title: `The modern suite for <span class="block mt-2 pb-3 bg-gradient-to-r from-sky-400 via-blue-500 to-amber-300 bg-clip-text text-transparent">planetary, lunar and solar imaging.</span>`,
    hero_subtitle: `Grade atmospheric seeing, stabilize, calibrate, and <strong class="text-white font-semibold">stack your frames with multi-zone alignment (AP) and sub-pixel Drizzle</strong>. From raw SER recordings to final sharpened masters, or optimized exports for AutoStakkert!, Siril, and WinJUPOS.`,
    hero_download_win: "Download Latest Release (Windows x64)",
    hero_download_linux: "Download Latest Release (Linux .deb)",
    hero_all_packages: "All Packages (Linux, Portable, CLI)",

    trust_open_source: "100% Free & Open-Source (MIT)",
    trust_standalone: "Standalone binaries, zero dependencies",
    trust_ram: "Bounded RAM usage < 900 MB",
    trust_formats: "Compatible with SER 8/16-bit, FITS & WinJUPOS",
    zoom_tooltip: "Open full screen (1870×1079)",
    fullscreen_btn: "Fullscreen",

    modes_tag: "Versatility & Freedom",
    modes_title: "Two Ways to Use CassiniFlow",
    modes_desc: "Whether you need a fully autonomous all-in-one pipeline or a high-performance pre-filter for your existing software, CassiniFlow adapts seamlessly.",

    mode1_badge: "Autonomous & Direct",
    mode1_title: "1. Direct All-in-One Mode (DirectStream)",
    mode1_subtitle: "Turn large raw video sequences into a stacked final master in a single pass with no intermediate files.",
    mode1_item1: "<strong>Universal Ingestion:</strong> 8/16-bit SER, FITS, AVI, MP4 without prior conversion.",
    mode1_item2: "<strong>Centering & Seeing Sorter:</strong> Automatic rejection of blurred frames and sub-pixel CoG centering.",
    mode1_item3: "<strong>Multi-Zone Stacking (AP):</strong> Local alignment tiles with dynamic residual turbulence compensation.",
    mode1_item4: "<strong>Final Restoration:</strong> 1.5x/3x Drizzle, software ADC dispersion correction, and live wavelet sharpening.",
    mode1_footer_wf: "Workflow: Direct Stream All-in-One",
    mode1_footer_out: "Output: 16-bit FITS / TIFF",

    mode2_badge: "Modular Pipeline",
    mode2_title: "2. Surgical Preprocessing Mode (PreProcessing)",
    mode2_subtitle: "Curate, shrink, and calibrate your sequences to feed your favorite stacking or de-rotation software.",
    mode2_item1: "<strong>Optical Calibration:</strong> Master Dark subtraction, Master Flat division, and dynamic Hot Pixel elimination.",
    mode2_item2: "<strong>File Size Reduction:</strong> Dynamic target-centered crop reducing recording file sizes by 70% to 90%.",
    mode2_item3: "<strong>Automated WinJUPOS Naming:</strong> Output SER files generated with the standard <code>YYYY-MM-DD-HHMM_S</code> convention.",
    mode2_item4: "<strong>Join & Batch Modes:</strong> Merge multiple consecutive video runs into a single UTC-timestamped SER file.",
    mode2_footer_wf: "Workflow: Preprocessing Only",
    mode2_footer_out: "To: AutoStakkert! / Siril / WinJUPOS",

    stacking_tag: "Integrated Algorithmic Engine",
    stacking_title: "High-Definition Multi-Zone Stacking",
    stacking_desc: "An engine engineered with zero-allocation memory processing to match the best planetary stackers.",

    stack_c1_title: "Multi-Scale AP Grid",
    stack_c1_desc: "Automatic alignment point generation with configurable tiles (24, 48, 104 px). Dynamic pruning of empty or low-brightness background areas.",
    stack_c2_title: "Sub-Pixel Drizzle",
    stack_c2_desc: "Super-sample your frames with 1.5x and 3.0x Drizzle modes to uncover fine Martian markings or Jovian festoons beyond sensor resolution.",
    stack_c3_title: "Reference Double-Stack",
    stack_c3_desc: "Automatic pre-stacking of a high-SNR reference frame to guide phase correlation of consecutive frames, even in high gain or low light.",
    stack_c4_title: "ADC & Sharpening",
    stack_c4_desc: "Software correction of atmospheric chromatic dispersion (ADC) and simultaneous export of raw linear and wavelet-sharpened masters.",

    features_tag: "Performance & Scientific Rigor",
    features_title: "Everything CassiniFlow Offers",
    feat1_title: "High-Speed SER & FITS Codecs",
    feat1_desc: "High-speed 8-bit and 16-bit SER (Mono, RAW Bayer, RGB) reader and writer preserving precise UTC frame timestamps. Universal MP4, AVI, MOV support via OpenCV & FFmpeg.",
    feat2_title: "Sub-Pixel Centering & Tracking",
    feat2_desc: "Automatic planetary limb detection, Center of Gravity (CoG) estimation, and pixel diameter measurement for drift-free tracking.",
    feat3_title: "Bounded Min-Heap Seeing Sorter",
    feat3_desc: "Sharpness grading using Laplacian variance and GradientSharpness. Bounded min-heap sorting handles tens of thousands of frames under 900 MB RAM.",
    feat4_title: "Cutting-Edge RAW Demosaicing",
    feat4_desc: "High-fidelity debayering for RGGB, BGGR, GBRG, GRBG patterns via Fast Bilinear, High-Quality Linear, and Malvar-He-Cutler (gradient-corrected).",
    feat5_title: "Full Optical Calibration (Dark/Flat)",
    feat5_desc: "Master Dark subtraction, Master Flat division, and dynamic Hot Pixel elimination to clean sensor data before alignment.",
    feat6_title: "Headless CassiniFlow.Cli",
    feat6_desc: "Automate processing runs via bash or PowerShell scripts using the standalone CLI included in every package. Ideal for robotic observatories.",

    themes_tag: "Ergonomics & Visual Comfort",
    themes_title: "Built for Field Use at the Telescope",
    themes_desc: "Instantly toggle between 3 themes tailored for pitch-dark nights, lab analysis, or daytime offices.",
    btn_theme_dark: "Deep Space (Dark)",
    btn_theme_night: "Night Vision (Astro Red)",
    btn_theme_light: "Clean Lab (Light)",

    theme_dark_title: "Theme: Deep Space (Dark)",
    theme_dark_desc: "Optimized for night-sky sessions and planetary contrast.",
    theme_night_title: "Theme: Night Vision (Astro Red)",
    theme_night_desc: "Preserves dark adaptation (rhodopsin) during telescope monitoring.",
    theme_light_title: "Theme: Clean Lab (Light)",
    theme_light_desc: "High clarity for office work, lectures, and bright environments.",

    dl_tag: "Fast Installation",
    dl_title: "Download CassiniFlow",
    dl_desc: "Standalone binaries with zero prerequisites. No external runtimes required to get started.",
    dl_latest_label: "Latest version:",
    dl_latest_tag: "Latest Stable Release",
    dl_date_fallback: "Recent release",

    pkg_win_setup_title: "Windows Setup",
    pkg_win_portable_title: "Windows Portable",
    pkg_linux_deb_title: "Linux Debian / Ubuntu",
    pkg_linux_tar_title: "Linux Portable & CLI",

    badge_recommended: "Recommended",
    pkg_win_setup_desc: "Complete installer with desktop shortcuts and clean uninstallation.",
    pkg_win_portable_desc: "ZIP archive requiring no administrator privileges. Ideal for USB flash drives.",
    pkg_linux_deb_desc: "Native .deb package for Ubuntu 22.04+, Debian, Linux Mint.",
    pkg_linux_tar_desc: "Standalone archive with install.sh script and CassiniFlow.Cli.",
    file_label: "File:",
    btn_dl_exe: "Download (.exe)",
    btn_dl_zip: "Download (.zip)",
    btn_dl_deb: "Download (.deb)",
    btn_dl_tar: "Download (.tar.gz)",

    dl_history_question: "Looking for previous releases or detailed changelogs?",
    dl_history_link: "View all releases on GitHub Releases →",

    qs_tag: "Getting Started",
    qs_title: "Get Started in 3 Simple Steps",
    qs_step1_title: "1. Import Your Sequence",
    qs_step1_desc: "Drag & drop your video recording (SER, FITS, AVI, MP4) into the interface. AstroViewer immediately displays the first frame with histogram and dimensions.",
    qs_step2_title: "2. Choose Your Profile",
    qs_step2_desc: "Select your target (Jupiter, Saturn, Moon, Sun) and mode: preprocessed SER export for AutoStakkert! or direct all-in-one stacking with AP tiles.",
    qs_step3_title: "3. Run Processing",
    qs_step3_desc: "Click \"Start Processing\". Monitor progress in real time and retrieve your centered sequences or Drizzled 16-bit master.",
    cli_box_title: "Automation via CassiniFlow.Cli",

    cli_code: `# Example: Seeing sorting, centering and 512x512 crop
cassiniflow-cli --input "raw_jupiter.ser" \\
                --crop 512x512 \\
                --quality-top 25 \\
                --debayer malvar \\
                --output "jupiter_centered_top25.ser"

# Example: Direct all-in-one stacking with 1.5x Drizzle
cassiniflow-cli --input "raw_saturn.ser" \\
                --workflow direct-stream \\
                --drizzle 1.5 \\
                --stack-percent 30 \\
                --output "saturn_master_drizzle.fits"`,

    footer_copy: "© 2026 <strong>CassiniFlow</strong> — Open-Source Project under MIT License.",
    footer_github: "GitHub Repository",
    footer_issues: "Report an Issue",
    footer_license: "MIT License"
  }
};

