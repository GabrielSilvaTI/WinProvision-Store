using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using WinProvision.Core.Services.Backup;

namespace WinProvision.Core.Services.Provisioning;

/// <summary>
/// Guarda, entre uma sessão e outra, os dois valores mais repetitivos do gerador de
/// comando CLI da aba Provisionamento — o caminho/URL do perfil .json e a URL de webhook
/// de notificação (ex.: Discord) — pra não precisar procurar/colar o mesmo link toda vez
/// que for gerar um novo comando /auto ou /Provision. Editável tanto em Configurações
/// (seção Conta) quanto na própria tela de Provisionamento ("Salvar como padrão"); os
/// dois lugares leem/escrevem o mesmo arquivo, então ficam sempre em sincronia.
///
/// O caminho do perfil não é sensível e fica salvo em texto puro. Já a URL do webhook
/// normalmente carrega um token embutido (ex.: um link de Discord Webhook dá pra postar
/// no canal pra quem o tiver) — por isso ela é criptografada em disco com o mesmo
/// mecanismo (DPAPI/CurrentUser) usado para o Personal Access Token do GitHub
/// (ver <see cref="SecureTokenStore"/>).
/// </summary>
public sealed class CliPresetsService
{
    private readonly string _presetsFilePath;
    private readonly string _webhookFilePath;

    /// <summary>Disparado depois de qualquer Save() — permite outra tela aberta ao mesmo
    /// tempo (ex.: Configurações e Provisionamento, ambas Singleton) refletir o valor mais
    /// recente sem precisar reabrir a página.</summary>
    public event Action? Changed;

    public string? ProfilePathOrUrl { get; private set; }
    public string? WebhookUrl { get; private set; }

    public CliPresetsService() : this(DefaultDir())
    {
    }

    internal CliPresetsService(string dir)
    {
        _presetsFilePath = Path.Combine(dir, "cli-presets.json");
        _webhookFilePath = Path.Combine(dir, "cli-webhook.dat");

        LoadProfilePath();
        LoadWebhookUrl();
    }

    private static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvision");

    private void LoadProfilePath()
    {
        if (!File.Exists(_presetsFilePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_presetsFilePath);
            ProfilePathOrUrl = JsonSerializer.Deserialize<PresetsData>(json)?.ProfilePathOrUrl;
        }
        catch (JsonException)
        {
            // Arquivo corrompido — melhor-esforço, começa do zero sem travar Configurações/Provisionamento.
        }
    }

    [SupportedOSPlatform("windows")]
    private void LoadWebhookUrl() => WebhookUrl = SecureTokenStore.TryLoad(_webhookFilePath);

    public void SaveProfilePathOrUrl(string? value)
    {
        ProfilePathOrUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_presetsFilePath)!);
            string json = JsonSerializer.Serialize(new PresetsData { ProfilePathOrUrl = ProfilePathOrUrl });
            File.WriteAllText(_presetsFilePath, json);
        }
        catch (IOException)
        {
            // Melhor-esforço — mesma filosofia do resto do app: uma falha de disco aqui
            // não pode travar a tela de Configurações/Provisionamento.
        }

        Changed?.Invoke();
    }

    [SupportedOSPlatform("windows")]
    public void SaveWebhookUrl(string? value)
    {
        WebhookUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        Directory.CreateDirectory(Path.GetDirectoryName(_webhookFilePath)!);
        if (WebhookUrl is null)
        {
            SecureTokenStore.Delete(_webhookFilePath);
        }
        else
        {
            SecureTokenStore.Save(_webhookFilePath, WebhookUrl);
        }

        Changed?.Invoke();
    }

    private sealed class PresetsData
    {
        public string? ProfilePathOrUrl { get; set; }
    }
}
