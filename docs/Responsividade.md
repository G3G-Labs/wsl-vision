# Responsividade

Out 23, 2026 · vale para o app (WPF) e para o canvas de design

O WSL Vision precisa funcionar em meia tela (Snap) e com a interface
ampliada (Ajustes › Tamanho da interface, ou o Tamanho do texto do
Windows). Nos dois casos sobra menos largura útil, então as telas se
reorganizam em vez de cortar conteúdo.

## A régua

A largura que conta é a da **área da página** (a janela menos o menu
lateral de 72), já descontada a escala da interface. Um notebook de
1366 px com a interface em 150% tem cerca de 840 de largura útil.

| Faixa | Largura da página | Exemplo |
|---|---|---|
| Larga | ≥ 1100 | janela padrão (1280) a 100% |
| Média | 840–1099 | janela padrão a 125%; tela cheia de 1366 a 125% |
| Compacta | < 840 | meia tela; interface a 150% ou mais |

A janela vai até 640 de largura a 100% (`LayoutMath.WindowMinWidth`,
multiplicado pela escala). Antes eram 900, o que impedia a meia tela em
monitores de 1366 e 1600 px. O canvas usa as mesmas faixas, com
`@container` na área da página (`main.pg`). Abaixo de 640 px de viewport
o canvas também cobre o celular (o menu vira uma linha no topo), o que o
app não precisa.

## As peças (src/WslVision/Views/Layout)

| Peça | Para quê |
|---|---|
| `Responsive.Track` | Mede a largura de uma área e passa a faixa para dentro em `Responsive.Size` (herdado). A janela mede a página; uma lista pode medir de novo com limites próprios (`MediumBelow`, `CompactBelow`). |
| `WideOnly`, `HideCompact` (Styles.xaml) | Somem fora da faixa larga ou na compacta. Use em `Border` e painéis; um `TextBlock` sem estilo vai dentro de uma `Border`. |
| `AdaptiveGrid` | Colunas iguais: quantas couberem com `MinColumnWidth`, até `MaxColumns`, só nos números de `ColumnSteps` (ex.: `1,2,4`, para quatro cartões nunca ficarem 3 + 1). |
| `SplitPanel` | Dois blocos lado a lado que se empilham abaixo de `StackBelow`. Dentro dele, `SplitPanel.IsStacked` serve aos estilos, e o conversor `EndAlign` passa as ações da direita para a esquerda. |
| `LayoutMath` | As contas, sem WPF (testadas em `LayoutMathTests`). |

## O que muda em cada tela

O que depende da faixa da página está nas colunas Média e Compacta. Os
blocos que empilham (`SplitPanel`) e as tabelas medem a própria largura;
os números entre parênteses são essa largura, não a da página.

| Tela | Média | Compacta |
|---|---|---|
| Barra de título | — | A busca vira só o ícone quando não cabe ao lado da marca. |
| Visão geral | Somem CPU e RAM dos projetos. | Some a legenda "Com o ambiente". Os quatro números ficam em 2 colunas quando não cabem 4. O resumo desce para baixo dos projetos (< 820), e a ação do ambiente desce para baixo do estado, à esquerda (< 600). |
| Containers | Os logs descem para baixo da lista (< 980). Na lista: somem "ligado há" e CPU (< 940). | Na lista: somem porta e memória e o status fica só com o ponto (< 640). VS Code e Terminal ficam só com o ícone. As ações do projeto descem para baixo do nome (< 720). |
| Arquivos | As ações descem para baixo do caminho (< 820). Na lista: some Dono (< 620). | Os locais viram uma fileira em cima da pasta (< 760). Na lista: some Modificado (< 460). |
| Terminal | Some a pasta da aba no cabeçalho e a dica de atalhos na barra de status. A barra de workspaces recolhe para 60 px pelo botão do topo (só os pontos de estado). | Some o projeto Compose da barra de status. A configuração da aba e do workspace fica em uma coluna (< 576). |
| Manutenção | — | Os quatro cartões viram uma coluna (< 776). |
| Ajustes | Duas colunas que ocupam a largura toda (uma só abaixo de 900). | As listas de opções descem para baixo do texto (< 460). |
| Sobre | O app e a empresa lado a lado (um embaixo do outro abaixo de 820). | — |

A paleta (Ctrl+K) agora acompanha o tamanho da interface e encolhe com a
janela. Enquanto ela está aberta, os terminais embutidos ficam ocultos:
eles são janelas nativas e a cobririam.

## Como testar no Windows

1. Arraste a janela até o mínimo e use Win+← (meia tela): nada pode ficar
   cortado nem sobreposto, em nenhuma aba.
2. Ajustes › Tamanho da interface em 150% e 200%, com a janela maximizada
   e em meia tela.
3. Containers com os logs abertos, de 1280 até o mínimo: as colunas somem
   na ordem da tabela acima.
4. Terminal com uma aba aberta: em meia tela, o botão Painel mostra e
   esconde o painel, e Ctrl+K abre a paleta por cima do terminal.
