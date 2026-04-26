using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;
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

// Add Presidio Analyzer client to PrivacyProxy
builder.Services.AddHttpClient<IPresidioAnalyzerClient, PresidioAnalyzerClient>(
                                                                                (sp, client) =>
                                                                                {
                                                                                    var opts = sp.GetRequiredService<IOptions<PresidioOptions>>().Value;
                                                                                    client.BaseAddress = new Uri(opts.AnalyzerUrl);
                                                                                });

// Add MappingStore to PrivacyProxy for storing mappings between PII and their Pseudonyms
// Scoped to ensure each HTTP request has its own instance.
builder.Services.AddScoped<IMappingStore, MappingStore>();

var options = new JsonSerializerOptions();

// To serialize enums as strings, not as numbers
options.Converters.Add(new JsonStringEnumConverter());

// To ignore null values when serializing, not writing them
options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();