using Harness.Host.WorkBoard;

namespace Harness.UnitTests.WorkBoard;

/// <summary>
/// A proveniência do artefato fornecido. O caso que a motivou: o ZIP do protótipo React chegava ao
/// runtime como "um anexo qualquer", e nada dizia ao card de frontend que a interface EXISTE e deve
/// ser evoluída — reconstruir do zero era o caminho de menor resistência.
/// </summary>
public sealed class AttachmentRolesTests
{
    [Theory]
    [InlineData("provided_frontend", "qualquer.bin", "provided_frontend")]
    [InlineData("requirements_source", "x.txt", "requirements_source")]
    [InlineData("design_reference", "y.png", "design_reference")]
    [InlineData("supporting_document", "z.pdf", "supporting_document")]
    [InlineData("PROVIDED_FRONTEND", "a.zip", "provided_frontend")]
    public void PapelDeclaradoValeMesmoQueAFormaSugiraOutro(string declarado, string arquivo, string esperado)
    {
        Assert.Equal(esperado, AttachmentRoles.Normalize(declarado, arquivo));
    }

    /// <summary>O caso do Lovable: ZIP sem declaração é protótipo de interface fornecido.</summary>
    [Fact]
    public void ZipSemDeclaracaoEInferidoComoFrontendFornecido()
    {
        Assert.Equal(
            AttachmentRoles.ProvidedFrontend,
            AttachmentRoles.Normalize(null, "bright-vision-interface-main.zip"));
    }

    [Theory]
    [InlineData("Prisma_Especificacao_Tecnica_MVP.md")]
    [InlineData("requisitos-do-sistema.pdf")]
    [InlineData("spec-v3.md")]
    public void DocumentoDeRequisitosEInferidoComoFonteDeRequisitos(string arquivo)
    {
        Assert.Equal(
            AttachmentRoles.RequirementsSource, AttachmentRoles.Normalize(null, arquivo));
    }

    /// <summary>
    /// A inferência é estreita de propósito: proveniência adivinhada em excesso vira ruído com
    /// aparência de metadado. O que não se reconhece é `other`, nunca erro.
    /// </summary>
    [Theory]
    [InlineData("foto-da-reuniao.png")]
    [InlineData("notas.md")]
    [InlineData("planilha.xlsx")]
    [InlineData("audio.mp3")]
    public void FormaNaoReconhecidaCaiEmOtherSemErro(string arquivo)
    {
        Assert.Equal(AttachmentRoles.Other, AttachmentRoles.Normalize(null, arquivo));
    }

    [Fact]
    public void PapelDesconhecidoDeclaradoNaoQuebraECaiNaInferencia()
    {
        Assert.Equal(
            AttachmentRoles.ProvidedFrontend,
            AttachmentRoles.Normalize("papel_que_nao_existe", "app.zip"));
    }
}
