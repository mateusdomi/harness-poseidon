/** @type {import('tailwindcss').Config} */
export default {
  darkMode: ['selector', '[data-theme="dark"]'],
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    screens: {
      md: '768px',
      lg: '1024px',
      xl: '1440px',
    },
    extend: {
      colors: {
        background: 'var(--color-background)',
        surface: 'var(--color-surface)',
        'surface-elevated': 'var(--color-surface-elevated)',
        'surface-hover': 'var(--color-surface-hover)',
        foreground: 'var(--color-foreground)',
        'foreground-muted': 'var(--color-foreground-muted)',
        border: 'var(--color-border)',
        'border-strong': 'var(--color-border-strong)',
        brand: {
          DEFAULT: 'var(--color-brand)',
          magenta: 'var(--color-brand-magenta)',
          strong: 'var(--color-brand-strong)',
        },
        primary: {
          DEFAULT: 'var(--color-primary)',
          foreground: 'var(--color-primary-foreground)',
          hover: 'var(--color-primary-hover)',
          active: 'var(--color-primary-active)',
        },
        accent: {
          DEFAULT: 'var(--color-accent)',
          foreground: 'var(--color-accent-foreground)',
        },
        success: {
          DEFAULT: 'var(--color-success)',
          foreground: 'var(--color-success-foreground)',
        },
        warning: {
          DEFAULT: 'var(--color-warning)',
          foreground: 'var(--color-warning-foreground)',
        },
        error: {
          DEFAULT: 'var(--color-error)',
          foreground: 'var(--color-error-foreground)',
        },
        info: {
          DEFAULT: 'var(--color-info)',
          foreground: 'var(--color-info-foreground)',
        },
      },
      backgroundImage: {
        'gradient-brand': 'var(--gradient-brand)',
        'gradient-primary': 'var(--gradient-primary)',
        'gradient-ambient': 'var(--gradient-ambient)',
      },
      fontFamily: {
        // As faces de marca (Space Grotesk / Inter) só são aplicadas se
        // presentes no sistema; caso contrário caem para stacks nativos ricos.
        // CSP proíbe fontes externas — nada de CDN.
        heading: [
          '"Space Grotesk"',
          'ui-rounded',
          '"SF Pro Display"',
          '"Segoe UI"',
          'system-ui',
          'sans-serif',
        ],
        sans: ['Inter', 'system-ui', '-apple-system', '"Segoe UI"', 'Roboto', 'sans-serif'],
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'Consolas', 'monospace'],
      },
      fontSize: {
        // Hierarquia mais marcante: display com leading justo e tracking negativo.
        display: ['2.25rem', { lineHeight: '1.1', letterSpacing: '-0.02em', fontWeight: '700' }],
      },
      letterSpacing: {
        tightest: '-0.02em',
      },
      borderRadius: {
        DEFAULT: 'var(--radius-md)',
        sm: 'var(--radius-sm)',
        md: 'var(--radius-md)',
        lg: 'var(--radius-lg)',
        xl: 'var(--radius-xl)',
      },
      minHeight: {
        touch: '44px',
      },
      minWidth: {
        touch: '44px',
      },
      boxShadow: {
        sm: 'var(--shadow-sm)',
        card: 'var(--shadow-card)',
        lg: 'var(--shadow-lg)',
        glow: 'var(--shadow-glow)',
      },
      transitionDuration: {
        fast: 'var(--duration-fast)',
        base: 'var(--duration-base)',
      },
      keyframes: {
        'fade-in': {
          from: { opacity: '0' },
          to: { opacity: '1' },
        },
        'rise-in': {
          from: { opacity: '0', transform: 'translateY(6px)' },
          to: { opacity: '1', transform: 'translateY(0)' },
        },
      },
      animation: {
        'fade-in': 'fade-in var(--duration-fast) ease',
        'rise-in': 'rise-in var(--duration-base) ease',
      },
    },
  },
  plugins: [],
};
