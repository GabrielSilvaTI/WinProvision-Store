# Orientações de interface

- Leia `DESIGN.md` antes de alterar a aparência de uma página, janela ou controle.
- O cliente usa WPF-UI 4.3.0. Prefira seus controles no namespace `ui` quando houver um equivalente adequado.
- Mantenha cores, tipografia, espaçamento, superfícies e estilos compartilhados em `WinProvision.Store/Styles/` e `App.xaml`; páginas devem consumir esses recursos em vez de criar variações locais sem necessidade.
- Preserve bindings, nomes XAML, eventos, acessibilidade por teclado e a lógica existente no code-behind ao reorganizar a interface.
- Use `DynamicResource` para cores que devem acompanhar o tema. Descrições devem quebrar linha e permanecer legíveis em larguras menores.
- Mantenha os textos da interface em português e use linguagem direta.
