using Harness.Host.Product;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Product;

namespace Harness.IntegrationTests.Product;

/// <summary>
/// Prontidão para o Prisma, cujo perfil sobrescreve o banco padrão por Oracle.
///
/// A pergunta que este arquivo responde antes de o projeto existir: <b>Oracle é uma stack
/// SUPORTADA ou um buraco de plataforma?</b> Descobrir isso durante o run custaria quota de agente
/// para reprovar por um motivo que não é do produto.
///
/// O achado que motivou o teste da trava de isolamento é concreto: a forma EZ-connect do Oracle
/// (<c>127.0.0.1:1521/FREEPDB1</c>) não era reconhecida como local, e a verificação de persistência
/// de um banco que ESTAVA na máquina teria parado com <c>NotSupported</c>.
/// </summary>
public sealed class OracleReadinessTests
{
    private const string PedidoPrisma =
        "Sistema web de acompanhamento de riscos corporativos, usado pela equipe de GRC pelo " +
        "navegador. O banco de dados deve ser Oracle.";

    /// <summary>
    /// O caminho REAL do override: a exigência do usuário é PARSEADA em diretiva pelo mesmo
    /// componente que o extrator usa em produção, e é a diretiva — não a prosa — que chega ao
    /// resolvedor. Resolver direto da prosa devolveria o baseline, e o teste estaria medindo outra
    /// coisa.
    /// </summary>
    private static ProjectEffectiveProfile Perfil()
    {
        var diretivas = ProfileDirectiveParser.Parse(
            PedidoPrisma, ProfileAuthority.ProjectRequirement, "prisma-requisito", "requirement");
        return EffectiveProfileResolver.Resolve(
            new EffectiveProfileInputs("prisma", PedidoPrisma, diretivas));
    }

    [Fact]
    public void OOverrideDeBancoPorOracleVenceOBaselineSemTrocarOPadraoGlobal()
    {
        var prisma = Perfil();
        Assert.Equal("Oracle", prisma.Data.Database);
        Assert.Contains(prisma.Overrides, item => item.OverrideValue.Contains("Oracle", StringComparison.Ordinal));

        // O baseline GLOBAL continua SQL Server: o override é do projeto, não da plataforma.
        var semOverride = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            "outro", "Sistema web de cadastro de equipamentos usado pela equipe.", []));
        Assert.Equal("SQL Server", semOverride.Data.Database);
    }

    /// <summary>
    /// O plano de verificação de um projeto Oracle não pode ter NENHUM requisito sem verificador.
    /// Se tivesse, o Prisma reprovaria por buraco da plataforma e ninguém saberia distinguir isso de
    /// uma entrega ruim.
    /// </summary>
    [Fact]
    public void OPlanoDeVerificacaoDeUmProjetoOracleNaoTemRequisitoSemVerificador()
    {
        var runner = new ProductVerificationRunner(
            ProductVerifierCatalog.CreateAll(new TrustedProcessRunner()));
        var plan = ProductVerificationPlan.From(
            Perfil(), runner.NativeVerifiers, runner.ProjectControlledVerifiers);

        Assert.Empty(plan.Unsupported);
        Assert.Contains(ProductEvidenceKind.PersistenceVerified, plan.Required);
        Assert.Contains(ProductEvidenceKind.DatabaseMigrationValidated, plan.Required);

        // Nenhum verificador é escolhido por fornecedor de banco: todos derivam do que a entrega
        // publica. É o que permite o mesmo plano valer para SQL Server e para Oracle.
        var persistencia = plan.Steps.Single(step => step.Kind == ProductEvidenceKind.PersistenceVerified);
        Assert.Equal(VerificationTrustLevel.PoseidonControlled, persistencia.MinimumTrust);
    }

    /// <summary>
    /// A trava do banco de produção precisa reconhecer as formas REAIS de cadeia de conexão. Barrar
    /// um banco local custa um requisito sem prova; liberar um remoto custa escrita em produção — e
    /// por isso tudo o que não se reconhece continua sendo tratado como remoto.
    /// </summary>
    [Theory]
    // Oracle EZ-connect, que era o caso quebrado.
    [InlineData("User Id=app;Password=x;Data Source=127.0.0.1:15210/FREEPDB1;", true)]
    [InlineData("User Id=app;Password=x;Data Source=localhost:1521/XEPDB1;", true)]
    [InlineData("Data Source=localhost/FREEPDB1;", true)]
    // Descritor TNS completo.
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1521))" +
        "(CONNECT_DATA=(SERVICE_NAME=FREEPDB1)));", true)]
    // SQL Server, nas formas que já existiam.
    [InlineData("Server=localhost;Database=Prisma;Trusted_Connection=True;", true)]
    [InlineData("Server=127.0.0.1,1433;Database=Prisma;", true)]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=Prisma;", true)]
    // SQLite por arquivo e em memória.
    [InlineData("Data Source=prisma.db;", true)]
    [InlineData("Data Source=:memory:;", true)]
    // O que precisa continuar barrado.
    [InlineData("Data Source=oracle-prod.trensrj.interno:1521/PRISMA;", false)]
    [InlineData("Server=db.producao.interno;Database=Prisma;", false)]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(HOST=oracle-prod.interno)(PORT=1521)));", false)]
    public void ATravaDeBancoReconheceConexaoLocalDeQualquerFornecedorEBarraOResto(
        string conexao, bool esperadoSeguro)
    {
        var raiz = Path.Combine(Path.GetTempPath(), $"poseidon-isolamento-{Guid.NewGuid():N}");
        Directory.CreateDirectory(raiz);
        try
        {
            File.WriteAllText(
                Path.Combine(raiz, "appsettings.json"),
                "{ \"ConnectionStrings\": { \"Default\": \"" + conexao.Replace("\\", "\\\\") + "\" } }");

            var verdict = DatabaseIsolationGuard.Inspect(raiz);

            Assert.Equal(esperadoSeguro, verdict.Safe);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        }
        finally
        {
            Directory.Delete(raiz, recursive: true);
        }
    }
}
