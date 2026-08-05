using System.IO.Compression;
using System.Text;
using Harness.Modules.Coordination.Domain;

namespace Harness.UnitTests.Coordination;

public sealed class AttachmentIngestPolicyTests
{
    [Fact]
    public void AcceptsRegularDocumentsInsideTheAllowlist()
    {
        foreach (var (name, type, content) in (ReadOnlySpan<(string, string, byte[])>)
        [
            ("requisitos.md", "text/markdown", Encoding.UTF8.GetBytes("requisitos")),
            ("planilha.csv", "text/csv", Encoding.UTF8.GetBytes("a,b")),
            ("captura.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        ])
        {
            var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                name,
                type,
                content));
            Assert.True(decision.Accepted, $"{name} deveria ser aceito: {decision.Detail}");
        }
    }

    [Theory]
    [InlineData("foto.png", "image/png")]
    [InlineData("contrato.pdf", "application/pdf")]
    [InlineData("gravacao.wav", "audio/wav")]
    public void RejectsAFileWhoseBytesDoNotMatchItsDeclaredFormat(string name, string type)
    {
        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            name, type, Encoding.UTF8.GetBytes("não é o formato declarado")));

        Assert.False(decision.Accepted);
        Assert.Equal("signature_mismatch", decision.Code);
    }

    [Fact]
    public void OfficeArchivesMustContainTheirCanonicalStructure()
    {
        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "falso.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            BuildZip(("qualquer.txt", Encoding.UTF8.GetBytes("x")))));

        Assert.Equal("office_structure_invalid", decision.Code);
    }

    [Fact]
    public void RejectsPathTraversalFileNames()
    {
        foreach (var name in (string[])
        [
            "../evil.md", "..\\evil.md", "nested/evil.md", "/etc/passwd.md", "controle\u0000.md",
        ])
        {
            var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                name,
                "text/markdown",
                Encoding.UTF8.GetBytes("x")));
            Assert.False(decision.Accepted);
            Assert.Equal("path_traversal", decision.Code);
        }
    }

    [Fact]
    public void RejectsExecutableExtensionsAndUnsupportedTypes()
    {
        var executable = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "payload.exe", "application/octet-stream", Encoding.UTF8.GetBytes("x")));
        Assert.Equal("executable_extension", executable.Code);
        var unsupported = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "notes.tar", "application/x-tar", Encoding.UTF8.GetBytes("x")));
        Assert.Equal("unsupported_type", unsupported.Code);
    }

    [Fact]
    public void RejectsExecutableSignaturesDisguisedAsDocuments()
    {
        foreach (var magic in (byte[][])
        [
            [0x4D, 0x5A, 0x90, 0x00],
            [0x7F, 0x45, 0x4C, 0x46],
            [0xCF, 0xFA, 0xED, 0xFE],
            [(byte)'#', (byte)'!', (byte)'/', (byte)'b'],
        ])
        {
            var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                "relatorio.pdf", "application/pdf", magic));
            Assert.False(decision.Accepted);
            Assert.Equal("executable_content", decision.Code);
        }
    }

    [Fact]
    public void RejectsOversizedAndEmptyContent()
    {
        Assert.Equal(
            "empty_content",
            AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                "vazio.md", "text/markdown", ReadOnlyMemory<byte>.Empty)).Code);
        Assert.Equal(
            "size_exceeded",
            AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                "grande.md",
                "text/markdown",
                new byte[AttachmentIngestPolicy.MaximumSizeBytes + 1])).Code);
    }

    [Fact]
    public void RejectsZipBombByCompressionRatio()
    {
        var zip = BuildZip(("zeros.txt", new byte[8 * 1024 * 1024]));
        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "bomba.zip", "application/zip", zip));
        Assert.False(decision.Accepted);
        Assert.Equal("zip_bomb", decision.Code);
    }

    [Fact]
    public void RejectsZipWithTraversalNestedArchiveOrExecutableEntry()
    {
        Assert.Equal(
            "zip_path_traversal",
            AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                "traversal.zip",
                "application/zip",
                BuildZip(("../escape.txt", Encoding.UTF8.GetBytes("x"))))).Code);
        Assert.Equal(
            "zip_blocked_entry",
            AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                "nested.zip",
                "application/zip",
                BuildZip(("inner.zip", Encoding.UTF8.GetBytes("PK"))))).Code);
        Assert.Equal(
            "zip_executable_entry",
            AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
                "sneaky.zip",
                "application/zip",
                BuildZip(("tool.txt", [0x4D, 0x5A, 0x00, 0x01])))).Code);
    }

    [Fact]
    public void AcceptsLegitimateZipWithDocuments()
    {
        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "docs.zip",
            "application/zip",
            BuildZip(
                ("leiame.md", Encoding.UTF8.GetBytes("# Documento com conteúdo variado 1234567890")),
                ("dados.csv", Encoding.UTF8.GetBytes("a,b,c\n1,2,3")))));
        Assert.True(decision.Accepted, decision.Detail);
    }

    [Fact]
    public void RejectsCorruptedZip()
    {
        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "quebrado.zip", "application/zip", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x01 }));
        Assert.Equal("zip_corrupted", decision.Code);
    }

    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// A regressão do intake do Prisma (2026-08-05): o ZIP do protótipo React caiu no
    /// `eslint.config.js`, porque a blocklist de anexo direto era aplicada a conteúdo de ZIP.
    /// Código-fonte é o payload LEGÍTIMO de um projeto fornecido; o que continua bloqueado dentro
    /// do ZIP é binário nativo e ZIP aninhado.
    /// </summary>
    [Fact]
    public void ZipDeProjetoComCodigoFonteEAceito()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
            buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            Add("app/eslint.config.js", "export default [];");
            Add("app/vite.config.mjs", "export default {};");
            Add("app/src/App.tsx", "export default function App() { return null; }");
            Add("app/tools/build.sh", "#!/bin/sh\necho build");
        }

        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "prototipo.zip", "application/zip", buffer.ToArray()));

        Assert.True(decision.Accepted, decision.Detail);
    }

    [Fact]
    public void BinarioNativoDentroDoZipContinuaBloqueado()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
            buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("bin/ferramenta.dll");
            using var writer = entry.Open();
            writer.Write("nao importa o conteudo"u8);
        }

        var decision = AttachmentIngestPolicy.Evaluate(new AttachmentIngestRequest(
            "prototipo.zip", "application/zip", buffer.ToArray()));

        Assert.False(decision.Accepted);
        Assert.Equal("zip_blocked_entry", decision.Code);
    }
}
