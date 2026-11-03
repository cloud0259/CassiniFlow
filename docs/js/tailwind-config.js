/**
 * CassiniFlow Web — Tailwind CSS Custom Configuration
 */
tailwind.config = {
  darkMode: 'class',
  theme: {
    extend: {
      fontFamily: {
        sans: ['"Plus Jakarta Sans"', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'monospace'],
      },
      colors: {
        space: {
          950: '#06090E',
          900: '#0B0F19',
          850: '#0F141C',
          800: '#121822',
          750: '#18202C',
          700: '#1E2736',
          600: '#2A374A',
        },
        accent: {
          DEFAULT: '#2563EB',
          hover: '#1D4ED8',
          glow: '#38BDF8',
          gold: '#F59E0B',
          night: '#DC2626',
        }
      },
      boxShadow: {
        glow: '0 0 25px -5px rgba(37, 99, 235, 0.35)',
        'glow-cyan': '0 0 25px -5px rgba(56, 189, 248, 0.35)',
        'glow-night': '0 0 25px -5px rgba(220, 38, 38, 0.45)',
      }
    }
  }
};

