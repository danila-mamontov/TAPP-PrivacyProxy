using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
using PrivacyProxy.Api.Endpoints;
using PrivacyProxy.Api.Models.Interfaces;
using PrivacyProxy.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

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
// That writes them via chunks and not one response.
builder.Services.AddScoped<StreamingDeanonymizer>();

// Add PresidioService to PrivacyProxy for handling PII detection and anonymization
// Scoped to ensure each HTTP request has its own instance.
builder.Services.AddScoped<IPresidioService, PresidioService>();

// Add ChatCompletionService to PrivacyProxy for handling chat completions
// Scoped to ensure each HTTP request has its own instance.
// Used by the ChatCompletionEndpoint.
builder.Services.AddScoped<ChatCompletionService>();

var options = new JsonSerializerOptions();

// To serialize enums as strings, not as numbers
options.Converters.Add(new JsonStringEnumConverter());

// To ignore null values when serializing, not writing them
options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

var app = builder.Build();

// Map the endpoint v1/chat/completions to the ChatCompletionEndpoint
app.MapChatCompletion();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();