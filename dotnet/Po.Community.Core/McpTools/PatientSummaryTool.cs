using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Po.Community.Core.Extensions;
using Po.Community.Core.Models;
using Po.Community.Core.Utilities;

namespace Po.Community.Core.McpTools;

public class PatientSummaryTool : IMcpTool
{
    private const string PatientIdParameter = "patientId";

    public string Name { get; } = "GetPatientSummary";

    public string? Description { get; } =
        "Gets a short summary of a patient including name, gender, birth date, and age.";

    public List<McpToolArgument> Arguments { get; } =
    [
        new McpToolArgument
        {
            Type = "string",
            Name = PatientIdParameter,
            Description = "The patient's id. Optional if patient context already exists.",
            IsRequired = false,
        },
    ];

    public List<string> FhirScopes { get; } = ["patient/Patient.read"];

    public async Task<CallToolResult> HandleAsync(
        HttpContext httpContext,
        McpServer mcpServer,
        IServiceProvider serviceProvider,
        CallToolRequestParams context
    )
    {
        var patientId = httpContext.GetPatientIdIfContextExists();

        if (string.IsNullOrWhiteSpace(patientId))
        {
            patientId = context.GetRequiredArgumentValue(PatientIdParameter);
        }

        var fhirClient = httpContext.CreateFhirClientWithContext();
        var patient = await fhirClient.ReadAsync<Patient>($"Patient/{patientId}");

        if (patient is null)
        {
            return McpToolUtilities.CreateTextToolResponse(
                "The patient could not be found.",
                isError: true
            );
        }

        var officialName = patient.Name?.FirstOrDefault();
        var fullName = officialName is null
            ? "Unknown"
            : string.Join(" ", officialName.Given ?? []).Trim() + " " + officialName.Family;

        var gender = patient.Gender?.ToString() ?? "Unknown";
        var birthDate = patient.BirthDate ?? "Unknown";

        string ageText = "Unknown";
        var dob = patient.BirthDateElement?.ToSystemDate();
        if (dob?.Years is not null)
        {
            ageText = dob.ToDateTimeOffset(TimeSpan.Zero).GetAge().ToString();
        }

        var summary =
            $"""
            Patient Summary
            ----------------
            ID: {patient.Id}
            Name: {fullName.Trim()}
            Gender: {gender}
            Birth Date: {birthDate}
            Age: {ageText}
            """;

        return McpToolUtilities.CreateTextToolResponse(summary);
    }
}