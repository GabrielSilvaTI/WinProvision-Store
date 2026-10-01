# Worker de logs do WinProvision

Este diretório contém uma versão legível do Worker enviado pelo usuário, com a página
`/view` redesenhada conforme `DESIGN.md`. Os endpoints usados pelo aplicativo continuam
os mesmos:

- `POST /push?session=ID` recebe as linhas do Orchestrator.
- `GET /stream?session=ID` transmite as linhas por Server-Sent Events.
- `GET /view?session=ID` exibe a página de acompanhamento.

## Implantação no painel Cloudflare

O código preserva o nome exportado da Durable Object (`SessionLog`), a binding
`SESSION_LOG` e a chave de armazenamento `logs`. Ao substituir o script no painel,
mantenha a binding e as migrações/configurações atuais do Worker. Não crie outra classe
ou binding durante essa atualização, pois as sessões existentes usam esses recursos.

Depois de colar e revisar `index.js` em **Workers & Pages → winprovision-logs → Edit
Code**, publique a versão. O arquivo pode também ser conectado a um repositório separado
para manter as próximas alterações versionadas.

Esta alteração é apenas do Worker de logs e não altera os binários do aplicativo.
