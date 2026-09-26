# WinProvision Store — sistema visual

## Direção

Seguir a linguagem visual da Microsoft Store do Windows 11 com WPF-UI 4.3.0 e CommunityToolkit.Mvvm. Todas as telas compartilham os mesmos tokens, cartões arredondados, botões pill azuis, tipografia Segoe UI Variable e navegação previsível. A interface prioriza respiro, hierarquia e linhas de leitura confortáveis.

## Cores

- Fundo principal: carvão `#FF202020`; navegação: `#FF252525`.
- Cartões: `#FF303030`; superfície elevada: `#FF383838`; bordas discretas: `#FF454545`.
- Texto primário: `#F5F5F5`; secundário: `#D0D0D0`; terciário: `#A8A8A8`.
- Acento de ação: azul Microsoft `#0078D4`, aplicado a seleção, foco e ação principal.
- Estados de sucesso, atenção e erro usam cores semânticas próprias; não substituem o azul de navegação e ação.
- Barras de progresso por método têm cores fixas: API COM roxa (#B991FF), WinProvision API laranja (#FF9A3D) e WinGet CLI azul (#1688E8). Operações ainda não identificadas usam azul.
- Use os brushes dinâmicos do tema para texto, controles e estados. Evite cores hexadecimais em páginas.

## Tipografia

- Família: Segoe UI Variable, com Segoe UI como fallback.
- Título de página: 28 px, Semibold, usando `FluentPageTitleStyle`.
- Título de cartão: 15–16 px, Semibold.
- Texto de leitura: 13–14 px; metadados e descrições auxiliares: 11–12 px.
- Descrições longas quebram linha. Dados tabulares podem usar números monoespaçados ou tabulares quando disponíveis.

## Geometria e ritmo

- Espaçamento usa uma escala baseada em 8 px, com subdivisões de 4 px para ajustes finos.
- Cartões usam raio de 14 px; botões e filtros usam formato pill. Controles não interativos usam raio de 8 px.
- Páginas usam margem de conteúdo compartilhada e cabeçalhos alinhados à mesma coluna.
- Cabeçalhos usam o mesmo bloco: ícone WPF-UI em tile de 40 px, título compartilhado e subtítulo de 14 px; a faixa abaixo mantém 20 px de respiro.
- Barras de ações usam `PageToolbarContainerStyle` sem cartão de fundo ou contorno, controles de 34 px e a mesma ordem visual: ações principais, seleção e comandos auxiliares.
- Rodapés de catálogo usam `PageStatusBarStyle` sem fundo ou contorno e altura fixa de 32 px, conforme Descobrir. Rótulos, combos, botões e ícones permanecem centralizados nessa altura. Quando disponíveis, mantêm a mesma ordem: status, classificação e três modos (lista/tabela, cartões e ícones), com `PageViewModeToggleStyle`.
- Páginas com status usam o ícone `Info24` compartilhado. Controles próprios da página podem ocupar o lado direito sem mudar o alinhamento do status.
- Listas usam linhas altas com ícone de 32 px, título, subtítulo e ação alinhada à direita. Versões e estados aparecem em badges pill.
- Coleções usam cartões com a mesma hierarquia de Descobrir. Filtros de origem usam pills horizontais com rolagem, nunca listas de checkboxes.
- Toolbars mostram uma ação principal e, no máximo, uma ação auxiliar; comandos restantes ficam no menu “Mais ações”.
- Cabeçalhos usam título grande em negrito e subtítulo secundário com quebra de linha.

## Componentes e estados

- Use `ui:NavigationView`, `ui:TitleBar`, `ui:Button`, `ui:TextBox`, `ui:SymbolIcon` e demais controles WPF-UI existentes.
- A navegação mostra claramente seleção, hover e foco. Ações primárias usam o acento; ações auxiliares usam aparência secundária ou transparente.
- Superfícies compartilham bordas suaves e contraste suficiente; sombras são sutis e reservadas a camadas realmente elevadas.
- Tokens de cor, tipografia, espaçamento e geometria ficam centralizados em `WinProvision.Store/Styles/AppStyles.xaml`; telas consomem esses recursos sem criar variações locais.
- Controles interativos têm feedback de hover, pressionamento, foco e desabilitado. Carregamento, erro e estado vazio devem permanecer explícitos.
- Preserve layout responsivo: textos podem crescer verticalmente, ações podem quebrar ou reposicionar e tabelas devem manter seus comandos acessíveis.
