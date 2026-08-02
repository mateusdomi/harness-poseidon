#!/usr/bin/env python3
"""Extrator sem rede para fontes não confiáveis; stdout é um único JSON."""

import json
import os
import subprocess
import sys
import tempfile
import wave
import zipfile
import xml.etree.ElementTree as et

MAX_TEXT = 200_000


def compact(value):
    return " ".join(value.split())[:MAX_TEXT]


def result(status, text):
    print(json.dumps({"status": status, "text": compact(text)}, ensure_ascii=False))


def command(args):
    completed = subprocess.run(args, capture_output=True, text=True, timeout=45, check=False)
    return completed.stdout if completed.returncode == 0 else ""


def docx_text(path):
    with zipfile.ZipFile(path) as archive:
        root = et.fromstring(archive.read("word/document.xml"))
    return " ".join(node.text or "" for node in root.iter() if node.tag.endswith("}t"))


def xlsx_text(path):
    rows = []
    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        shared = []
        if "xl/sharedStrings.xml" in names:
            root = et.fromstring(archive.read("xl/sharedStrings.xml"))
            shared = [
                "".join(node.text or "" for node in item.iter() if node.tag.endswith("}t"))
                for item in root
            ]
        for name in sorted(value for value in names if value.startswith("xl/worksheets/sheet") and value.endswith(".xml")):
            root = et.fromstring(archive.read(name))
            for row in (node for node in root.iter() if node.tag.endswith("}row")):
                values = []
                for cell in (node for node in row if node.tag.endswith("}c")):
                    kind = cell.attrib.get("t")
                    if kind == "inlineStr":
                        raw = "".join(
                            node.text or "" for node in cell.iter() if node.tag.endswith("}t")
                        )
                    else:
                        raw = next(
                            (node.text or "" for node in cell if node.tag.endswith("}v")), ""
                        )
                    if kind == "s" and raw.isdigit() and int(raw) < len(shared):
                        raw = shared[int(raw)]
                    values.append(raw)
                if values:
                    rows.append(" | ".join(values))
    return "\n".join(rows)


def zip_text(path):
    chunks = []
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist()[:512]:
            chunks.append(f"Arquivo: {info.filename}")
            if os.path.splitext(info.filename)[1].lower() in {".txt", ".md", ".csv", ".json"}:
                chunks.append(archive.read(info)[:MAX_TEXT].decode("utf-8", errors="replace"))
    return "\n".join(chunks)


def pdf_text(path):
    text = command(["pdftotext", "-layout", path, "-"])
    if text.strip():
        return text
    with tempfile.TemporaryDirectory(dir="/tmp") as directory:
        prefix = os.path.join(directory, "page")
        converted = subprocess.run(
            ["pdftoppm", "-f", "1", "-l", "10", "-png", "-r", "150", path, prefix],
            capture_output=True,
            timeout=45,
            check=False,
        )
        if converted.returncode != 0:
            return ""
        return "\n".join(
            command(["tesseract", os.path.join(directory, name), "stdout", "-l", "por+eng"])
            for name in sorted(os.listdir(directory))
            if name.endswith(".png")
        )


def audio_text(path):
    """Normaliza áudio e transcreve localmente; nenhum byte deixa a sandbox."""
    from vosk import KaldiRecognizer, Model, SetLogLevel

    SetLogLevel(-1)
    with tempfile.TemporaryDirectory(dir="/tmp") as directory:
        wav_path = os.path.join(directory, "audio.wav")
        converted = subprocess.run(
            [
                "ffmpeg", "-nostdin", "-v", "error", "-y", "-i", path,
                "-t", "180", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav_path,
            ],
            capture_output=True,
            timeout=45,
            check=False,
        )
        if converted.returncode != 0:
            return ""

        model = Model("/opt/harness/models/vosk-pt")
        transcript = []
        with wave.open(wav_path, "rb") as source:
            recognizer = KaldiRecognizer(model, source.getframerate())
            while chunk := source.readframes(8000):
                if recognizer.AcceptWaveform(chunk):
                    value = json.loads(recognizer.Result()).get("text", "")
                    if value:
                        transcript.append(value)
            final = json.loads(recognizer.FinalResult()).get("text", "")
            if final:
                transcript.append(final)
        return " ".join(transcript)


def main():
    path, content_type = sys.argv[1], sys.argv[2]
    if content_type.startswith("text/") or content_type == "application/json":
        text = open(path, "rb").read(MAX_TEXT).decode("utf-8", errors="replace")
    elif content_type == "application/pdf":
        text = pdf_text(path)
    elif content_type in {"image/png", "image/jpeg", "image/webp", "image/gif"}:
        text = command(["tesseract", path, "stdout", "-l", "por+eng"])
    elif content_type == "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
        text = docx_text(path)
    elif content_type == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet":
        text = xlsx_text(path)
    elif content_type in {"application/zip", "application/x-zip-compressed"}:
        text = zip_text(path)
    elif content_type in {"audio/mpeg", "audio/wav", "audio/ogg", "audio/mp4"}:
        text = audio_text(path)
    else:
        result("stored_not_interpreted", "Fonte armazenada, mas este tipo ainda não possui extrator.")
        return
    if text.strip():
        result("extracted", text)
    else:
        result("stored_not_interpreted", "Fonte armazenada, mas nenhum conteúdo legível foi extraído.")


try:
    main()
except Exception:
    result("extraction_failed", "Fonte armazenada, mas a extração falhou de forma segura.")
