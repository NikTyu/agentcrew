using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AgentCrew.Core.Events;
using AgentCrew.Core.Llm;
using AgentCrew.Core.Orchestration;
using AgentCrew.Core.Tools;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
// Локальные секреты (ключ API) — в файле, который не попадает в git.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var llmOptions = builder.Configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
var crewOptions = builder.Configuration.GetSection("Crew").Get<CrewOptions>() ?? new CrewOptions();
crewOptions = crewOptions with { Runtime = crewOptions.Runtime with { Temperature = llmOptions.Temperature } };
var dataPath = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["DataPath"] ?? "data");

builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton(crewOptions);
builder.Services.AddSingleton(_ => KnowledgeBase.LoadFromDirectory(Path.Combine(dataPath, "knowledge")));
builder.Services.AddSingleton(_ => SalesData.LoadFromCsv(Path.Combine(dataPath, "sales.csv")));
builder.Services.AddSingleton<IChatClient>(_ => ChatClientFactory.Create(llmOptions));
builder.Services.AddSingleton(sp => CrewFactory.CreateDefault(sp.GetRequiredService<KnowledgeBase>(), sp.GetRequiredService<SalesData>(), crewOptions));
builder.Services.AddSingleton(sp => new CrewOrchestrator(sp.GetRequiredService<IChatClient>(), sp.GetRequiredService<AgentCatalog>(), crewOptions));

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.Encoder = jsonOptions.Encoder;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/info", (LlmOptions llm, AgentCatalog catalog, KnowledgeBase kb, SalesData sales) => new
{
    provider = ChatClientFactory.Describe(llm),
    demo = llm.IsDemo,
    agents = catalog.All.Select(a => new
    {
        id = a.Id,
        name = a.DisplayName,
        description = a.Description,
        tools = a.Tools.Select(t => new { name = t.Name, description = t.Description }),
    }),
    knowledge = new { documents = kb.Documents.Select(d => new { d.Id, d.Title }), chunks = kb.ChunkCount },
    sales = new { records = sales.Records.Count, quarters = sales.Quarters },
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Запуск команды агентов. Ответ — поток Server-Sent Events с трассой выполнения.
app.MapPost("/api/crew/run", async (RunRequest body, HttpContext http, CrewOrchestrator crew, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(body.Task) || body.Task.Length > 4000)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new { error = "Поле task обязательно (до 4000 символов)." });
        return;
    }

    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    var ct = http.RequestAborted;
    var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
    var request = new CrewRequest(body.Task.Trim(), body.History?.TakeLast(12).ToList());

    var run = Task.Run(async () =>
    {
        try
        {
            await crew.RunAsync(request, new ChannelEventSink(channel.Writer), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Клиент закрыл соединение — просто останавливаемся.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Crew run failed");
            channel.Writer.TryWrite(new AgentEvent(AgentEventType.Error, CrewOrchestrator.OrchestratorName, ex.Message));
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }, CancellationToken.None);

    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(evt, jsonOptions)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
    }
    await run;
});

app.Logger.LogInformation("LLM: {Provider}", ChatClientFactory.Describe(llmOptions));
app.Run();

internal sealed record RunRequest(string Task, List<ChatTurn>? History);

public partial class Program;
