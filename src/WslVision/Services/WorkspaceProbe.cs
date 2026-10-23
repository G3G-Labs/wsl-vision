using System.Text;

namespace WslVision.Services;

/// <summary>O que uma consulta descobriu sobre a pasta e o comando de uma aba.</summary>
public sealed record TabProbe(string TabId, string? Branch, int Changed, int Ahead, int Behind, int? ExitCode);

/// <summary>
/// Branch, alterações e o código de saída do comando de várias abas, numa consulta só dentro da distro
/// (um wsl.exe para todas, em vez de um por aba).
/// </summary>
public static class WorkspaceProbe
{
    /// <summary>
    /// O script recebe em $A1 uma linha "id\tpasta" por aba, com as pastas já expandidas. GIT_OPTIONAL_LOCKS=0:
    /// a consulta roda de tempos em tempos e não pode pegar o .git/index.lock (o git commit do usuário falharia).
    /// </summary>
    public const string Script = """
        d="/tmp/wslvision-$(id -u)"
        tab="$(printf '\t')"
        printf '%s\n' "$A1" | while IFS="$tab" read -r id dir; do
          [ -n "$id" ] || continue
          printf '@@ %s\n' "$id"
          GIT_OPTIONAL_LOCKS=0 git -C "$dir" status --porcelain=v2 --branch 2>/dev/null | head -n 400
          [ -f "$d/tab-$id.status" ] && printf '@status %s\n' "$(cat "$d/tab-$id.status")"
        done
        """;

    public static string Input(IEnumerable<(string TabId, string Folder)> tabs)
    {
        var sb = new StringBuilder();
        foreach (var (id, folder) in tabs)
        {
            // Tab e quebra de linha separam os campos: uma pasta com eles não é consultada.
            if (folder.Contains('\t') || folder.Contains('\n'))
                continue;
            sb.Append(id).Append('\t').Append(folder).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Lê a saída do <see cref="Script"/>: um bloco "@@ id" por aba, com o git status v2 e o "@status" do comando.</summary>
    public static IReadOnlyList<TabProbe> Parse(string output)
    {
        var result = new List<TabProbe>();
        string? id = null;
        string? branch = null;
        int changed = 0, ahead = 0, behind = 0;
        int? exit = null;

        void Flush()
        {
            if (id is not null)
                result.Add(new TabProbe(id, branch, changed, ahead, behind, exit));
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                Flush();
                id = line[3..].Trim();
                branch = null;
                changed = ahead = behind = 0;
                exit = null;
            }
            else if (id is null)
            {
                continue;
            }
            else if (line.StartsWith("@status ", StringComparison.Ordinal))
            {
                exit = int.TryParse(line[8..].Trim(), out var code) ? code : null;
            }
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = line[14..].Trim();
                branch = head == "(detached)" ? "HEAD solto" : head;
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                foreach (var part in line[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith('+') && int.TryParse(part[1..], out var a)) ahead = a;
                    else if (part.StartsWith('-') && int.TryParse(part[1..], out var b)) behind = b;
                }
            }
            else if (line.Length > 1 && line[1] == ' ' && "12u?".Contains(line[0]))
            {
                changed++;
            }
        }
        Flush();
        return result;
    }

    /// <summary>"●2 ↑1 ↓3": alterações não commitadas e commits à frente e atrás do remoto.</summary>
    public static string GitSummary(int changed, int ahead, int behind)
    {
        var parts = new List<string>();
        if (changed > 0) parts.Add($"●{changed}");
        if (ahead > 0) parts.Add($"↑{ahead}");
        if (behind > 0) parts.Add($"↓{behind}");
        return string.Join(' ', parts);
    }
}
