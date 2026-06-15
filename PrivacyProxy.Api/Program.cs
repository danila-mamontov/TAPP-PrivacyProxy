using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Endpoints;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Live, hot-reloadable configuration written by the WebUI (a shared volume in Docker).
// Added after the default sources so it overrides the .env / appsettings defaults, and
// reloadOnChange lets the API pick up edits without a restart (consumed via IOptionsMonitor).
// Only wired up when the target directory exists (e.g. the mounted /config volume) so we
// never watch a non-existent path locally or in tests.
var liveConfigPath = Environment.GetEnvironmentVariable("PRIVACYPROXY_CONFIG_FILE")
                     ?? "/config/privacyproxy.json";
var liveConfigDir = Path.GetDirectoryName(Path.GetFullPath(liveConfigPath));
if (liveConfigDir is not null && Directory.Exists(liveConfigDir))
{
    builder.Configuration.AddJsonFile(liveConfigPath, optional: true, reloadOnChange: true);
}

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
    var opts = sp.GetRequiredService<IOptionsMonitor<LlmOptions>>().CurrentValue;
    client.BaseAddress = new Uri(opts.BaseUrl);
});

// Add Mapping configuration (TTL for MappingStore entries) and check on startup
builder.Services.AddOptions<MappingOptions>()
       .BindConfiguration("Mapping")
       .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MappingOptions>, MappingOptionsValidator>();

// Add MappingStore to PrivacyProxy for storing mappings between PII and their Pseudonyms
// Singleton, because we want to have a single session with every mapping for session requests.
builder.Services.AddSingleton<IMappingStore, MappingStore>();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

Log.Information("PrivacyProxy starting up...");

Log.Information("Listening on Port(s): {Ports}", builder.Configuration["Urls"]);

app.Run();