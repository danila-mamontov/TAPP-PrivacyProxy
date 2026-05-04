using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Endpoints;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

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
        var opts = sp.GetRequiredService<IOptions<PresidioOptions>>().Value;
        client.BaseAddress = new Uri(opts.AnalyzerUrl);
    });

// Add LLM client to PrivacyProxy
builder.Services.AddHttpClient<ILlmClient, LlmClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
});

// Add MappingStore to PrivacyProxy for storing mappings between PII and their Pseudonyms
// Scoped to ensure each HTTP request has its own instance.
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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

Log.Information("PrivacyProxy starting up...");

Log.Information("Listening on Port(s): {Ports}", builder.Configuration["Urls"]);

app.Run();