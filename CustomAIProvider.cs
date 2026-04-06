using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace MAF;

/// <summary>
/// Implementación de un proveedor de IA Personalizado para KPMG APIM usando la abstracción unificada de .NET
/// </summary>
public class CustomAIProvider : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ChatClientMetadata _metadata = new("KpmgApimProvider", default);
    
    private string? _aadToken;
    private DateTimeOffset _tokenExpiration = DateTimeOffset.MinValue;

    public ChatClientMetadata Metadata => _metadata;

    public CustomAIProvider(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    private async Task<string> GetAadTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_aadToken) && DateTimeOffset.UtcNow < _tokenExpiration)
        {
            return _aadToken;
        }

        // Credenciales desde AppSettings / Environment Variabes / Secrets
        var clientId = _config["KpmgApim:ClientId"] ?? throw new ArgumentException("Falta ClientId en configuración");
        var clientSecret = _config["KpmgApim:ClientSecret"] ?? throw new ArgumentException("Falta ClientSecret en configuración");
        
        // El Scope según la imagen, diferenciando PROD de NONPROD
        var isProd = bool.Parse(_config["KpmgApim:IsProd"] ?? "false");
        var scope = isProd 
            ? "22e4556b-94f5-470c-b8f6-8c1fff3587ec/.default" // PROD REST API
            : "2ed23f1c-2585-4169-a94f-ba3ccdb8ee8a/.default"; // NONPROD REST API

        // Endpoint POST para obtener el AAD Token temporal
        var tokenUrl = "https://login.microsoftonline.com/deff24bb-2089-4400-8c8e-f71e680378b2/oauth2/v2.0/token";

        var dict = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "scope", scope },
            { "grant_type", "client_credentials" }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(dict)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var jsonStr = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(jsonStr);
        _aadToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
        
        // Margen de seguridad antes de la expiración (expire 1 hr = 3600s, descontamos 60s)
        _tokenExpiration = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);

        return _aadToken!;
    }

    public async Task<ChatCompletion> CompleteAsync(
        IList<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        var token = await GetAadTokenAsync(cancellationToken);

        var isProd = bool.Parse(_config["KpmgApim:IsProd"] ?? "false");
        var baseUrl = isProd 
            ? "https://prod-pr-aiauto-api-mgmt.azure-api.net" 
            : "https://nprd-pr-aiauto-api-mgmt.azure-api.net";
            
        var deploymentId = _config["KpmgApim:DeploymentId"] ?? "gpt-4o-08-06-2024";
        var apiUrl = $"{baseUrl}/aoai/openai/deployments/{deploymentId}/chat/completions?api-version=2024-10-21";

        // Mapear ChatMessages de .NET AI al schema estándar de OpenAI "messages"
        var payload = new
        {
            messages = chatMessages.Select(m => new 
            { 
                role = m.Role.Value.ToLower(), 
                content = m.Text 
            }).ToArray()
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(payload)
        };

        // Agregar los Headers requeridos por la API KPMG
        // ocp-apim-subscription-key = APIM client subscription-key provided by Cloud DevOps
        httpRequest.Headers.Add("ocp-apim-subscription-key", _config["KpmgApim:SubscriptionKey"] ?? "");
        httpRequest.Headers.Add("ocp-apim-trace", "TRUE");
        
        // Token temporal AAD con formato Bearer (si aplica a la convención estándar) o envío raw
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        
        // Agregar info de cliente
        httpRequest.Headers.Add("SourceIP", _config["KpmgApim:SourceIP"] ?? "127.0.0.1"); 
        httpRequest.Headers.Add("UserID", _config["KpmgApim:UserID"] ?? "tu_email@kpmg.com");

        var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        using var responseDoc = JsonDocument.Parse(responseJson);
        var responseText = responseDoc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return new ChatCompletion(new ChatMessage(ChatRole.Assistant, responseText));
    }

    public async IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
        IList<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // La Request de APIM parece estándar para un REST payload síncrono.
        // Si no activamos validaciones "SSE (Server-Sent Events)" omitiremos la fragmentación completa,
        // esto ejecutará el bloque enviándolo por streaming de un solo frame.
        var completion = await CompleteAsync(chatMessages, options, cancellationToken);
        
        yield return new StreamingChatCompletionUpdate
        {
            Role = ChatRole.Assistant,
            Text = completion.Message.Text
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType == typeof(IChatClient) ? this : null;
    }

    public void Dispose()
    {
        // El HttpClient es inyectado y manejado por IHttpClientFactory, no lo destruimos aquí
    }
}
