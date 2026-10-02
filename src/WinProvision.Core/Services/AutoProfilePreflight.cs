using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Models.Provisioning;

namespace WinProvision.Core.Services;

internal sealed record AutoPreflightResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

[SupportedOSPlatform("windows")]
internal static class AutoProfilePreflight
{
    private static readonly Regex WingetIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private const long OfficeMinimumFreeBytes = 6L * 1024 * 1024 * 1024;
    private const long PackageMinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    public static AutoPreflightResult Validate(ProfileManifest manifest)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int officeCount = 0;

        for (int index = 0; index < manifest.Apps.Count; index++)
        {
            var app = manifest.Apps[index];
            string id = app.Id?.Trim() ?? string.Empty;
            string source = app.OfficeOptions is not null
                ? "office"
                : app.Source?.Trim().ToLowerInvariant() ?? (id.Length == 9 && id.All(char.IsLetterOrDigit) ? "msstore" : "winget");

            if (app.OfficeOptions is { } officeOptions)
            {
                officeCount++;
                if (!seen.Add("office\u001f" + AutoInstallCliService.OfficeIdentityKey(app)))
                    warnings.Add($"Plano Office duplicado detectado: {officeOptions.ProductId}; a referência repetida será processada apenas uma vez.");
                if (!AutoInstallCliService.ValidateOfficeOptions(officeOptions, out string? officeError))
                    errors.Add($"Item {index + 1} ({id}): {officeError}");
                continue;
            }

            if (id.Length == 0 || !WingetIdPattern.IsMatch(id))
            {
                errors.Add($"Item {index + 1}: ID '{id}' inválido. Use apenas letras, números, ponto, hífen ou sublinhado.");
                continue;
            }

            if (source is not ("winget" or "msstore"))
                errors.Add($"Item {index + 1} ({id}): origem '{source}' inválida; use 'winget' ou 'msstore'.");
            else if (source == "msstore" && (id.Length != 9 || !id.All(char.IsLetterOrDigit)))
                errors.Add($"Item {index + 1} ({id}): ID da Microsoft Store deve ter 9 caracteres alfanuméricos.");

            if (!seen.Add(source + "\u001f" + id))
                warnings.Add($"ID duplicado detectado: {id} ({source}); ele será processado apenas uma vez.");
        }

        ValidateProvisioning(manifest.Provisioning, errors);

        if (manifest.Apps.Any(app => app.OfficeOptions is null))
        {
            try
            {
                string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var drive = new DriveInfo(Path.GetPathRoot(systemRoot)!);
                if (drive.AvailableFreeSpace < PackageMinimumFreeBytes)
                    errors.Add($"Espaço insuficiente para os pacotes: {drive.AvailableFreeSpace / (1024d * 1024 * 1024):0.0} GB disponíveis em {drive.Name}; são necessários pelo menos 2 GB livres antes de iniciar downloads.");
                else
                    warnings.Add("O perfil não contém o tamanho exato de cada instalador; foi confirmado o mínimo de 2 GB livres, mas um pacote grande ainda pode exigir mais espaço.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"Não foi possível consultar o espaço livre para os pacotes: {ex.Message}");
            }
        }

        if (officeCount > 0)
        {
            try
            {
                string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvision", "Office");
                var drive = new DriveInfo(Path.GetPathRoot(cacheRoot)!);
                if (drive.AvailableFreeSpace < OfficeMinimumFreeBytes)
                    errors.Add($"Espaço insuficiente para preparar o Office: {drive.AvailableFreeSpace / (1024d * 1024 * 1024):0.0} GB disponíveis em {drive.Name}; são necessários pelo menos 6 GB livres antes do download.");
                else
                    warnings.Add("O Office pode exigir vários GB adicionais durante a instalação; o espaço livre será verificado novamente pelo ODT.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"Não foi possível consultar o espaço livre para o Office: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Provisioning?.MachineName))
            warnings.Add("A alteração do nome do computador requer reinicialização para entrar em vigor.");

        return new AutoPreflightResult(errors, warnings);
    }

    private static void ValidateProvisioning(ProvisioningManifest? manifest, List<string> errors)
    {
        if (manifest is null) return;
        ValidateEnum(manifest.Theme, nameof(manifest.Theme), errors);
        ValidateEnum(manifest.SystemTheme, nameof(manifest.SystemTheme), errors);
        ValidateEnum(manifest.AppsTheme, nameof(manifest.AppsTheme), errors);
        ValidateEnum(manifest.AccentColorMode, nameof(manifest.AccentColorMode), errors);
        ValidateEnum(manifest.TaskbarAlignment, nameof(manifest.TaskbarAlignment), errors);
        ValidateEnum(manifest.TaskbarSearchBox, nameof(manifest.TaskbarSearchBox), errors);
        ValidateEnum(manifest.PowerPlan, nameof(manifest.PowerPlan), errors);

        if (manifest.AccentColorMode == AccentColorMode.Personalizado
            && (manifest.AccentColor is not { Length: 7 } color || color[0] != '#'
                || !uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out _)))
            errors.Add("Cor de destaque personalizada inválida; use o formato #RRGGBB.");

        foreach ((string label, int? value) in new[]
        {
            ("Tempo de tela na tomada", manifest.DisplayTimeoutOnAc),
            ("Tempo de tela na bateria", manifest.DisplayTimeoutOnDc),
            ("Suspensão na tomada", manifest.StandbyTimeoutOnAc),
            ("Suspensão na bateria", manifest.StandbyTimeoutOnDc),
        })
        {
            if (value is < 0 or > 10080)
                errors.Add($"{label}: informe um valor entre 0 e 10080 minutos.");
        }

        if (manifest.AccentColorMode == AccentColorMode.Personalizado && manifest.AccentColor is null)
            errors.Add("Cor personalizada exige AccentColorMode=Personalizado e AccentColor no formato #RRGGBB.");
        if (!string.IsNullOrWhiteSpace(manifest.MachineName)
            && (manifest.MachineName.Length > 15
                || !Regex.IsMatch(manifest.MachineName, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)))
            errors.Add("Nome do computador inválido; use até 15 letras, números ou hífens e não comece/termine com hífen.");

        if (!string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64))
        {
            try
            {
                byte[] wallpaper = Convert.FromBase64String(manifest.WallpaperImageBase64);
                if (wallpaper.LongLength > 50L * 1024 * 1024)
                    errors.Add("O wallpaper excede o limite preventivo de 50 MB para o /auto.");
            }
            catch (FormatException) { errors.Add("A imagem do papel de parede não contém Base64 válido."); }
        }
    }

    private static void ValidateEnum<T>(T? value, string name, List<string> errors) where T : struct, Enum
    {
        if (value is { } candidate && !Enum.IsDefined(candidate))
            errors.Add($"Opção '{name}' contém um valor não reconhecido ({candidate}).");
    }
}
