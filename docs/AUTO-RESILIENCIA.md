# Retomada e diagnóstico do WinProvision Orchestrator

O modo `/auto` grava um checkpoint local identificado pela origem do perfil (caminho ou URL). IDs e opções de cada item são registrados separadamente para que alterações em outros itens não apaguem o que já terminou. Ele fica em:

```text
%LOCALAPPDATA%\WinProvision\AutoRuns\<hash-da-origem>.json
```

O checkpoint contém o estado das etapas e itens, método usado, código de saída, duração e caminho do log. Ele não armazena o JSON do perfil, URLs privadas ou credenciais.

## Como a retomada funciona

- Itens concluídos são preservados quando a mesma execução é retomada.
- Falhas em que o instalador comprovadamente não iniciou podem ser tentadas na próxima execução.
- Se o processo reiniciar enquanto um instalador estiver em andamento, o item fica como resultado incerto e não é iniciado novamente. O `/auto` continua com os outros itens seguros e mostra o aviso para conferir o aplicativo.
- Ajustes do Windows podem ser reaplicados após interrupção porque são operações idempotentes.
- Itens com o mesmo ID, origem e versão fixada mantêm o estado ao atualizar o JSON na mesma URL. Uma mudança nas opções de um item cria uma identidade nova para ele.
- Uma execução concluída continua marcada como concluída; chamar a mesma origem novamente não repete os itens já registrados.
- A retomada usa a conta Windows que gravou o arquivo. Se o `/auto` mudar de usuário para `SYSTEM` (ou vice-versa), o `%LOCALAPPDATA%` também muda.

## Pré-validação

Antes de aquecer o WinGet ou iniciar downloads, o `/auto` valida IDs, origens, duplicatas, opções de Office e ajustes de provisionamento. Perfis Office precisam de pelo menos 6 GB livres para começar a preparação. Perfis de pacotes exigem no mínimo 2 GB livres na unidade do Windows; como o perfil não inclui o tamanho de cada instalador, pacotes grandes ainda podem precisar de mais espaço.

Alterar o nome do computador requer reinicialização. Códigos de saída `3010` e `1641` também são registrados como indicação de reinicialização necessária.

## Elevação e políticas

O log identifica se a execução está como usuário, administrador ou `SYSTEM`, lista as ações que podem pedir elevação e diferencia UAC recusado, falta de permissão, bloqueio por política e falha do instalador quando o motor fornece essa informação. Fallbacks mudam o método de instalação; não removem políticas de TI, WDAC ou AppLocker.

## Progresso e logs

A barra representa itens concluídos. O detalhe mostra a fase real (`preparando`, `baixando`, `instalando` ou `verificando`) e exibe um percentual somente quando o motor o reporta. O resumo final informa método, código de saída, duração e caminho do log por item. Em um item interrompido, a duração aparece como indisponível quando não foi possível medi-la com precisão.
