using System.Text;
using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class TerminalScriptTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void TextoDoUsuario_SoEntraEmBase64()
    {
        const string dir = "/home/g/pasta com 'aspas' e $(rm -rf ~)";
        const string cmd = "echo \"$HOME\"; rm -rf /tmp/x";
        var script = TerminalScript.Build(dir, cmd, "abc123", helpers: false);

        Assert.DoesNotContain(dir, script);
        Assert.DoesNotContain(cmd, script);
        Assert.Contains(B64(dir), script);
        Assert.Contains(B64(cmd), script);
    }

    [Fact]
    public void ComComando_GravaOCodigoDeSaidaDaAba()
    {
        var script = TerminalScript.Build("/x", "npm run dev", "abc123", helpers: false);

        Assert.Contains("rm -f -- \"$__wv_d/tab-abc123.status\"", script);
        Assert.Contains("printf '%s' \"$__wv_rc\" > \"$__wv_d/tab-abc123.status\"", script);
        // O arquivo antigo some antes de rodar o comando de novo.
        Assert.True(script.IndexOf("rm -f -- \"$__wv_d/tab-abc123.status\"", StringComparison.Ordinal) < script.IndexOf("eval", StringComparison.Ordinal));
    }

    [Fact]
    public void SemComando_NaoTemArquivoDeEstado()
    {
        var script = TerminalScript.Build("/x", null, "abc123", helpers: false);

        Assert.DoesNotContain("status", script);
        Assert.DoesNotContain("eval", script);
        Assert.EndsWith("exec \"${SHELL:-/bin/bash}\" -l\n", script);
    }

    [Fact]
    public void Auxilios_SoPorVariaveisDeAmbiente()
    {
        var script = TerminalScript.Build("/x", null, null, helpers: true);

        Assert.Contains("set completion-ignore-case on", script);
        Assert.Contains("export INPUTRC=", script);
        Assert.Contains("history -a", script);
        Assert.DoesNotContain(".bashrc", script);
        Assert.DoesNotContain("> \"$HOME/.inputrc\"", script);
    }

    [Fact]
    public void SemAuxilios_NaoMexeNoShell()
    {
        var script = TerminalScript.Build("/x", null, null, helpers: false);

        Assert.DoesNotContain("INPUTRC", script);
        Assert.DoesNotContain("PROMPT_COMMAND", script);
    }

    [Fact]
    public void Lote_VaiInteiroNoScriptEApagaOEstadoAntigo()
    {
        var scripts = new[] { TerminalScript.Build("/a", "npm run dev", "aba1", helpers: true), TerminalScript.Build("/b", null, "aba2", helpers: true) };
        var writer = TerminalScript.Writer(scripts, ["aba1", "aba2"]);

        // Nada vai como argumento do wsl.exe (a linha de comando do Windows tem limite).
        Assert.DoesNotContain("$A1", writer);
        Assert.Contains(B64(scripts[0]), writer);
        Assert.Contains(B64(scripts[1]), writer);
        Assert.Contains("rm -f -- \"$d/tab-aba1.status\"", writer);
        Assert.Contains("rm -f -- \"$d/tab-aba2.status\"", writer);
        Assert.Equal(2, writer.Split("mktemp").Length - 1);
    }

    [Fact]
    public void Lote_IdEstranho_ERecusado() =>
        Assert.Throws<ArgumentException>(() => TerminalScript.Writer([], ["a/../b"]));

    [Fact]
    public void IdDeAbaEstranho_ERecusado() =>
        Assert.Throws<ArgumentException>(() => TerminalScript.Build("/x", "ls", "../../etc", helpers: false));
}
