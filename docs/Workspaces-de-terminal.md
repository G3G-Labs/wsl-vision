# Workspaces de terminal

Out 23, 2026 · Gabriel Oliveira · status: implementado (fases 1 a 3), ainda não testado no Windows

A página Terminal do WSL Vision passa a guardar a organização do
desenvolvedor: grupos de abas por projeto, cada aba com nome, pasta,
usuário e comando de inicialização. Tudo continua lá depois de fechar o
app ou reiniciar o PC, e um clique sobe o projeto inteiro (containers e
terminais). A ideia vem do escopo do Forge
(`Ferramentas/Forge/docs/Escopo-Forge.md`), recortada para o que o WSL
Vision é: um painel de WSL + Docker, não um terminal genérico.

## O problema, no WSL Vision

Hoje a página Terminal tem abas embutidas (Windows Terminal via ConPTY)
e uma lista de comandos rápidos ao lado. Mas as abas morrem quando o app
fecha. Para voltar a trabalhar no ObraClick, o usuário abre o app, sobe
os containers na página Containers, volta ao Terminal, abre uma aba na
pasta da API, outra no front, roda `npm run dev` em cada uma e renomeia
tudo. Todo dia.

## O que vem do Forge e o que fica de fora

O WSL Vision já resolve a parte cara do Forge (o terminal em si, com o
controle do Windows Terminal). O que falta é a camada de organização.

| Ideia do Forge | No WSL Vision | Por quê |
|---|---|---|
| Grupos (workspaces) com nome, cor, recolher | **Entra** | É o centro da proposta. |
| Abas com nome, cor, pasta, comando, "iniciar automaticamente" | **Entra** | Idem. |
| Persistência automática e restauração | **Entra** | Idem. |
| "Iniciar grupo" / "Parar grupo" | **Entra**, e sobe os projetos Compose das abas antes delas | Junta as duas metades do app numa ação. |
| Ações da aba: iniciar, reiniciar, parar, VS Code, Explorer, copiar caminho | **Entra** | O LauncherService já abre VS Code e Explorer. |
| Aba de administrador | **Vira aba root** (já existe, com confirmação e cor âmbar) | No WSL não existe UAC; root é o equivalente. |
| Branch Git na aba e na barra de status | **Entra**, consultada dentro da distro em lote | Ler `\\wsl.localhost` pelo Windows é lento. |
| Autocomplete sem maiúsculas e histórico garantido | **Entra**, pelo script que o app já gera para cada aba | Nada é gravado nos arquivos do usuário. |
| Histórico unificado pesquisável | **Fase 3** | Depende da integração de shell (OSC 133). |
| PowerShell, CMD, Git Bash | **Fica de fora** | Os shells são as distros WSL. Para o resto existe o Forge. |
| Emulação VT e renderização próprias | **Fica de fora** | Já usamos o controle do Windows Terminal. |
| Temas de terminal editáveis | **Fica de fora** | O tema acompanha o tema escuro do app. |
| Portas em uso, alertas de processo | **Depois** | O app já mostra as portas dos containers. |

**Não-objetivos**, iguais aos do Forge: sem IA, sem conta, sem nuvem, e
sem salvar a saída (scrollback) dos terminais.

## Casos de uso

1. **Começar o dia.** Liga o PC. O WSL Vision abre com o Windows
   (opção "Iniciar ambiente") e o workspace "ObraClick", marcado para
   subir junto, já roda `docker compose up -d` e abre as abas `api`
   (`npm run dev`) e `fila` (`php artisan queue:work`).
2. **Trocar de contexto.** Recolhe "ObraClick" e expande "Ferramentas"
   sem fechar nada. As abas de "Ferramentas" só iniciam o shell quando
   são abertas.
3. **Pular para o código.** Na aba `api`, clica em "VS Code" e o editor
   abre na pasta da aba, já conectado ao WSL.
4. **Tarefa como root.** Cria no grupo uma aba root para `apt upgrade`.
   Ela fica marcada em âmbar e pede confirmação ao iniciar.
5. **Parar o dia.** "Parar grupo" manda Ctrl+C para todas as abas e
   oferece derrubar os containers do projeto ligado.

## Conceitos

- **Workspace (grupo):** um agrupamento, como um cliente ou um produto:
  nome, cor, ordem, recolhido ou não, uma pasta de partida para as abas
  novas e a chave "subir com o ambiente". A pasta do workspace não é a de
  um projeto: em "Rentalz", front-end e back-end são projetos separados.
