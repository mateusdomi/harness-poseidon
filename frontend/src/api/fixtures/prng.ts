import type { Ulid } from '../contracts';

/** Seed fixa das fixtures — dados 100% determinísticos entre execuções. */
export const FIXTURE_SEED = 42;

/** PRNG mulberry32: pequeno, rápido e determinístico. */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

function encodeBase32(value: number, length: number, random: () => number): string {
  let out = '';
  let v = value;
  for (let i = 0; i < length; i += 1) {
    out = CROCKFORD[v % 32] + out;
    v = Math.floor(v / 32);
  }
  // Completa com entropia do PRNG se o valor não preencher tudo.
  while (out.length < length) out = CROCKFORD[Math.floor(random() * 32)] + out;
  return out.slice(-length);
}

/**
 * Gerador de ULID determinístico: parte temporal vem de um relógio base
 * monotônico (base + contador) e a parte aleatória do PRNG com seed.
 */
export class DeterministicUlidGenerator {
  #random: () => number;
  #counter = 0;
  readonly #baseTimeMs: number;

  constructor(seed: number = FIXTURE_SEED, baseTimeMs: number = Date.UTC(2026, 5, 1)) {
    this.#random = mulberry32(seed);
    this.#baseTimeMs = baseTimeMs;
  }

  next(): Ulid {
    const time = this.#baseTimeMs + this.#counter;
    this.#counter += 1;
    const timePart = encodeBase32(time, 10, this.#random);
    let randomPart = '';
    for (let i = 0; i < 16; i += 1) {
      randomPart += CROCKFORD[Math.floor(this.#random() * 32)];
    }
    return `${timePart}${randomPart}`;
  }
}

/** Inteiro em [min, max] usando o PRNG. */
export function int(random: () => number, min: number, max: number): number {
  return min + Math.floor(random() * (max - min + 1));
}

/** Escolhe um elemento do array usando o PRNG. */
export function pick<T>(random: () => number, items: readonly T[]): T {
  return items[Math.floor(random() * items.length)];
}

/** Relógio determinístico: cada chamada avança `stepMinutes` a partir da base. */
export function createTickClock(
  baseIso: string = '2026-07-01T12:00:00Z',
  stepMinutes = 37,
): () => string {
  let ticks = 0;
  const base = Date.parse(baseIso);
  return () => {
    ticks += 1;
    return new Date(base + ticks * stepMinutes * 60_000).toISOString();
  };
}
