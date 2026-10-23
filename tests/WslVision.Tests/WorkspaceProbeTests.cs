using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class WorkspaceProbeTests
{
    private const string Output = """
        @@ api
        # branch.oid 4f2c1e9
        # branch.head feat/orcamentos
        # branch.upstream origin/feat/orcamentos
        # branch.ab +1 -3
        1 .M N... 100644 100644 100644 aaa bbb src/a.ts
        ? novo.txt
        @status 1
        @@ docs
        @@ solto
        # branch.oid 4f2c1e9
        # branch.head (detached)
        @status 0
        """;

    [Fact]
    public void LeBranchAlteracoesEEstado()
    {
        var all = WorkspaceProbe.Parse(Output);

        Assert.Equal(["api", "docs", "solto"], all.Select(p => p.TabId));
        var api = all[0];
        Assert.Equal("feat/orcamentos", api.Branch);
        Assert.Equal(2, api.Changed);
        Assert.Equal(1, api.Ahead);
        Assert.Equal(3, api.Behind);
        Assert.Equal(1, api.ExitCode);
    }

    [Fact]
    public void PastaSemGit_SemBranchESemEstado()
    {
        var docs = WorkspaceProbe.Parse(Output)[1];

        Assert.Null(docs.Branch);
        Assert.Equal(0, docs.Changed);
        Assert.Null(docs.ExitCode);
    }

    [Fact]
    public void HeadSolto() => Assert.Equal("HEAD solto", WorkspaceProbe.Parse(Output)[2].Branch);

    [Fact]
    public void Consulta_NaoPegaOLockDoGit() => Assert.Contains("GIT_OPTIONAL_LOCKS=0 git", WorkspaceProbe.Script);

    // Aba sem o arquivo de status: "[ -f ] && ..." no fim do laço faria o script sair com 1 e encher o log de avisos.
    [Fact]
    public void Consulta_SaiComZeroSemArquivoDeStatus() => Assert.DoesNotContain("] &&", WorkspaceProbe.Script);

    [Fact]
    public void SaidaVazia() => Assert.Empty(WorkspaceProbe.Parse(""));

    [Fact]
    public void Entrada_UmaLinhaPorAba_PulaPastasComSeparador()
    {
        var input = WorkspaceProbe.Input([("a1", "/home/g/obra"), ("b2", "/tmp/com\ttab"), ("c3", "/home/g/x y")]);

        Assert.Equal("a1\t/home/g/obra\nc3\t/home/g/x y\n", input);
    }

    [Theory]
    [InlineData(0, 0, 0, "")]
    [InlineData(2, 1, 0, "●2 ↑1")]
    [InlineData(0, 0, 4, "↓4")]
    public void Resumo(int changed, int ahead, int behind, string expected) => Assert.Equal(expected, WorkspaceProbe.GitSummary(changed, ahead, behind));
}
