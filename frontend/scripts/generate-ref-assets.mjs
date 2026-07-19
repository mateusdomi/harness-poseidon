/**
 * Gera os assets locais de referência visual (frontend/public/refs/*.png)
 * usados pelas fixtures de protótipos. PNGs RGB simples, sem dependências:
 * codificação PNG manual (IHDR/IDAT/IEND + zlib do Node).
 *
 * Uso: node scripts/generate-ref-assets.mjs
 */
import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'refs');

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

/** Codifica um framebuffer RGB (width*height*3) como PNG. */
function encodePng(width, height, rgb) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // color type: truecolor RGB
  const raw = Buffer.alloc(height * (1 + width * 3));
  for (let y = 0; y < height; y += 1) {
    raw[y * (1 + width * 3)] = 0; // filtro: none
    rgb.copy(raw, y * (1 + width * 3) + 1, y * width * 3, (y + 1) * width * 3);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** Canvas RGB mínimo: fill + rect. */
function canvas(width, height, [r, g, b]) {
  const buffer = Buffer.alloc(width * height * 3);
  for (let i = 0; i < width * height; i += 1) {
    buffer[i * 3] = r;
    buffer[i * 3 + 1] = g;
    buffer[i * 3 + 2] = b;
  }
  return {
    width,
    height,
    buffer,
    rect(x, y, w, h, [rr, gg, bb]) {
      for (let yy = Math.max(0, y); yy < Math.min(height, y + h); yy += 1) {
        for (let xx = Math.max(0, x); xx < Math.min(width, x + w); xx += 1) {
          const i = (yy * width + xx) * 3;
          buffer[i] = rr;
          buffer[i + 1] = gg;
          buffer[i + 2] = bb;
        }
      }
    },
    png() {
      return encodePng(width, height, buffer);
    },
  };
}

const W = 480;
const H = 300;

const DARK_BG = [11, 11, 18];
const SURFACE = [23, 23, 31];
const ELEVATED = [33, 33, 46];
const BORDER = [52, 52, 68];
const BRAND = [124, 92, 252];
const MAGENTA = [201, 50, 132];
const ACCENT = [182, 245, 66];
const MUTED = [166, 173, 187];
const ERROR = [248, 113, 113];
const WARNING = [251, 191, 36];
const SUCCESS = [52, 211, 153];

/** kanban-denso: 4 colunas com cards densos. */
function kanban() {
  const c = canvas(W, H, DARK_BG);
  const colW = 108;
  const gap = 12;
  for (let col = 0; col < 4; col += 1) {
    const x = 12 + col * (colW + gap);
    c.rect(x, 12, colW, H - 24, SURFACE);
    c.rect(x, 12, colW, 18, ELEVATED); // cabeçalho da coluna
    const cards = [4, 6, 3, 5][col];
    for (let row = 0; row < cards; row += 1) {
      const y = 40 + row * 40;
      c.rect(x + 8, y, colW - 16, 32, ELEVATED);
      c.rect(x + 8, y, 4, 32, [BRAND, ACCENT, MAGENTA, WARNING][(col + row) % 4]);
    }
  }
  return c.png();
}

/** paleta-dark: swatches da paleta dark de telemetria. */
function paleta() {
  const c = canvas(W, H, DARK_BG);
  const swatches = [BRAND, MAGENTA, ACCENT, MUTED, SUCCESS, WARNING, ERROR, ELEVATED];
  const sw = W / swatches.length;
  swatches.forEach((color, i) => {
    c.rect(i * sw + 4, 40, sw - 8, 140, color);
    c.rect(i * sw + 4, 190, sw - 8, 12, BORDER); // régua de legenda
  });
  c.rect(0, 0, W, 24, SURFACE); // barra de título
  return c.png();
}

/** cockpit-mock: header + grid de cards de telemetria. */
function cockpit() {
  const c = canvas(W, H, DARK_BG);
  c.rect(0, 0, W, 40, SURFACE); // header
  c.rect(16, 12, 120, 16, BRAND); // logo/título
  const cardW = 140;
  const cardH = 100;
  for (let row = 0; row < 2; row += 1) {
    for (let col = 0; col < 3; col += 1) {
      const x = 16 + col * (cardW + 16);
      const y = 56 + row * (cardH + 16);
      c.rect(x, y, cardW, cardH, SURFACE);
      c.rect(x + 10, y + 10, cardW - 60, 10, MUTED);
      c.rect(x + 10, y + 30, cardW - 20, 40, ELEVATED);
      c.rect(x + 10, y + cardH - 20, 40, 10, [ACCENT, BRAND, MAGENTA][col]);
    }
  }
  return c.png();
}

/** aprovacoes-figma: wireframe simples de fila de aprovações. */
function aprovacoes() {
  const c = canvas(W, H, [244, 245, 250]); // fundo claro (Figma)
  c.rect(0, 0, 72, H, [255, 255, 255]); // sidebar
  c.rect(10, 12, 52, 10, [124, 92, 252]);
  for (let i = 0; i < 5; i += 1) c.rect(10, 36 + i * 20, 52, 8, [210, 213, 224]);
  c.rect(72, 0, W - 72, 36, [255, 255, 255]); // header
  for (let i = 0; i < 4; i += 1) {
    const y = 52 + i * 58;
    c.rect(92, y, W - 120, 46, [255, 255, 255]); // linha da fila
    c.rect(92, y, 4, 46, [201, 50, 132]); // indicador de pendência
    c.rect(106, y + 10, 180, 8, [120, 126, 148]);
    c.rect(106, y + 26, 120, 6, [190, 194, 208]);
    c.rect(W - 120 - 70, y + 12, 30, 22, [182, 245, 66]); // aprovar
    c.rect(W - 120 - 34, y + 12, 30, 22, [248, 113, 113]); // rejeitar
  }
  return c.png();
}

mkdirSync(OUT_DIR, { recursive: true });
const assets = {
  'kanban-denso.png': kanban(),
  'paleta-dark.png': paleta(),
  'cockpit-mock.png': cockpit(),
  'aprovacoes-figma.png': aprovacoes(),
};
for (const [name, data] of Object.entries(assets)) {
  writeFileSync(join(OUT_DIR, name), data);
  console.log(`✓ public/refs/${name} (${data.length} bytes)`);
}
