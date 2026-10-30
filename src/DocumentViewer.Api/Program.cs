using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentViewer.Api.Endpoints;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddDocumentSources(builder.Configuration);
builder.Services.AddScoped<IDocumentAggregationService, DocumentAggregationService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapVehicleDocumentsEndpoint();

app.Run();

public partial class Program;
