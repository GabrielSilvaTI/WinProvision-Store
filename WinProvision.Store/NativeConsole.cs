using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinProvision.Store;

/// <summary>
/// O executável é WinExe (sem console próprio, janela normal) — sem isso, todo
/// Console.WriteLine some no vazio mesmo quando o app é chamado de dentro de um
/// terminal (ex.: "WinProvision.Store.exe /auto perfil.json" no cmd/PowerShell).
/// AttachConsole(ATTACH_PARENT_PROCESS) reaproveita o console de quem chamou o
/// processo, pra dar feedback visível no modo CLI (ver App.xaml.cs).
///
/// Efeito colateral de ser WinExe: o cmd.exe/PowerShell que chamou o processo NÃO
/// espera por ele (só espera processos console "de verdade") — ele já devolve o
/// prompt e fica bloqueado dentro de um ReadConsole esperando uma linha de teclado
/// de verdade. Isso é o motivo de precisar apertar Enter depois que a instalação
/// termina: não é o nosso processo que está esperando, é o shell pai. ReleaseParentPrompt()
/// resolve isso injetando um Enter sintético no buffer de input do console.
/// </summary>
internal static class NativeConsole
{
    private const int AttachParentProcess = -1;
    private const int StdInputHandle = -10;
    private const ushort KeyEvent = 0x0001;
    private const ushort VkReturn = 0x0D;
    private const ushort ScanCodeReturn = 0x1C;

    private const uint WM_CLOSE = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord KeyEvent;
    }

    private static bool _attached;

    /// <summary>
    /// Tenta anexar ao console do processo pai. Sem efeito (e sem erro) se não houver
    /// um — ex.: o app foi iniciado por duplo clique, ou programaticamente via
    /// Process.Start com saída redirecionada (nesse caso não existe console pra anexar,
    /// e é exatamente o cenário de acoplar isso ao WinProvision principal).
    /// </summary>
    public static void AttachToParentIfAvailable()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        _attached = true;

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(stderr);
    }

    /// <summary>
    /// Injeta um Enter sintético no buffer de input do console anexado, destravando
    /// sozinho o ReadConsole do shell pai (ver comentário da classe). Chame isso uma
    /// vez, logo antes de encerrar o processo. Sem efeito se AttachToParentIfAvailable
    /// não conseguiu anexar a nenhum console.
    /// </summary>
    public static void ReleaseParentPrompt()
    {
        if (!_attached)
        {
            return;
        }

        try
        {
            IntPtr stdIn = GetStdHandle(StdInputHandle);
            if (stdIn == IntPtr.Zero || stdIn == new IntPtr(-1))
            {
                return;
            }

            var records = new[] { MakeKeyEvent(true), MakeKeyEvent(false) };
            WriteConsoleInput(stdIn, records, (uint)records.Length, out _);
        }
        catch
        {
            // Best effort só — nunca deve impedir o processo de encerrar por causa disso.
        }
    }

    /// <summary>
    /// Fecha a janela do console anexado (o cmd/PowerShell que chamou o /auto), como
    /// se o usuário tivesse clicado no X dela — encerra o conhost/Terminal e, junto
    /// com ele, o próprio powershell.exe hospedado. Só faz algo se
    /// AttachToParentIfAvailable() conseguiu anexar a um console de verdade (chamada
    /// via clique duplo não tem console pra fechar, e isso não tem efeito nesse caso).
    /// Chame isso depois que a AutoWindow já tiver fechado (ver App.xaml.cs), pra não
    /// derrubar o console enquanto a UI de progresso ainda está visível.
    /// </summary>
    public static void CloseParentConsole()
    {
        if (!_attached)
        {
            return;
        }

        try
        {
            IntPtr consoleWindow = GetConsoleWindow();
            if (consoleWindow == IntPtr.Zero)
            {
                return;
            }

            PostMessage(consoleWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Best effort só — nunca deve impedir o processo de encerrar por causa disso.
        }
    }

    private static InputRecord MakeKeyEvent(bool keyDown) => new()
    {
        EventType = KeyEvent,
        KeyEvent = new KeyEventRecord
        {
            bKeyDown = keyDown,
            wRepeatCount = 1,
            wVirtualKeyCode = VkReturn,
            wVirtualScanCode = ScanCodeReturn,
            UnicodeChar = '\r',
            dwControlKeyState = 0
        }
    };
}
