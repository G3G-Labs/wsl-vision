using System.Text;

namespace WslVision.Services;

/// <summary>Uma aba a preparar: onde abre, como quem e o que roda. <see cref="TabId"/> identifica o arquivo de estado.</summary>
public sealed record TerminalRequest(string Folder, bool AsRoot = false, string? Command = null, string? TabId = null);

/// <summary>
/// Monta o script que cada terminal executa dentro da distro: entra na pasta, roda o comando (se houver), grava o código
/// de saída e abre o shell do usuário. Textos do usuário entram em base64, nunca direto no script.
/// </summary>
public static class TerminalScript
{
    /// <summary>Arquivo com o código de saída do comando da aba, dentro da pasta do app na distro.</summary>
    public static string StatusFileName(string tabId) => $"tab-{tabId}.status";

    /// <summary>
    /// Script que grava os scripts das abas na pasta do usuário (um arquivo cada) e imprime os caminhos, na ordem.
    /// Os scripts vão dentro dele em base64 (o script todo segue pelo stdin, sem limite de tamanho). Antes, apaga o
    /// arquivo de estado das abas: o código de saída da execução anterior não pode valer para a nova.
    /// </summary>
    public static string Writer(IReadOnlyList<string> scripts, IEnumerable<string> tabIds)
    {
        var sb = new StringBuilder("umask 077\nd=\"/tmp/wslvision-$(id -u)\"\nmkdir -p \"$d\"\n");
        foreach (var id in tabIds)
        {
            if (!id.All(char.IsAsciiLetterOrDigit))
                throw new ArgumentException("Id de aba inválido.", nameof(tabIds));
            sb.Append($"rm -f -- \"$d/{StatusFileName(id)}\"\n");
        }
        foreach (var script in scripts)
        {
            sb.Append("f=\"$(mktemp \"$d/term.XXXXXX\")\" || exit 1\n");
            sb.Append($"printf '%s' '{Convert.ToBase64String(Encoding.UTF8.GetBytes(script))}' | base64 -d > \"$f\" || exit 1\n");
            sb.Append("printf '%s\\n' \"$f\"\n");
        }
        return sb.ToString();
    }

    /// <param name="helpers">
    /// Autocomplete sem maiúsculas (um inputrc do app que inclui o do usuário) e histórico gravado a cada comando.
    /// Só variáveis de ambiente: nada é escrito em .bashrc ou .inputrc.
    /// </param>
    public static string Build(string linuxDir, string? command, string? tabId, bool helpers)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        if (tabId is not null && !tabId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Id de aba inválido.", nameof(tabId));

        var sb = new StringBuilder();
        sb.Append("rm -f -- \"$0\"\n");
        sb.Append($"cd -- \"$(printf '%s' '{B64(linuxDir)}' | base64 -d)\" 2>/dev/null || cd\n");
        sb.Append("__wv_d=\"/tmp/wslvision-$(id -u)\"\n");
        if (helpers)
        {
            sb.Append("__wv_ir=\"$__wv_d/inputrc\"\n");
            sb.Append("{ [ -f /etc/inputrc ] && printf '$include /etc/inputrc\\n'; [ -f \"$HOME/.inputrc\" ] && printf '$include %s\\n' \"$HOME/.inputrc\"; printf 'set completion-ignore-case on\\n'; } > \"$__wv_ir\" 2>/dev/null && export INPUTRC=\"$__wv_ir\"\n");
            sb.Append("export PROMPT_COMMAND=\"history -a${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"\n");
        }
        if (!string.IsNullOrWhiteSpace(command))
        {
            var status = tabId is null ? null : $"\"$__wv_d/{StatusFileName(tabId)}\"";
            if (status is not null)
                sb.Append($"rm -f -- {status}\n");
            sb.Append($"__wv_cmd=\"$(printf '%s' '{B64(command)}' | base64 -d)\"\n");
            sb.Append("printf '\\033[1;34m$ %s\\033[0m\\n' \"$__wv_cmd\"\n");
            sb.Append("eval \"$__wv_cmd\"\n");
            sb.Append("__wv_rc=$?\n");
            if (status is not null)
                sb.Append($"printf '%s' \"$__wv_rc\" > {status}\n");
            // 130 é o Ctrl+C (o botão Parar): não é erro.
            sb.Append("if [ \"$__wv_rc\" -ne 0 ] && [ \"$__wv_rc\" -ne 130 ]; then printf '\\033[31mO comando terminou com código %s. O shell continua aberto.\\033[0m\\n' \"$__wv_rc\"; fi\n");
            sb.Append("unset __wv_cmd __wv_rc\n");
        }
        sb.Append("unset __wv_d __wv_ir\n");
        sb.Append("exec \"${SHELL:-/bin/bash}\" -l\n");
        return sb.ToString();
    }
}