- **Aba:** é o projeto. Tem nome, usuário (seu usuário ou root), pasta,
  comando de inicialização, "iniciar automaticamente" e o projeto Compose.
  O projeto Compose é achado pela pasta da aba (o de pasta igual, ou o mais
  fundo que a contém) ou escolhido à mão.
- **Abas avulsas:** abertas de fora do workspace (por exemplo, "Abrir
  terminal" na linha de um projeto em Containers, ou a paleta Ctrl+K).
  Ficam na seção "Abertas agora" e não são salvas até o usuário arrastar
  para um grupo ou escolher "Guardar no workspace…". Assim os workspaces
  não acumulam lixo de abas de uso único.
- **Comandos rápidos:** deixam de ser uma lista à parte. Cada um vira
  uma aba com comando e "iniciar automaticamente" desligado (ver
  Migração). O "rodar na aba atual" continua pela paleta Ctrl+K.

## Interface

A página Terminal perde o painel da direita e ganha três áreas fixas:
barra lateral de workspaces, terminal e barra de status. Nada precisa
ser configurado antes do primeiro uso: sem workspaces, a página mostra
o estado vazio e o terminal abre como hoje.

**Barra lateral (240 px, recolhível)**

- Árvore de grupos e abas. Cada grupo tem a bolinha da cor dele, o nome,
  a contagem de abas rodando ("2/3") e, ao passar o mouse, os botões
  "Iniciar grupo" e "Parar grupo".
- Cada aba mostra o estado (bolinha), o nome, a branch Git em
  monoespaçado e um cadeado âmbar se for root.
- Estados da aba: **não iniciada** (anel cinza), **shell aberto** (azul),
  **comando rodando** (verde), **encerrou com erro** (vermelho, com o
  código de saída na dica).
- No topo, "Abertas agora" (abas avulsas). No rodapé, "+ Novo
  workspace" e o atalho "Criar a partir de um projeto Compose".
- Arrastar reordena abas e grupos e move abas entre grupos.

**Cabeçalho da aba (40 px)**

- Nome editável no lugar (duplo clique), pasta em monoespaçado.
- Ícones: Iniciar ou Reiniciar, Parar (Ctrl+C), VS Code, Explorer e o
  menu "…" (duplicar, copiar caminho, abrir no Windows Terminal,
  configurar, remover).
- "Configurar" abre um painel recolhível logo abaixo do cabeçalho, sem
  modal: distro, usuário, pasta, comando de inicialização, "iniciar
  automaticamente", variáveis de ambiente e cor. Salva ao sair do campo.

**Terminal:** ocupa todo o resto, sem cartão nem margem extra.

**Barra de status (24 px):** distro · usuário (âmbar se root) · pasta
atual · branch, alterações pendentes e commits à frente/atrás · projeto
Compose da aba e quantos containers dele estão rodando. Na barra lateral,
um ícone de caixa na aba mostra o mesmo estado pela cor.

**Estado vazio:** "Seus terminais, do jeito que você deixou". Botões
"Abrir terminal" (aba avulsa, como hoje) e "Criar workspace". Se houver
projetos Compose, sugere "Criar workspace para obraclick-homologacao"
com a pasta já preenchida.

**Outros pontos do app**

- **Paleta Ctrl+K:** "Iniciar workspace ObraClick", "Ir para a aba api",
  "Rodar `docker compose logs -f` na aba atual".
- **Bandeja:** uma linha por workspace com Iniciar/Parar.
- **Containers:** o menu do projeto ganha "Abrir workspace" quando há um
  ligado a ele.

**Princípios**, os do Forge: tudo se edita no lugar; criar uma aba leva
dois cliques ("+" no grupo, Enter); vermelho só para erro, âmbar só para
root; todo ícone tem dica e atalho.

## Atalhos

As teclas vão para o terminal antes do app, então os atalhos seguem o
Windows Terminal (com Shift) e não roubam os do bash. O Forge usa Ctrl+W
e Ctrl+R; aqui eles continuam sendo "apagar palavra" e "busca reversa"
do bash.

| Ação | Atalho |
|---|---|
| Nova aba no grupo atual | Ctrl+Shift+T |
| Fechar aba | Ctrl+Shift+W |
| Novo workspace | Ctrl+Shift+N |
| Próxima / anterior | Ctrl+Tab / Ctrl+Shift+Tab |
| Ir para a aba 1–9 do grupo | Alt+1 … Alt+9 |
| Iniciar / reiniciar o comando da aba | Ctrl+Shift+R |
| Iniciar o grupo inteiro | Ctrl+Shift+Enter |
| Abrir pasta no VS Code | Ctrl+Shift+E |
| Histórico do app (Fase 3) | Ctrl+Shift+H |
| Renomear (com foco na barra lateral) | F2 |

F2 só vale na barra lateral: dentro do terminal ele é do htop e do mc.

## Persistência

O app salva a estrutura, nunca a saída. Ao reabrir, cada aba volta com
nome, pasta e comando, e só executa o comando se estiver marcada para
iniciar automaticamente.

| Item | Salvo? |
|---|---|
| Grupos: nome, cor, ordem, recolhido, pasta de partida, "subir com o ambiente" | Sim |
| Abas: nome, ordem, grupo, usuário, pasta, comando, "iniciar automaticamente", projeto Compose escolhido | Sim |
| Aba e grupo selecionados, largura da barra lateral | Sim |
| Abas avulsas ("Abertas agora") | Não |
| Última pasta visitada | Não (a aba volta à pasta inicial) |
| Saída dos terminais e processos em execução | Não |

**Onde:** `%APPDATA%\WslVision\workspaces.json`, ao lado do
`settings.json`, mas separado dele: muda muito mais e tem outro dono.

```json
{
  "version": 1,
  "selected": "t1",
  "groups": [
    {
      "id": "g1",
      "name": "Rentalz",
      "color": "#4CC2FF",
      "collapsed": false,
      "folder": "~/Projetos/Rentalz",
      "startWithEnvironment": true,
      "tabs": [
        { "id": "t1", "name": "Front-end", "folder": "./rentalz-frontend",
          "startup": "npm run dev", "autostart": true },
        { "id": "t2", "name": "Back-end", "folder": "./rentalz-backend",
          "startup": "php artisan serve", "autostart": true,
          "composeProject": "rentalz-backend" },
        { "id": "t3", "name": "manutenção", "root": true, "folder": "~",
          "startup": "", "autostart": false }
      ]
    }
  ]
}
```

`distro` é omitido quando é a distro principal das Configurações. Pastas
relativas (`./api`) partem da pasta do workspace. `composeProject` só
aparece quando o projeto foi escolhido à mão; sem ele, vale o da pasta.
No formato antigo ele ficava no workspace; ao abrir, vai para as abas na
pasta do workspace (ou para a primeira).

**Como salvar:** o mesmo padrão do SettingsService (arquivo temporário,
renomeia, cópia do último válido e `.ilegivel` se não der para ler),
com atraso de 500 ms para juntar mudanças em sequência e um salvamento
final ao fechar. Desligar o PC no meio de uma gravação não perde nada.

**Restauração:** ao abrir, a barra lateral aparece na hora, e nenhuma aba
ganha processo por estar selecionada: a aba parada mostra "Aba não
iniciada" e um botão Iniciar (Ctrl+Shift+R ou clique duplo). As abas com
"iniciar automaticamente" sobem assim que a distro estiver ligada. O app
não liga a distro sozinho (o padrão de Ajustes é "só o WSL Vision"), então
elas esperam por ela. As que vão iniciar são preparadas num
único `wsl.exe` (um script grava os scripts de todas) e sobem uma de cada
vez, com um intervalo curto. Abrir 10 abas não pode virar 20 `wsl.exe`
de uma vez: foi esse tipo de rajada que esgotou os canais hv_sock da
distro antes.

**Migração:** na primeira abertura com a versão nova, se houver
`QuickCommands` no `settings.json`, o app cria o grupo "Comandos
rápidos" com uma aba por comando (nome, pasta, comando, "iniciar
automaticamente" desligado). A lista antiga só é apagada depois de o
`workspaces.json` ser gravado com sucesso. É o mesmo cuidado da migração
do `StartEnvironmentOnLaunch`, com teste.

## Shell: autocomplete e histórico

Cada aba já roda um script gerado pelo LauncherService (entra na pasta,
roda o comando, abre `$SHELL -l`). Ele ganha três linhas, desligáveis
nas Configurações:

| Auxílio | Como |
|---|---|
| Autocomplete sem maiúsculas | Gera `/tmp/wslvision-<uid>/inputrc` com `$include ~/.inputrc` e `set completion-ignore-case on`, e exporta `INPUTRC` antes do `exec`. Para zsh, um `ZDOTDIR` temporário que carrega o `.zshrc` original e acrescenta o `zstyle` equivalente. |
| Histórico que não se perde | Exporta `PROMPT_COMMAND="history -a${PROMPT_COMMAND:+;$PROMPT_COMMAND}"` e liga `histappend`. Fechar a aba não perde os comandos. |
| Estado e código de saída | Depois do comando de inicialização, o script grava o código de saída em `/tmp/wslvision-<uid>/tab-<id>.status`. Na Fase 3, a integração de shell (OSC 133) substitui isso. |

Nada é escrito em `.bashrc`, `.inputrc` ou `.zshrc` do usuário.

## Git

A branch de cada aba sai de uma consulta só, dentro da distro, para todas
as abas abertas: `git -C <pasta> rev-parse --abbrev-ref HEAD` e
`git status --porcelain=v2 --branch` (alterações e à frente/atrás).
Roda quando a aba ganha foco e a cada 10 s enquanto a página Terminal
está visível, com o CachedQuery que já existe. Com a página oculta ou a
janela na bandeja, não roda. A mesma consulta lê os arquivos `.status`.

## Integração com o resto do app

- **Iniciar ambiente** (`LaunchAction.Environment`): depois da distro, do
  Docker e dos projetos Compose marcados, inicia os workspaces com
  "subir com o ambiente". A ordem é containers primeiro, abas depois.
- **Iniciar grupo:** os projetos Compose das abas (cada aba tem o seu)
  que estiverem parados sobem primeiro: roda `docker compose up -d` (o ComposeRunner, respeitando o
  `depends_on`), espera os containers e então abre as abas com comando.
- **Parar grupo:** Ctrl+C em todas as abas e a pergunta "Parar também os
  containers de rentalz-frontend, rentalz-backend?".
- **Containers:** "Terminal" num projeto abre a aba desse projeto, se
  houver uma; senão, uma aba em "Abertas agora".

## Arquitetura

O código segue o desenho atual (MVVM, serviços sem referência à UI).

| Peça | O que é |
|---|---|
| `Models/Workspace.cs` | `WorkspaceFile`, `WorkspaceGroup`, `WorkspaceTab` (o JSON acima). |
| `Services/WorkspaceStore.cs` | Lê e grava o `workspaces.json` (atômico, com atraso de 500 ms), faz a migração e avisa as mudanças. Pasta injetável, como o SettingsService, para testes. |
| `Services/LauncherService.cs` | `PrepareEmbeddedTerminalAsync` passa a aceitar a aba inteira (variáveis, auxílios do shell, id do `.status`) e ganha a versão em lote. |
| `Services/WorkspaceGit.cs` | A consulta em lote de branch, status e `.status`. |
| `ViewModels/WorkspaceGroupViewModel.cs`, `WorkspaceTabViewModel.cs` | Estado da interface: selecionada, estado, branch. O `TerminalSessionViewModel` atual vira o estado de execução da aba. |
| `ViewModels/TerminalViewModel.cs` | Árvore, seleção, iniciar/parar grupo, abas avulsas. Os comandos rápidos saem daqui. |
| `Views/Pages/TerminalView.xaml(.cs)` | Novo layout. Continua criando um `EasyTerminalControl` por aba iniciada, e só por aba iniciada. |

**Testes:** leitura e gravação do JSON (incluindo arquivo truncado),
migração dos comandos rápidos, pastas relativas, montagem do script da
aba (aspas, base64, `INPUTRC`), análise da saída do `git status` em lote
e a ordem de restauração (selecionada primeiro, sem rajada).

## Leveza

Cada aba não iniciada é só uma linha na árvore: nenhum controle de
terminal, nenhum processo.

| Métrica | Meta |
|---|---|
| Abrir a página com 20 abas em 5 grupos, 2 automáticas | barra lateral em < 200 ms; 2 `wsl.exe` de sessão + 1 de preparo |
| RAM a mais por aba iniciada (só o app, sem o shell) | < 15 MB |
| CPU com todas as abas ociosas | < 1% (sem polling com a página oculta) |
| `workspaces.json` com 50 abas | < 30 KB |

## Fases

Cada fase termina com uma versão utilizável.

1. **Workspaces persistentes.** Modelo, `workspaces.json`, migração,
   barra lateral (criar, renomear, recolher, cor), configurar aba,
   restauração preguiçosa. Marco: reiniciar o PC e encontrar tudo igual.
2. **Ações e integração.** Iniciar/parar/reiniciar aba e grupo, projeto
   Compose ligado, "subir com o ambiente", VS Code, Explorer, barra de
   status, atalhos, paleta e bandeja, arrastar e soltar.
3. **Auxílios ao desenvolvedor.** Git em lote, autocomplete sem
   maiúsculas, histórico garantido, estado e código de saída.
4. **Depois.** Histórico unificado (Ctrl+Shift+H, OSC 133), um
   `.wslvision.json` versionado no repositório para importar o workspace,
   sugestão de comandos a partir do `package.json` e do `compose.yaml`,
   divisão de tela.

## Riscos

| Risco | Mitigação |
|---|---|
| Rajada de `wsl.exe` ao restaurar (já travou a distro uma vez) | Preparo em lote, início em fila, só abas automáticas e a selecionada. |
| O EasyWindowsTerminalControl não expõe a saída do ConPTY para ler OSC 133 | O arquivo `.status` cobre o estado na Fase 3 sem depender da saída. Verificar na Fase 1. |
| `INPUTRC`/`PROMPT_COMMAND` conflitam com o perfil do usuário | Variáveis, não arquivos; incluem o original; desligáveis. |
| Atalhos do app roubam teclas do terminal | Atalhos com Shift, como o Windows Terminal; F2 só na barra lateral. |
| Muitos controles de terminal pesam | Aba só ganha controle quando inicia; fechar a aba libera o controle. |
| A migração apaga os comandos rápidos | Só apaga depois de gravar o `workspaces.json`; teste automatizado. |

## Decisões tomadas

As quatro questões que estavam em aberto foram resolvidas com a proposta
original:

- Abas avulsas ficam em "Abertas agora" e não são salvas. A configuração
  da aba tem "Guardar num workspace".
- Fechar a janela para a bandeja não para as abas.
- "Parar grupo" manda Ctrl+C às abas e pergunta antes de parar os
  containers do projeto ligado.
- Os comandos rápidos viraram abas no grupo "Comandos rápidos". O "rodar
  na aba atual" continua pela paleta.

## O que ficou para depois

- **Reordenar:** arrastar e soltar abas e grupos ainda não existe. Por
  enquanto eles se movem pelo menu de botão direito (Mover para cima ou
  para baixo).
- **Variáveis de ambiente:** a aba ainda não tem variáveis próprias.
- **Histórico unificado:** o Ctrl+Shift+H e o arquivo `history.jsonl`
  dependem da integração de shell (OSC 133).
- **Janela escondida:** os terminais embutidos vivem na janela. Com o app
  só na bandeja, uma aba iniciada (por "iniciar automaticamente" ou pelo
  Iniciar ambiente) pode só começar a rodar quando a janela abrir.
  Verificar no Windows. Por isso o "Iniciar" de um workspace na bandeja
  abre a janela no Terminal.
- **Barra recolhida:** a barra lateral recolhe com o botão do topo, mas
  não recolhe sozinha na tela estreita.

## Critérios de aceite

- [ ] Criar 3 workspaces com 3 abas cada (uma delas root), renomear tudo,
  reiniciar o PC e encontrar tudo igual.
- [ ] "Iniciar grupo" num workspace com front-end e back-end (cada um com o seu Compose) sobe os containers dos dois e
  depois roda os comandos das abas.
- [ ] Com "Iniciar ambiente" e "subir com o ambiente", ligar o PC deixa o
  projeto pronto sem nenhum clique.
- [ ] Abas não iniciadas não criam processo nem controle de terminal.
- [ ] A branch aparece e muda depois de um `git checkout` na aba.
- [ ] No bash, `cd doc` + Tab completa `Documentos`, e a seta para cima
  mostra comandos de antes do reinício.
- [ ] Os comandos rápidos antigos aparecem no grupo "Comandos rápidos".
- [ ] Desligar o PC durante uma gravação não corrompe os workspaces.
