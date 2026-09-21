using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace WinProvision.Core.Services;

/// <summary>
/// Registro central de TODA execução do winget.exe feita pelo app. A Store liga o <see cref="Sink"/>
/// ao log de diagnóstico; cada linha traz os argumentos e a cadeia de chamadores, o que responde
/// "quem abriu esse winget.exe?" (instalação, listagem, atualização, checagem de versão...).
/// </summary>
public static class WingetCliAudit
{
    public static Action<string>? Sink { get; set; }

    public static void Launch(string fileName, string arguments)
    {
        var sink = Sink;
        if (sink is null)
        {
            return;
        }

        try
        {
            var chain = string.Join(
                " <- ",
                new StackTrace(1, false).GetFrames()
                    .Select(frame => frame.GetMethod())
                    .Where(method => method?.DeclaringType is not null &&
                                     method.DeclaringType != typeof(WingetCliAudit))
                    .Select(method => Describe(method!))
                    .Distinct()
                    .Take(6));
            sink($"WINGET.EXE LAUNCH file={fileName} args=\"{arguments}\" origem={chain}");
        }
        catch
        {
            // Auditoria nunca pode alterar o comportamento da execução.
        }
    }

    private static string Describe(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = method.Name;

        // Máquinas de estado async e lambdas: <Metodo>d__N / <>c__DisplayClass.
        if (type.Name.StartsWith('<'))
        {
            var end = type.Name.IndexOf('>');
            if (end > 1)
            {
                name = type.Name[1..end];
            }

            type = type.DeclaringType ?? type;
        }

        return $"{type.Name}.{name}";
    }
}
