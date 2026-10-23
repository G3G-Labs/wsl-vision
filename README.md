# WSL Vision

[![Licença: MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![Status: alfa](https://img.shields.io/badge/status-alfa-orange)

Painel leve para Windows que controla o WSL e o Docker Engine instalado direto na distro (ex.: Debian), sem Docker Desktop. Containers, arquivos, terminal e consumo numa janela só, com um visual escuro e discreto, feito para ficar aberto ao lado do editor.

**Por quê?** Rodar o Docker Engine nativo dentro do WSL é mais leve que o Docker Desktop e não exige licença, mas tudo vira linha de comando. O WSL Vision dá a esse ambiente uma interface: sobe seus projetos Compose com um clique, mostra o que está consumindo memória, navega pelos arquivos da distro e fica quieto na bandeja quando você não precisa dele.

> [!NOTE]
> Projeto em estágio inicial (v0.1). Funciona, mas ainda pode ter arestas. Relatos de bugs e sugestões são bem-vindos nas [Issues](../../issues).

## Download

Baixe o `.exe` na página de [Releases](../../releases). Não precisa instalar: é só abrir.

- **WslVision-lite.exe** (~10 MB): exige o [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0). Ocupa menos disco e menos RAM.
- **WslVision.exe** (~135 MB): portátil, roda em qualquer Windows sem instalar nada.

Na primeira abertura, um assistente escolhe a distro, verifica o Docker Engine e configura a conexão.

## Requisitos

- Windows 10 2004+ ou Windows 11, com WSL 2 e uma distro (recomendado: Debian com systemd).
- Docker Engine instalado **dentro** da distro. O assistente oferece a instalação guiada se ainda não tiver.
- Opcional: Windows Terminal e VS Code com a extensão WSL.

Nada disso exige administrador no Windows.

## Compilar e rodar

```powershell
# no Windows, com o .NET 10 SDK
dotnet run --project src/WslVision
```

Gerar o `.exe` único. Há duas edições:

| Edição | Comando | Tamanho | Quando usar |
| --- | --- | --- | --- |
| **Leve** (recomendada) | `dotnet publish src/WslVision -c Release -p:Edition=Lite -o publish/lite` | ~10 MB | PCs com o [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) instalado. É a que ocupa menos disco e menos RAM, porque o runtime é compartilhado com outros apps. |
| **Portátil** | `dotnet publish src/WslVision -c Release -o publish` | ~135 MB | Qualquer Windows, sem instalar nada. |

A edição portátil é publicada sem compressão de propósito. Comprimida, ela cairia para ~62 MB, mas o .NET descompactaria as bibliotecas na RAM a cada abertura; sem compressão, elas são lidas do disco sob demanda.

Também compila e publica a partir do Linux/WSL (o projeto usa `EnableWindowsTargeting`), mas o app só roda no Windows.

Testes da lógica pura (rodam em qualquer sistema):

```bash
dotnet test tests/WslVision.Tests
```

Argumentos: `--minimized` abre só o ícone na bandeja (é o que a opção "Iniciar com o Windows" usa).

### Publicar uma versão

As releases são geradas pelo GitHub Actions ([release.yml](.github/workflows/release.yml)). Basta enviar uma tag:

```bash
git tag v0.2.0
git push origin v0.2.0
```

O workflow roda os testes, publica as duas edições no Windows com a versão da tag e cria a release com `WslVision-lite.exe`, `WslVision.exe` e `SHA256SUMS.txt`. Tags com sufixo (ex.: `v0.2.0-beta.1`) viram pré-lançamento.

## Iniciar ambiente

Um clique deixa tudo pronto para trabalhar: liga a distro, liga o serviço do Docker e sobe os containers dos projetos marcados com **"Iniciar com o ambiente"** (caixa em cada projeto, na aba Containers). Cada passo só acontece se ainda não estiver pronto.

O botão aparece:
- no cartão do ambiente, no topo da Visão geral, com o andamento de cada passo (distro, Docker, projetos);
- no topo da aba Containers, quando há algo para ligar;
- no painel da bandeja (clique esquerdo no ícone) e no topo do menu da bandeja;
- na paleta de comandos (`Ctrl+K`).

Com tudo ligado, o mesmo lugar mostra **Parar ambiente**, que desliga o Docker e os containers, mas mantém a distro.

Em **Ajustes › Inicialização** você escolhe o que liga quando o app abre (inclusive com o Windows): **só o WSL Vision** (padrão), a distro, a distro e o Docker, ou o ambiente completo. Ali também fica **"Docker liga junto com a distro"** (systemd), que vem desligado: assim o Docker e os projetos só sobem quando você pede.

## O que tem

| Aba | Funções |
| --- | --- |
| **Visão geral** | Tela inicial. Cartão do ambiente (estado, passos, Iniciar/Parar ambiente, desligar o WSL); memória, CPU (último minuto), disco da distro e `vmmemWSL` com gráfico; projetos com "Iniciar com o ambiente", VS Code e iniciar/parar; quem mais usa memória; atividade recente (containers que iniciaram, pararam, caíram ou ficaram sem memória); limites do `.wslconfig` (memória, processadores, devolver memória ociosa) com backup e desfazer, e um "Usar recomendado" para o PC (um terço dos processadores lógicos; um quarto da RAM, de 4 a 8 GB) |
| **Containers** | Agrupados por projeto do Docker Compose (nome do serviço, pasta, status geral, CPU e memória somados; abrir no VS Code ou no terminal; iniciar, reiniciar ou parar o projeto inteiro na ordem do `depends_on`; "Iniciar com o ambiente"), com as ações na própria linha do projeto e botões coloridos pelo que fazem (verde inicia, azul reinicia, vermelho para ou exclui). Por container: status traduzido, porta clicável, tempo ligado, CPU e memória; iniciar, parar, reiniciar, excluir (com confirmação); logs ao vivo com busca, pausa e copiar; filtros e busca; ligar/desligar o Docker |
| **Arquivos** | Favoritos e locais; barra de caminho com trilha clicável, alternância Linux/Windows e copiar; a ação principal (VS Code) segue a pasta selecionada; abrir no terminal ou no Explorer; nova pasta, renomear, excluir, copiar/recortar/colar; arrastar do Windows para dentro e de dentro para o Explorer; operações como root com confirmação; listagem paginada para pastas grandes |
| **Terminal** | Terminal da distro dentro da janela, com abas (controle do próprio Windows Terminal, via ConPTY); abrir como root com confirmação; pastas recentes; comandos rápidos numa aba nova ou na aba atual; abrir a mesma pasta no Windows Terminal |
| **Manutenção** | Mostra quanto ocupa e limpa: imagens sem uso, cache de build, logs e containers parados e volumes sem uso do Docker; cache do apt, journal, `~/.cache`, npm e `/tmp` antigo da distro. Liberar o cache de memória do Linux e compactar o disco virtual (`.vhdx`), devolvendo espaço ao C: |
| **Ajustes** | Trocar a distro; o que ligar ao abrir o app; Docker liga ou não junto com a distro; socket ou TCP; iniciar com o Windows; fechar para a bandeja; reabrir o assistente; lista do que o app alterou e **Desfazer tudo** |
| **Bandeja** | Clique esquerdo: painel rápido com o estado, Iniciar/Parar ambiente, memória, CPU e `vmmemWSL`, projetos (VS Code, terminal, iniciar/parar), terminal, desligar WSL e sair. Clique direito: menu com os favoritos (VS Code, terminal, Explorer), ligar/desligar Docker e o resto. Duplo clique: abre a janela |
| **Paleta de comandos** | `Ctrl+K` ou a caixa "Buscar ou executar…" no topo: ações do ambiente, projetos, containers (logs, abrir porta), pastas favoritas e todas as abas, filtradas pelo que você digita; `↑`/`↓` e `Enter` |
| **Assistente** | Escolha da distro (avisa se o Docker Desktop estiver instalado), verificação do Docker Engine e do systemd, grupo `docker`, conexão (socket ou TCP) com teste, iniciar com o Windows |

Atalhos gerais: `Ctrl+K` paleta de comandos · `Ctrl+1` Visão geral · `Ctrl+2` Containers · `Ctrl+3` Arquivos · `Ctrl+4` Terminal · `Ctrl+5` Manutenção · `Ctrl+,` Ajustes. Na aba Containers: `Ctrl+F` busca · `F5` atualiza · `Tab` percorre as linhas · `Enter` abre os logs · `Del` exclui · `Shift+F10` (ou clique direito) abre o menu do container.

Atalhos na aba Arquivos: `Ctrl+Shift+V` VS Code · `Ctrl+Shift+T` terminal · `Ctrl+Shift+E` Explorer · `Ctrl+Shift+C` copiar caminho · `Ctrl+Shift+N` nova pasta · `F2` renomear · `Del` excluir · `Ctrl+C/X/V` · `F5` atualizar · `Alt+↑` pasta acima · `Alt+←` voltar · `Enter` abrir.

## Acessibilidade

- Toda ação tem texto ou nome acessível. As ações de projeto são botões com rótulo. Os botões só com ícone mostram o nome rápido ao passar o mouse e têm nome para o Narrador. O menu de clique direito repete as ações com texto.
- As linhas de container podem ser usadas só pelo teclado. O Narrador lê "serviço, status, tempo ligado, imagem".
- A moldura de foco aparece só para quem navega com `Tab`.
- Status nunca depende só da cor: o selo sempre traz o texto ("Rodando", "Com erro"…).
- **Tamanho da interface** em Ajustes › Acessibilidade. Por padrão, segue Configurações do Windows › Acessibilidade › Tamanho do texto; dá para fixar entre 100% e 200%.
- O app usa um tema escuro próprio, independente do Windows, com contraste de texto de pelo menos 4,5:1. O tema claro e o alto contraste do Windows ainda não são seguidos.

## Consumo de recursos

O objetivo é não pesar quando o app só está na bandeja e gastar pouco quando a janela está aberta.

| Situação | O que roda |
| --- | --- |
| Na bandeja | Nada consulta o WSL nem o Docker. O app entra no **modo eficiência** do Windows 11 (prioridade baixa, EcoQoS) e, 3 s depois de esconder a janela, devolve ao Windows a memória ociosa (heap compactado + working set reduzido). As conexões com o Docker fecham sozinhas em 20 s. |
| Janela aberta | `wsl --list --running` a cada 10 s (não liga a distro) e um ping ao Docker numa conexão já aberta. |
| Visão geral, Containers ou painel da bandeja aberto | Uma conexão com o fluxo de **eventos** do Docker: a lista só é recarregada quando algo muda (sem eventos de healthcheck), e de 30 em 30 s para o "Ligado há". Os mesmos eventos alimentam a atividade recente. |
| Idem | **Um** processo de shell na distro lê `/proc` e os cgroups dos containers a cada 2 s, só com comandos internos do shell (~5 ms de CPU por leitura no Linux). Nada de uma requisição `stats` por container, nem do `docker` CLI. Todas as telas compartilham o mesmo processo e a mesma conexão, que fecham quando a última delas sai. |

Outros cuidados:
- Nenhuma conexão nova com o Docker abre se a distro estiver desligada: abrir uma religaria o WSL.
- Desligar o WSL espera uma consulta de estado nova antes de continuar.
- As animações de carregamento só rodam quando estão visíveis.
- O GC não roda em modo concorrente, o que economiza memória e threads num app de bandeja.
- O alvo `net10.0-windows` não traz as projeções WinRT (24 MB).
- O log de um container guarda no máximo 5.000 linhas, inclusive enquanto está pausado.

## Arquitetura

```
src/WslVision
├── Services/            único lugar que fala com o WSL e o Docker
│   ├── WslService       wsl.exe: distros, liga/desliga, scripts na distro
│   ├── Docker/          cliente HTTP da Docker Engine API + túnel dial-stdio
│   ├── FileService      operações de arquivo por dentro do Linux
│   ├── MetricsService   um único processo que lê /proc e os cgroups dos containers (compartilhado entre abas)
│   ├── LauncherService  VS Code, Windows Terminal, Explorer, área de transferência
│   ├── SetupService     passos do assistente e "desfazer tudo"
│   ├── WslConfigService .wslconfig com backup
│   ├── ActivityLog      atividade recente, traduzida dos eventos do Docker
│   ├── TrayService      ícone, painel rápido e menu da bandeja
│   └── AppState         distro ligada? Docker responde? (só com a janela visível)
├── ViewModels/          MVVM com CommunityToolkit.Mvvm; ContainerInventory (a lista de containers ao vivo,
│                        compartilhada) e EnvironmentViewModel (estado e ações do ambiente) servem todas as telas
├── Views/               WPF + WPF-UI, com o tema escuro do produto por cima
└── Themes/
    ├── Tokens.xaml      cores do produto (fundo, superfícies, texto, destaque, estados)
    └── Styles.xaml      botões, cartões, menu lateral, listas
tests/WslVision.Tests    xUnit: caminhos, status e eventos do Docker, edição do .wslconfig
```

A interface nunca executa comandos direto: ela chama os serviços.

### Decisões técnicas (e onde elas diferem do escopo original)

- **Conexão com o Docker pelo socket, não por TCP aberto.** O padrão é um túnel `docker system dial-stdio` rodando dentro da distro via `wsl.exe`. O app fala HTTP pelo stdin/stdout desse processo, então nenhuma porta fica aberta. A API em `tcp://127.0.0.1:2375` continua disponível como opção em Ajustes, com aviso: mesmo só no localhost, qualquer programa do PC (inclusive uma página web, via DNS rebinding) ganharia controle de root da distro.
- **Modo TCP via override do systemd** (`/etc/systemd/system/docker.service.d/wslvision-tcp.conf`), e não via `hosts` no `daemon.json`. Isso porque o `docker.service` do Debian já passa `-H fd://`, e a combinação impede o dockerd de subir.
- **Cliente HTTP próprio em vez do Docker.DotNet.** São ~300 linhas com só os endpoints usados. Ficam de fora uma dependência grande e o problema de plugar o túnel de stdio nela.
- **.NET 10** (LTS). O .NET 8 sai de suporte em novembro de 2026.
- **Sem trimming.** O WPF não suporta (`NETSDK1168`). Por isso existe a edição leve, que usa o runtime instalado (~10 MB).
- **Nada acorda o WSL sozinho.** O estado é consultado com `wsl --list --running`, que não liga a distro. Métricas e consultas ao Docker só rodam com a janela visível e a aba aberta. Ligar a distro é sempre uma ação do usuário.
- **VS Code pelo protocolo `vscode://`**, não pelo `code.cmd`. Um `.cmd` passa pelo `cmd.exe`, que interpretaria `&`, `|` e `%` de nomes de pasta como comandos; na URI, o caminho vai codificado.
- **Argumentos chegam em base64.** Todo caminho ou texto do usuário que vai para a distro passa por esse script, então espaços, aspas, `$` e `;` nunca são interpretados pelo shell ou pelo Windows.
- **Arquivos por dentro do Linux** (`find`, `mv`, `cp -a`, `rm`), em vez de `System.IO` sobre `\\wsl.localhost`. É mais rápido em pastas grandes e dá dono/permissão corretos. Quando falta permissão, o app pergunta e refaz como root (`wsl -u root`, sem senha nem administrador).
- **Grupo `docker`:** depois de adicionar o usuário, o assistente reinicia a distro uma vez, porque sessões já abertas não enxergam o grupo novo.

## Segurança

- O app nunca pede administrador (`asInvoker` no manifesto). A única exceção é compactar o disco virtual: o `diskpart` precisa de administrador, então o Windows pede permissão na hora, depois de uma confirmação no app.
- Só executa ações pré-definidas; não há campo de comando livre fora do terminal.
- Root só com confirmação explícita (arquivos protegidos, terminal como root, mudanças do assistente).
- Exclusões sempre pedem confirmação. Pastas essenciais (`/`, `/etc`, `/usr`, a home…) não podem ser apagadas, renomeadas nem movidas pelo app.
- Abrir pelo navegador de arquivos um programa ou script do Windows (`.exe`, `.bat`, `.cmd`, `.ps1`, `.lnk`…) pede confirmação, porque abrir é executar.
- No Linux, `\` faz parte do nome do arquivo e nunca é tratado como separador de pasta. Sem isso, um arquivo chamado `..\..` faria a exclusão apagar uma pasta acima.
- Toda alteração no sistema fica registrada em `%AppData%\WslVision\settings.json` e pode ser desfeita em **Ajustes › Desfazer tudo**: grupo docker, início automático do Docker, systemd no `/etc/wsl.conf` (com backup), override TCP e `.wslconfig` (com backup).

## Estado atual

- Compila sem avisos; 126 testes de unidade passam.
- Testado em Linux/WSL contra um Docker Engine real (somente leitura): o túnel dial-stdio, ping, versão, listagem com status traduzido, CPU/memória por container e logs ao vivo. Também o fluxo de eventos com o filtro e o loop de métricas por cgroup, contra os containers reais. Os scripts de shell (arquivos, terminal, detecção do assistente, métricas) também foram testados no `dash`, com nomes difíceis (espaços, aspas, `$`, acentos, nomes começando com `-`).
- Um teste manual no Windows já confirmou que o app abre e lista containers e arquivos. As mudanças desde então (grupos, Iniciar ambiente, acessibilidade, modo eficiência, terminal embutido, Manutenção, o novo visual com a Visão geral, a paleta de comandos e o painel da bandeja) ainda precisam de um teste lá.

## Próximas versões

- Listar imagens e volumes um a um (a limpeza dos não usados já está em Manutenção).
- Atalhos de teclado configuráveis (hoje são fixos).
- Várias distros ao mesmo tempo (hoje: uma principal, trocável em Ajustes).
- Inglês além do português; instalador e publicação no winget.

## Contribuindo

Issues e pull requests são bem-vindos. Antes de mandar um PR:

- Rode `dotnet test tests/WslVision.Tests` e confira se o build não gera avisos.
- Mantenha a regra da arquitetura: a interface nunca executa comandos direto, só chama os serviços.
- Nada pode ligar o WSL sozinho nem pedir administrador sem uma ação explícita do usuário.
- Para mudanças grandes, abra uma issue antes para conversarmos sobre a ideia.

## Licença

Distribuído sob a licença [MIT](LICENSE). WSL, Windows e Windows Terminal são marcas da Microsoft; Docker é marca da Docker, Inc. Este projeto não tem vínculo com nenhuma das duas.
