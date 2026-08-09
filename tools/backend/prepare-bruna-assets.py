#!/usr/bin/env python3
"""Prepara os assets transparentes da Bruna a partir da foto oficial.

A imagem padrão é frontend/public/people/bruna-magalhaes.jpg. Também é possível
passar uma foto customizada via --source (por exemplo, a foto atualizada do
perfil de liderança). O script remove o fundo com rembg, redimensiona para uso
em DPI/Retina e aplica uma máscara suave nas bordas para que a personagem flutue
sobre o desktop.

Uso:
    python3 tools/backend/prepare-bruna-assets.py
    python3 tools/backend/prepare-bruna-assets.py --source /caminho/nova-bruna.jpg
    python3 tools/backend/prepare-bruna-assets.py --auto --data-dir ~/.harness-poseidon
"""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

try:
    from rembg import remove
except ImportError as exc:
    sys.exit(f"rembg é obrigatório: {exc}. Execute: pip install rembg pillow")


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SOURCE = REPO_ROOT / "frontend" / "public" / "people" / "bruna-magalhaes.jpg"
TARGET_DIR = REPO_ROOT / "src" / "Harness.Bruna.Desktop" / "Assets"
TARGET_SIZE = 512  # pixels lógicos; em @2x fica 1024 físicos


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Prepara assets transparentes da Bruna.")
    parser.add_argument(
        "--source",
        type=Path,
        help="Caminho da foto de origem (padrao: frontend/public/people/bruna-magalhaes.jpg)",
    )
    parser.add_argument(
        "--auto",
        action="store_true",
        help="Procura uma foto customizada em <data-dir>/assets/bruna-magalhaes.*",
    )
    parser.add_argument(
        "--data-dir",
        type=Path,
        default=Path.home() / ".harness-poseidon",
        help="Diretorio de dados do Poseidon (usado com --auto)",
    )
    return parser.parse_args()


def resolve_source(args: argparse.Namespace) -> Path:
    if args.source:
        return args.source

    if args.auto:
        assets_dir = args.data_dir / "assets"
        for extension in (".png", ".jpg", ".jpeg", ".webp"):
            candidate = assets_dir / f"bruna-magalhaes{extension}"
            if candidate.exists():
                return candidate

    return DEFAULT_SOURCE


def ensure_source(path: Path) -> None:
    if not path.exists():
        sys.exit(f"Imagem de origem não encontrada: {path}")


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
    args = parse_args()
    source_path = resolve_source(args)
    ensure_source(source_path)
    print(f"Processando {source_path} ...")
    source = Image.open(source_path)
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
