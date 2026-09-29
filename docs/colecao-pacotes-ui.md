# Coleção de Pacotes: elementos e práticas

Resumo da interface implementada em `WinProvision.Store/PackagesPage.xaml`, com referências à documentação oficial do [WPF-UI](https://wpfui.lepo.co/).

## Elementos da tela

| Área | Elementos | Para que servem |
|---|---|---|
| Cabeçalho | Ícone `Layer24`, título e descrição | Identifica a página e explica seu propósito. Reutiliza estilos compartilhados de título e ícone. |
| Barra de ações | Alternar filtros, `SplitButton` para instalar, Detalhes, Recarregar e menu Mais | Mantém as ações frequentes visíveis e reúne comandos adicionais no menu. A seta do `SplitButton` oferece variações da instalação. |
| Painel de filtros | `Expander` para fontes, filtros/perfil, estimativas e pesquisa | Agrupa opções por assunto e permite recolher áreas para liberar espaço. Fontes usam `CheckBox`; a busca usa `ui:AutoSuggestBox`. |
| Conteúdo | Cabeçalho de tabela e três modos: lista, cartões e ícones | Apresenta os mesmos pacotes em densidades diferentes, preservando ações, seleção e detalhes. |
| Linha ou cartão | Nome, editor, ID, versão, origem, tamanho e ações | Usa `DataTemplate` para repetir a mesma estrutura por pacote. A seleção é indicada por check e realce verde sutil. O menu de contexto oferece ações relacionadas ao pacote. |
| Estado vazio | Ícone, mensagem e ações para abrir uma coleção JSON ou criar perfil | Informa o que ocorreu e oferece próximos passos claros. |
| Rodapé | Resumo da seleção, estado, classificação e botões de visualização | Mostra a quantidade selecionada e a ação que será aplicada, além de permitir ordenar e alternar o modo de exibição. |

## Práticas de interface e implementação

- **Controles WPF-UI:** a página usa `ui:Button`, `ui:SplitButton`, `ui:AutoSuggestBox` e `ui:SymbolIcon` para ações, pesquisa e ícones. `SplitButton` combina uma ação principal com um menu próprio; `SymbolIcon` usa os símbolos Fluent disponíveis no pacote.
- **Virtualização:** as listas usam `VirtualizingStackPanel`; cartões e ícones usam `ui:VirtualizingWrapPanel`. Os `ItemsControl` habilitam virtualização e reciclagem de contêineres para limitar elementos visuais criados quando há muitos pacotes.
- **Modelos reutilizáveis:** `DataTemplate` separa a apresentação de cada pacote da estrutura da página. Os eventos de clique e menus encaminham as ações ao code-behind existente.
- **Estados legíveis:** seleção, hover, foco, desabilitado, coleção vazia e busca sem resultados têm apresentações próprias. Cores e superfícies são obtidas por recursos dinâmicos compartilhados, acompanhando o tema.
- **Acessibilidade e entrada:** a caixa de pesquisa declara `AutomationProperties.Name`, oferece botão para limpar o texto e aceita envio da consulta. Os controles mantêm dicas de ferramenta e alvos de interação explícitos.
- **Consistência:** título, toolbar, barra de status, raio de borda, tipografia e modos de exibição consomem estilos de `AppStyles.xaml` e recursos compartilhados, evitando variações desnecessárias na página.

## Documentação oficial consultada

- [WPF-UI: `Button`](https://wpfui.lepo.co/api/Wpf.Ui.Controls.Button.html)
- [WPF-UI: `SplitButton`](https://wpfui.lepo.co/api/Wpf.Ui.Controls.SplitButton.html)
- [WPF-UI: `AutoSuggestBox`](https://wpfui.lepo.co/api/Wpf.Ui.Controls.AutoSuggestBox.html)
- [WPF-UI: `SymbolIcon`](https://wpfui.lepo.co/api/Wpf.Ui.Controls.SymbolIcon.html)
- [WPF-UI: `VirtualizingWrapPanel`](https://wpfui.lepo.co/api/Wpf.Ui.Controls.VirtualizingWrapPanel.html)
