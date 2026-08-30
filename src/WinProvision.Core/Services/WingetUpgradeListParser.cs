using System.Linq;
using System.Text.RegularExpressions;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Faz o parsing da saída de texto de <c>winget upgrade</c> (tabela alinhada por coluna).
///
/// Diferente de <c>winget export</c>, o <c>winget upgrade</c> não tem uma opção de saída
/// estruturada (JSON) — só imprime uma tabela de texto formatada pro console. Esse parser
/// usa a técnica padrão pra esse cenário: acha a linha de cabeçalho (a que vem logo antes
/// da linha de traços "----...") e usa a posição inicial de cada palavra do cabeçalho como
/// o limite de cada coluna nas linhas de dados seguintes — como as colunas do winget são
/// alinhadas com espaços (padding), o valor de cada linha de dados começa na mesma posição
/// que o título da coluna correspondente.
///
/// Limitação conhecida: assume que cada cabeçalho de coluna é uma única palavra (verdade em
/// pt-BR e en-US: "Nome/Id/Versão/Disponível/Origem" e "Name/Id/Version/Available/Source").
/// Se o winget mudar esse formato de tabela no futuro, ou o header vier em um idioma com
/// título de coluna com espaço, o parsing simplesmente retorna uma lista vazia (falha segura
/// — nunca lança, e a UI trata "0 atualizações encontradas" nesse caso).
/// </summary>
public static class WingetUpgradeListParser
{
    public static List<UpgradablePackage> Parse(string rawOutput)
    {
        var result = new List<UpgradablePackage>();

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return result;
        }

        string[] lines = rawOutput.Replace("\r\n", "\n").Split('\n');

        int separatorIndex = Array.FindIndex(lines, line =>
        {
            string trimmed = line.Trim();
            return trimmed.Length >= 5 && trimmed.All(c => c == '-');
        });

        if (separatorIndex <= 0)
        {
            // Sem tabela reconhecível (ex.: "Nenhuma atualização disponível.") — lista vazia é o resultado correto aqui.
            return result;
        }

        string headerLine = lines[separatorIndex - 1];
        var headerMatches = Regex.Matches(headerLine, @"\S+");

        if (headerMatches.Count < 3)
        {
            return result;
        }

        int[] columnStarts = headerMatches.Select(m => m.Index).ToArray();

        for (int i = separatorIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                // Linha em branco = fim da tabela (o rodapé de resumo do winget vem depois dela).
                break;
            }

            if (line.Length <= columnStarts[1])
            {
                // Linha curta demais pra conter nem a 2ª coluna — não é mais uma linha de dados
                // (provavelmente já é texto de rodapé tipo "N atualizações disponíveis.").
                break;
            }

            string[] columns = SliceByColumns(line, columnStarts);

            string name = columns[0].Trim();
            string id = columns[1].Trim();

            if (string.IsNullOrWhiteSpace(id))
            {
                break;
            }

            string currentVersion = columns.Length > 2 ? columns[2].Trim() : string.Empty;
            string availableVersion = columns.Length > 3 ? columns[3].Trim() : string.Empty;
            string source = columns.Length > 4 ? columns[^1].Trim() : string.Empty;

            result.Add(new UpgradablePackage
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                CurrentVersion = currentVersion,
                AvailableVersion = availableVersion,
                Source = source
            });
        }

        return result;
    }

    private static string[] SliceByColumns(string line, int[] columnStarts)
    {
        var columns = new string[columnStarts.Length];

        for (int i = 0; i < columnStarts.Length; i++)
        {
            int start = Math.Min(columnStarts[i], line.Length);
            int end = i < columnStarts.Length - 1 ? Math.Min(columnStarts[i + 1], line.Length) : line.Length;

            columns[i] = end > start ? line[start..end] : string.Empty;
        }

        return columns;
    }
}
