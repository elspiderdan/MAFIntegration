using MAF;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

var builder = WebApplication.CreateBuilder(args);

// 1. Registramos nuestro proveedor Custom de IA inyectando un HttpClient
builder.Services.AddHttpClient<IChatClient, CustomAIProvider>();

// 2. Configuramos el Agent Framework de Microsoft
// El ChatClientAgent envuelve nuestro cliente base y le puede agregar funcionalidades
// avanzadas como function calling, sistema de instrucciones o flujos multiagente.
builder.Services.AddTransient<ChatClientAgent>(sp => 
{
    var baseChatClient = sp.GetRequiredService<IChatClient>();
    
    var agent = new ChatClientAgent(baseChatClient, new ChatClientAgentOptions 
    {
        Name = "MainAssistant",
        Description = "Eres un asistente utilitario alimentado por una IA custom."
    });
    return agent;
});

var app = builder.Build();

// 3. Endpoint único expuesto en Minimal API para ejecutar el prompt
app.MapPost("/api/prompt", async (PromptRequest request, ChatClientAgent agent) =>
{
    // Ejecutamos la petición hacia el Agente, el cual terminará delegando
    // en nuestro CustomAIProvider para procesar el Completion genérico.
    var completion = await agent.RunAsync(request.Prompt);
    
    return Results.Ok(new 
    { 
        AgentName = agent.Name,
        Response = completion.Text 
    });
});

app.Run();

// Registro de modelo de datos para petición entrante
public record PromptRequest(string Prompt);
