using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentViewer.Api.Endpoints;
using DocumentViewer.Api.Middleware;
using DocumentViewer.Api.Observability;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservabilityLogging();
builder.AddObservabilityTelemetry();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddDocumentSources();
builder.Services.AddPersistence();
builder.Services.AddDocumentViewerHealthChecks();
builder.Services.AddScoped<IDocumentAggregationService, DocumentAggregationService>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseDocumentViewerRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapDocumentViewerHealthChecks();
app.MapVehicleDocumentsEndpoint();

app.Run();

public partial class Program;
