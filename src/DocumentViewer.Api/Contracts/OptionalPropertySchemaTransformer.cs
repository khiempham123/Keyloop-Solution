using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace DocumentViewer.Api.Contracts;

/// <summary>
/// Removes nullable members from each schema's <c>required</c> list.
///
/// The host serialises with <c>DefaultIgnoreCondition = WhenWritingNull</c>, so a null
/// member is <b>absent</b> from the JSON rather than present as <c>null</c> — a healthy
/// source's <c>sources[]</c> entry has no <c>error</c> property at all. Left alone, the
/// generator marks those members required and the committed contract then rejects the
/// server's own happy-path response: a client generated from it with validation enabled
/// fails on every successful call. The client layer is this submission's stubbed
/// deliverable, so its contract has to describe the server.
///
/// This is a <b>schema</b> transformer rather than a document transformer because
/// <c>document.Components.Schemas</c> is still null when document transformers run —
/// the schemas are attached afterwards.
///
/// Optionality is read from the types themselves via <see cref="NullabilityInfoContext"/>,
/// so adding a nullable member cannot reintroduce the mismatch.
/// </summary>
internal sealed class OptionalPropertySchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Required is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        var parameters = PrimaryConstructorParametersOf(context.JsonTypeInfo.Type);
        if (parameters.Length == 0)
        {
            return Task.CompletedTask;
        }

        var nullability = new NullabilityInfoContext();

        foreach (var parameter in parameters)
        {
            if (parameter.Name is null || !IsOptional(nullability, parameter))
            {
                continue;
            }

            schema.Required.Remove(JsonNamingPolicy.CamelCase.ConvertName(parameter.Name));
        }

        return Task.CompletedTask;
    }

    private static ParameterInfo[] PrimaryConstructorParametersOf(Type type)
    {
        var constructor = type.GetConstructors()
            .OrderByDescending(static c => c.GetParameters().Length)
            .FirstOrDefault();

        return constructor?.GetParameters() ?? [];
    }

    private static bool IsOptional(NullabilityInfoContext nullability, ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || nullability.Create(parameter).WriteState == NullabilityState.Nullable;
}
