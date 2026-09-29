# Banco de screenshots

`screenshot-database-clean.json` é a cópia limpa fornecida pelo mantenedor a partir do banco público de screenshots do UniGetUI. As chaves de origem são slugs do UniGetUI, não IDs WinGet.

O workflow `winget-sync` associa somente chaves com correspondência exata e única ao `PackageIdentifier` WinGet. Os objetos são publicados no R2 em `Store/Screenshot_Database/<id-winget-em-minúsculas>/<sha256>.<extensão>`, e o catálogo `apps.json` recebe `screenshotUrls`. Casos sem correspondência única ficam no relatório do workflow para revisão.
