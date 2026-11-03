/**
 * CassiniFlow Web — Main Application Logic
 */

// Initialize Lucide Icons
if (typeof lucide !== 'undefined') {
  lucide.createIcons();
}

// Mobile Menu Toggle
function toggleMobileMenu() {
  const menu = document.getElementById('mobileMenu');
  if (menu) menu.classList.toggle('hidden');
}

// Global State
let currentLang = 'fr';
let detectedOS = 'windows';
let fetchedVersion = null;
let currentThemeKey = 'dark';

// -------------------------------------------------------------
// 1. Language Toggle & Apply (i18n)
// -------------------------------------------------------------
function setLanguage(lang) {
  currentLang = (lang === 'en') ? 'en' : 'fr';
  localStorage.setItem('cassini_lang', currentLang);
  document.documentElement.lang = currentLang;

  // Update switcher button label
  const btnLabel = document.getElementById('langLabel');
  if (btnLabel) btnLabel.innerText = (currentLang === 'fr') ? 'EN' : 'FR';

  if (typeof i18nData === 'undefined') return;
  const d = i18nData[currentLang];
  if (!d) return;

  // Update Head Titles & Meta
  document.title = d.page_title;
  const metaDesc = document.getElementById('metaDescription');
  if (metaDesc) metaDesc.content = d.meta_desc;
  const ogTitle = document.getElementById('ogTitle');
  if (ogTitle) ogTitle.content = d.og_title;
  const ogDesc = document.getElementById('ogDescription');
  if (ogDesc) ogDesc.content = d.og_desc;

  // Update all data-i18n elements (auto-detects HTML markup)
  document.querySelectorAll('[data-i18n]').forEach(el => {
    const key = el.getAttribute('data-i18n');
    if (d[key] !== undefined) {
      if (d[key].includes('<') && d[key].includes('>')) {
        el.innerHTML = d[key];
      } else {
        el.innerText = d[key];
      }
    }
  });

  // Update all data-i18n-html elements (HTML markup)
  document.querySelectorAll('[data-i18n-html]').forEach(el => {
    const key = el.getAttribute('data-i18n-html');
    if (d[key] !== undefined) {
      el.innerHTML = d[key];
    }
  });

  // Update CLI Code Snippet
  const cliCode = document.getElementById('cliSnippetCode');
  if (cliCode && d.cli_code) {
    cliCode.innerText = d.cli_code;
  }

  // Update Theme strings in current theme preview
  updateThemePreviewStrings();

  // Update Dynamic CTA label with OS and Version if present
  updateDownloadLabels();
}

function toggleLanguage() {
  setLanguage(currentLang === 'fr' ? 'en' : 'fr');
}

// -------------------------------------------------------------
// 2. OS Detection
// -------------------------------------------------------------
function detectUserOS() {
  const ua = window.navigator.userAgent.toLowerCase();
  detectedOS = (ua.indexOf('linux') !== -1) ? 'linux' : 'windows';
  const icon = document.getElementById('heroOsIcon');
  if (icon && typeof lucide !== 'undefined') {
    icon.setAttribute('data-lucide', detectedOS === 'linux' ? 'package' : 'monitor');
    lucide.createIcons();
  }
  updateDownloadLabels();
}

function updateDownloadLabels() {
  const primaryBtn = document.getElementById('heroPrimaryDownloadBtn');
  const label = document.getElementById('heroPrimaryDownloadLabel');
  if (typeof i18nData === 'undefined') return;
  const d = i18nData[currentLang];
  if (!d) return;

  if (primaryBtn && label) {
    if (fetchedVersion) {
      if (detectedOS === 'linux') {
        label.innerText = currentLang === 'en'
          ? `Download for Linux (.deb - ${fetchedVersion})`
          : `Télécharger pour Linux (.deb - ${fetchedVersion})`;
      } else {
        label.innerText = currentLang === 'en'
          ? `Download for Windows (.exe - ${fetchedVersion})`
          : `Télécharger pour Windows (.exe - ${fetchedVersion})`;
      }
    } else {
      label.innerText = (detectedOS === 'linux') ? d.hero_download_linux : d.hero_download_win;
    }
  }
}

