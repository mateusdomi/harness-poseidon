import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}

/**
 * Normaliza um nome em um slug de URL: minúsculas, sem acentos, apenas
 * `[a-z0-9-]`, sem hífens duplicados nas bordas. Usado para gerar o
 * "Identificador da URL" automaticamente a partir do nome.
 */
export function slugify(value: string): string {
  return value
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/**
 * Deriva uma sigla técnica (project.key) do nome: MAIÚSCULAS, `[A-Z0-9]`,
 * truncada em 12. Ex.: "Poseidon Frontend" → "POSEIDONFRON".
 */
export function keyify(value: string): string {
  return value
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toUpperCase()
    .replace(/[^A-Z0-9]+/g, '')
    .slice(0, 12);
}
