using System.Globalization;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed record ProfileJsonValidationResult(
    bool IsValid,
    string Message,
    string? Path = null,
    long? Line = null,
    long? Column = null);

/// <summary>
/// Valida o contrato do JSON exportado pelo aplicativo antes de ele ser salvo.
/// A validação combina a forma do documento com a desserialização do modelo, sem
/// alterar nem normalizar o texto que o usuário está editando.
/// </summary>
public static class ProfileJsonValidator
{
    public static ProfileJsonValidationResult Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Invalid("O documento está vazio.");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Invalid("A raiz do documento deve ser um objeto JSON.");

            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out _))
                return Invalid("O campo 'schemaVersion' é obrigatório e deve ser um número inteiro.", "$.schemaVersion");

            if (!root.TryGetProperty("createdAt", out var createdAt) ||
                createdAt.ValueKind != JsonValueKind.String ||
                !DateTime.TryParse(createdAt.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _))
                return Invalid("O campo 'createdAt' é obrigatório e deve ser uma data ISO-8601.", "$.createdAt");

            if (root.TryGetProperty("apps", out var apps))
            {
                var appsResult = ValidateApps(apps, "$.apps");
                if (appsResult is not null)
                    return appsResult;
            }
            else if (root.TryGetProperty("tabs", out var tabs))
            {
                if (tabs.ValueKind != JsonValueKind.Array)
                    return Invalid("O campo 'tabs' deve ser uma lista.", "$.tabs");

                int tabIndex = 0;
                foreach (var tab in tabs.EnumerateArray())
                {
                    if (tab.ValueKind != JsonValueKind.Object ||
                        !tab.TryGetProperty("apps", out var tabApps))
                        return Invalid("Cada item de 'tabs' precisa conter uma lista 'apps'.", $"$.tabs[{tabIndex}]");

                    var appsResult = ValidateApps(tabApps, $"$.tabs[{tabIndex}].apps");
                    if (appsResult is not null)
                        return appsResult;
                    tabIndex++;
                }
            }
            else
                return Invalid("O documento precisa conter 'apps' ou 'tabs'.");

            if (root.TryGetProperty("provisioning", out var provisioning) &&
                provisioning.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return Invalid("O campo 'provisioning' deve ser um objeto ou null.", "$.provisioning");

            if (ProfileManifestParser.Parse(json) is null)
                return Invalid("O documento não corresponde a um perfil ou backup reconhecido.");
            return new ProfileJsonValidationResult(true, "Perfil válido.");
        }

        catch (JsonException ex)
        {
            long? line = ex.LineNumber is null ? null : ex.LineNumber.Value + 1;
            long? column = ex.BytePositionInLine is null ? null : ex.BytePositionInLine.Value + 1;
            string location = line is { } l && column is { } c
                ? $"Linha {l}, coluna {c}."
                : "Revise a sintaxe.";
            return Invalid($"JSON inválido. {location}", null, line, column);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return Invalid($"Estrutura incompatível com o perfil: {ex.Message}");
        }
        catch (System.Reflection.TargetParameterCountException ex)
        {
            return Invalid($"Estrutura incompatível com o perfil: {ex.Message}");
        }
    }

    private static ProfileJsonValidationResult? ValidateApps(JsonElement apps, string path)
    {
        if (apps.ValueKind != JsonValueKind.Array)
            return Invalid("O campo 'apps' deve ser uma lista.", path);

        int index = 0;
        foreach (var app in apps.EnumerateArray())
        {
            if (app.ValueKind != JsonValueKind.Object)
                return Invalid("Cada item de 'apps' deve ser um objeto.", $"{path}[{index}]");

            if (!app.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
                return Invalid("Cada aplicativo precisa de um 'id' não vazio.", $"{path}[{index}].id");
            index++;
        }

        return null;
    }

    private static ProfileJsonValidationResult Invalid(
        string message,
        string? path = null,
        long? line = null,
        long? column = null) =>
        new(false, message, path, line, column);
}