// -------------------------------------------------------------
// 3. GitHub Releases Fetch
// -------------------------------------------------------------
async function loadGitHubReleaseInfo() {
  const repo = 'cloud0259/CassiniFlow';
  try {
    const response = await fetch(`https://api.github.com/repos/${repo}/releases/latest`);
    if (!response.ok) return;
    const data = await response.json();

    if (data.tag_name) {
      const version = data.tag_name.startsWith('v') ? data.tag_name : 'v' + data.tag_name;
      fetchedVersion = version;

      const headerBadge = document.getElementById('headerVersionBadge');
      const heroBadge = document.getElementById('heroBadgeText');
      const releaseTag = document.getElementById('releaseVersionTag');

      if (headerBadge) headerBadge.innerText = version;
      if (releaseTag) releaseTag.innerText = version;
      if (heroBadge) {
        heroBadge.innerText = (currentLang === 'en')
          ? `CassiniFlow ${version} — Latest stable release available`
          : `CassiniFlow ${version} — Dernière version stable disponible`;
      }

      updateDownloadLabels();
    }

    if (data.published_at) {
      const date = new Date(data.published_at);
      const dateEl = document.getElementById('releasePublishDate');
      if (dateEl) {
        dateEl.innerText = date.toLocaleDateString(currentLang === 'en' ? 'en-US' : 'fr-FR', { month: 'long', year: 'numeric' });
      }
    }

    if (data.assets && Array.isArray(data.assets)) {
      data.assets.forEach(asset => {
        const name = asset.name.toLowerCase();
        const url = asset.browser_download_url;

        if (name.includes('setup') && name.endsWith('.exe')) {
          const el = document.getElementById('linkWinSetup');
          if (el) el.href = url;
        } else if (name.includes('portable') && name.endsWith('.zip')) {
          const el = document.getElementById('linkWinZip');
          if (el) el.href = url;
        } else if (name.endsWith('.deb')) {
          const el = document.getElementById('linkLinuxDeb');
          if (el) el.href = url;
        } else if (name.endsWith('.tar.gz')) {
          const el = document.getElementById('linkLinuxTar');
          if (el) el.href = url;
        }
      });
    }
  } catch (err) {
    console.warn('Note: GitHub Releases API not reachable or rate-limited.', err);
  }
}

// -------------------------------------------------------------
// 4. Interactive Theme Switcher Preview
// -------------------------------------------------------------
const themeConfigs = {
  dark: {
    img: 'assets/screenshots/cassiniflow_screen_dark.png',
    dotColor: 'bg-blue-500 shadow-glow',
    containerBg: 'bg-space-850',
    borderColor: 'border-space-600',
    textColor: 'text-white'
  },
  night: {
    img: 'assets/screenshots/cassiniflow_screen_night.png',
    dotColor: 'bg-red-600 shadow-glow-night',
    containerBg: 'bg-[#150505]',
    borderColor: 'border-red-950',
    textColor: 'text-red-200'
  },
  light: {
    img: 'assets/screenshots/cassiniflow_screen_light.png',
    dotColor: 'bg-blue-600',
    containerBg: 'bg-slate-100',
    borderColor: 'border-slate-300',
    textColor: 'text-slate-900'
  }
};

function updateThemePreviewStrings() {
  if (typeof i18nData === 'undefined') return;
  const d = i18nData[currentLang];
  if (!d) return;

  const title = document.getElementById('themeTitle');
  const desc = document.getElementById('themeDescription');
  if (title) title.innerText = d[`theme_${currentThemeKey}_title`];
  if (desc) desc.innerText = d[`theme_${currentThemeKey}_desc`];
}

function switchThemePreview(themeKey) {
  currentThemeKey = themeKey;
  const cfg = themeConfigs[themeKey];
  const container = document.getElementById('themePreviewContainer');
  const dot = document.getElementById('themeIndicatorDot');
  const img = document.getElementById('themeScreenshotImg');
  const zoomLink = document.getElementById('themeZoomLink');
  const imgAnchor = document.getElementById('themeImgAnchor');

  updateThemePreviewStrings();

  // Update image & zoom links
  if (img) {
    img.style.opacity = '0.3';
    setTimeout(() => {
      img.src = cfg.img;
      if (zoomLink) zoomLink.href = cfg.img;
      if (imgAnchor) imgAnchor.href = cfg.img;
      img.style.opacity = '1';
    }, 150);
  }

  // Adjust container styles
  const title = document.getElementById('themeTitle');
  if (title) title.className = `text-sm sm:text-base font-bold ${cfg.textColor}`;
  if (container) container.className = `max-w-6xl mx-auto rounded-2xl p-4 sm:p-6 border ${cfg.borderColor} ${cfg.containerBg} shadow-2xl transition-all duration-300 group`;
  if (dot) dot.className = `w-3.5 h-3.5 rounded-full ${cfg.dotColor}`;

  // Update button active styles
  ['dark', 'night', 'light'].forEach(t => {
    const btn = document.getElementById(`btnTheme${t.charAt(0).toUpperCase() + t.slice(1)}`);
    if (btn) {
      if (t === themeKey) {
        btn.className = 'px-4 py-2 text-xs font-semibold rounded-lg bg-accent text-white transition-all shadow-sm';
      } else {
        btn.className = 'px-4 py-2 text-xs font-semibold rounded-lg text-slate-400 hover:text-white transition-all';
      }
    }
  });
}

// -------------------------------------------------------------
// 5. Language Initialization
// -------------------------------------------------------------
function initLanguage() {
  const urlParams = new URLSearchParams(window.location.search);
  const urlLang = urlParams.get('lang');
  const savedLang = localStorage.getItem('cassini_lang');
  const browserLang = (navigator.language || navigator.userLanguage || '').toLowerCase();

  if (urlLang === 'en' || urlLang === 'fr') {
    setLanguage(urlLang);
  } else if (savedLang === 'en' || savedLang === 'fr') {
    setLanguage(savedLang);
  } else if (browserLang.startsWith('en')) {
    setLanguage('en');
  } else {
    setLanguage('fr');
  }
}

// -------------------------------------------------------------
// 6. Application Startup
// -------------------------------------------------------------
function startApp() {
  detectUserOS();
  initLanguage();
  if (typeof lucide !== 'undefined') {
    lucide.createIcons();
  }
  loadGitHubReleaseInfo();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', startApp);
} else {
  startApp();
}

