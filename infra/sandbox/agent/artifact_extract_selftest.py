#!/usr/bin/env python3
"""Regressão executável do extrator empacotado; não usa rede nem fixtures externas."""

import json
import os
import subprocess
import tempfile
import zipfile

EXTRACTOR = "/opt/harness/artifact_extract.py"


def extract(path, content_type):
    completed = subprocess.run(
        ["python3", EXTRACTOR, path, content_type],
        capture_output=True,
        text=True,
        timeout=50,
        check=True,
    )
    return json.loads(completed.stdout)


def require(path, content_type, expected):
    result = extract(path, content_type)
    if result["status"] != "extracted" or expected not in result["text"]:
        raise AssertionError(f"{content_type}: {result}")


def pdf_bytes(text):
    safe = text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    stream = f"BT /F1 18 Tf 72 720 Td ({safe}) Tj ET".encode("ascii")
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
    ]
    output = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for number, body in enumerate(objects, start=1):
        offsets.append(len(output))
        output.extend(f"{number} 0 obj\n".encode("ascii"))
        output.extend(body)
        output.extend(b"\nendobj\n")
    xref = len(output)
    output.extend(f"xref\n0 {len(objects) + 1}\n".encode("ascii"))
    output.extend(b"0000000000 65535 f \n")
    for offset in offsets[1:]:
        output.extend(f"{offset:010d} 00000 n \n".encode("ascii"))
    output.extend(
        f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode(
            "ascii"
        )
    )
    return bytes(output)


with tempfile.TemporaryDirectory(dir="/tmp") as directory:
    text_path = os.path.join(directory, "source.txt")
    with open(text_path, "w", encoding="utf-8") as stream:
        stream.write("REGRA-TEXTO-7319")
    require(text_path, "text/plain", "REGRA-TEXTO-7319")

    docx_path = os.path.join(directory, "source.docx")
    with zipfile.ZipFile(docx_path, "w") as archive:
        archive.writestr(
            "word/document.xml",
            '<w:document xmlns:w="urn:w"><w:body><w:p><w:r><w:t>REGRA-DOCX-7319</w:t></w:r></w:p></w:body></w:document>',
        )
    require(
        docx_path,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "REGRA-DOCX-7319",
    )

    xlsx_path = os.path.join(directory, "source.xlsx")
    with zipfile.ZipFile(xlsx_path, "w") as archive:
        archive.writestr(
            "xl/sharedStrings.xml",
            '<sst xmlns="urn:s"><si><t>REGRA-XLSX-7319</t></si></sst>',
        )
        archive.writestr(
            "xl/worksheets/sheet1.xml",
            '<worksheet xmlns="urn:s"><sheetData><row>'
            '<c t="s"><v>0</v></c>'
            '<c t="inlineStr"><is><t>REGRA-INLINE-XLSX-7319</t></is></c>'
            '</row></sheetData></worksheet>',
        )
    require(
        xlsx_path,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "REGRA-INLINE-XLSX-7319",
    )

    pdf_path = os.path.join(directory, "source.pdf")
    with open(pdf_path, "wb") as stream:
        stream.write(pdf_bytes("REGRA PDF 7319"))
    require(pdf_path, "application/pdf", "REGRA PDF 7319")

    subprocess.run(
        ["pdftocairo", "-singlefile", "-png", "-r", "180", pdf_path, os.path.join(directory, "ocr")],
        capture_output=True,
        timeout=45,
        check=True,
    )
    require(os.path.join(directory, "ocr.png"), "image/png", "REGRA PDF 7319")

    audio_path = os.path.join(directory, "source.wav")
    subprocess.run(
        ["espeak-ng", "-v", "pt-br", "-s", "135", "-w", audio_path,
         "poseidon controla empréstimos de equipamentos"],
        capture_output=True,
        timeout=20,
        check=True,
    )
    require(audio_path, "audio/wav", "equipamentos")

print("artifact extractor self-test: text, PDF, DOCX, XLSX, image and audio extracted")
