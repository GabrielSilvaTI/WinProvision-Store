using System;
using QRCoder;

namespace WinProvision.Core.Services;

/// <summary>
/// Codifica QR Codes usando a implementação QRCoder, que aplica o padrão completo
/// (blocos Reed-Solomon, máscaras e informação de formato) para URLs de qualquer tamanho.
/// </summary>
internal static class QrEncoder
{
    /// <summary>Retorna a matriz do QR Code, onde <see langword="true"/> representa módulo escuro.</summary>
    public static bool[,] Encode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        using QRCodeData data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        int size = data.ModuleMatrix.Count;
        var modules = new bool[size, size];

        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
                modules[row, column] = data.ModuleMatrix[row][column];
        }

        return modules;
    }
}
