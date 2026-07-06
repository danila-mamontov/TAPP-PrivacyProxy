using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Core.Configuration;
using PrivacyProxy.Api.Endpoints;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Live, hot-reloadable configuration written by the WebUI. The path is shared with the WebUI
// via ProxyConfigFile.ResolvePath() (env override -> /config volume in Docker -> temp folder
// locally) and layered above the .env / appsettings defaults. reloadOnChange + IOptionsMonitor
// let the API apply edits without a restart.
var liveConfigPath = ProxyConfigFile.ResolvePath();
Directory.CreateDirectory(Path.GetDirectoryName(liveConfigPath)!);
builder.Configuration.AddJsonFile(liveConfigPath, optional: true, reloadOnChange: true);

builder.Services.AddOpenApi();

// Configure JSON serialization for Minimal API endpoints
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower;
    opts.SerializerOptions.PropertyNameCaseInsensitive = true;
    opts.SerializerOptions.DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull;
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Add Presidio configuration and validation to PrivacyProxy and check on startup
builder.Services.AddOptions<PresidioOptions>()
       .BindConfiguration("Presidio")
       .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PresidioOptions>, PresidioOptionsValidator>();

// Add LLM configuration and validation to PrivacyProxy and check on startup
builder.Services.AddOptions<LlmOptions>()
       .BindConfiguration("Llm")
       .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();

// Add the default entity policy to handle entities recognized by Presidio
builder.Services.AddSingleton<IEntityPolicy, DefaultEntityPolicy>();

// Add Presidio Analyzer client to PrivacyProxy
builder.Services.AddHttpClient<IPresidioAnalyzerClient, PresidioAnalyzerClient>(
    (sp, client) =>
    {
        // Read the current (hot-reloadable) value so a recreated client picks up changes.
        var opts = sp.GetRequiredService<IOptionsMonitor<PresidioOptions>>().CurrentValue;
        client.BaseAddress = new Uri(opts.AnalyzerUrl);
    });

// Add LLM client to PrivacyProxy
builder.Services.AddHttpClient<ILlmClient, LlmClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IOptionsMonitor<LlmOptions>>().CurrentValue.BaseUrl;
    // Tolerate a missing trailing slash so ".../v1" and ".../v1/" both resolve correctly
    // (without the slash, the relative "chat/completions" would drop the "/v1" segment).
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
});

// Add MappingStore for mapping PII to placeholders. Scoped (per request): each chat-completion
// request gets its own, isolated mapping table. The whole round-trip (anonymize -> LLM ->
// deanonymize) happens within one request, so the mapping lives exactly as long as it is needed,
// while no state (placeholders, entity type, casing) bleeds between requests or users. Chat
// completions are stateless (the client resends the full, deanonymized history every turn), so a
// request-scoped store is sufficient and avoids cross-request contamination.
builder.Services.AddScoped<IMappingStore, MappingStore>();

// Add StreamingDeanonymizer to PrivacyProxy for deanonymizing text in real-time from LLM responses
builder.Services.AddScoped<StreamingDeanonymizer>();

// Add PresidioService to PrivacyProxy for handling PII detection and anonymization
builder.Services.AddScoped<IPresidioService, PresidioService>();

// Add ChatCompletionService to PrivacyProxy for handling chat completions
builder.Services.AddScoped<IChatCompletionService, ChatCompletionService>();

Log.Logger = new LoggerConfiguration()
             .ReadFrom.Configuration(builder.Configuration)
             .CreateLogger();

builder.Host.UseSerilog();

var app = builder.Build();

app.MapChatCompletion();

// Lightweight liveness endpoint — handy for reachability/tunnel checks and container healthchecks.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

Log.Information("PrivacyProxy starting up...");

Log.Information("Listening on Port(s): {Ports}", builder.Configuration["Urls"]);

app.Run();