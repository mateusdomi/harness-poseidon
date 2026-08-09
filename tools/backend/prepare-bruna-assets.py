#!/usr/bin/env python3
"""Prepara os assets transparentes da Bruna a partir da foto oficial.

A imagem oficial (frontend/public/people/bruna-magalhaes.jpg) tem fundo real.
Este script remove o fundo com rembg, redimensiona para uso em DPI/Retina e
aplica uma máscara suave nas bordas para que a personagem flutue sobre o desktop.

Uso:
    python3 tools/backend/prepare-bruna-assets.py
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

try:
    from rembg import remove
except ImportError as exc:
    sys.exit(f"rembg é obrigatório: {exc}. Execute: pip install rembg pillow")


REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE = REPO_ROOT / "frontend" / "public" / "people" / "bruna-magalhaes.jpg"
TARGET_DIR = REPO_ROOT / "src" / "Harness.Bruna.Desktop" / "Assets"
TARGET_SIZE = 512  # pixels lógicos; em @2x fica 1024 físicos


def ensure_source() -> None:
    if not SOURCE.exists():
        sys.exit(f"Imagem oficial não encontrada: {SOURCE}")


def remove_background(source: Image.Image) -> Image.Image:
    # rembg espera RGBA de entrada para preservar qualidade.
    return remove(source.convert("RGBA"))


def center_crop_to_square(image: Image.Image) -> Image.Image:
    width, height = image.size
    # A foto oficial é vertical (682x1024). Cortamos um quadrado central
    # focando no rosto/ombros, que funciona melhor como mascote flutuante.
    size = min(width, height)
    left = (width - size) // 2
    top = int((height - size) * 0.12)  # desloca levemente para cima (rosto)
    top = max(0, min(top, height - size))
    return image.crop((left, top, left + size, top + size))


def apply_soft_mask(image: Image.Image) -> Image.Image:
    """Aplica uma máscara oval suave para suavizar as bordas da silhueta."""
    width, height = image.size
    mask = Image.new("L", (width, height), 0)
    # Elipse levemente menor que o quadro para dar uma borda transparenta suave.
    margin = int(width * 0.04)
    draw = ImageDraw.Draw(mask)
    draw.ellipse(
        (margin, margin, width - margin, height - margin),
        fill=255,
    )
    mask = mask.filter(ImageFilter.GaussianBlur(radius=width * 0.03))
    rgba = image.convert("RGBA")
    r, g, b, a = rgba.split()
    a = ImageChops.multiply(a, mask)
    return Image.merge("RGBA", (r, g, b, a))


def save_variants(base: Image.Image) -> None:
    TARGET_DIR.mkdir(parents=True, exist_ok=True)
    base.save(TARGET_DIR / "bruna-idle.png", "PNG")
    # Versão @2x para Retina/HiDPI.
    base.resize((TARGET_SIZE * 2, TARGET_SIZE * 2), Image.LANCZOS).save(
        TARGET_DIR / "bruna-idle@2x.png", "PNG"
    )
    # Thumbnail para uso em tray/menu quando aplicável.
    base.resize((128, 128), Image.LANCZOS).save(
        TARGET_DIR / "bruna-thumbnail.png", "PNG"
    )


def main() -> int:
    ensure_source()
    print(f"Processando {SOURCE} ...")
    source = Image.open(SOURCE)
    print("Removendo fundo (pode levar alguns segundos) ...")
    no_bg = remove_background(source)
    print("Cortando e redimensionando ...")
    cropped = center_crop_to_square(no_bg)
    resized = cropped.resize((TARGET_SIZE, TARGET_SIZE), Image.LANCZOS)
    print("Aplicando máscara suave ...")
    final = apply_soft_mask(resized)
    print(f"Salvando em {TARGET_DIR} ...")
    save_variants(final)
    print("Assets da Bruna prontos.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
