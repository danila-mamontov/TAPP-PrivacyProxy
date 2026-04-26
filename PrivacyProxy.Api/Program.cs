using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PrivacyProxy.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddOptions<PresidioOptions>()
       .BindConfiguration("Presidio")
       .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<PresidioOptions>, PresidioOptionsValidator>();

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