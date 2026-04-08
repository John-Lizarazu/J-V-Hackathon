using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Po.Community.Core.Extensions;
using Po.Community.Core.Models;
using Po.Community.Core.Utilities;

namespace Po.Community.Core.McpTools;

public class CareGapsTool : IMcpTool
{
    private const string PatientIdParameter = "patientId";

    public string Name { get; } = "GetCareGaps";

    public string? Description { get; } =
        "Identifies simple potential care gaps for a patient, such as overdue A1C, blood pressure follow-up, or wellness visits.";

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

    public List<string> FhirScopes { get; } =
    [
        "patient/Patient.read",
        "patient/Condition.read",
        "patient/Observation.read",
        "patient/Encounter.read"
    ];

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

        var careGaps = new List<string>();

        // Pull conditions
        var conditionsBundle = await fhirClient.SearchAsync<Condition>(
            new string[] { $"patient={patientId}" }
        );

        var conditionNames = conditionsBundle?.Entry?
            .Select(e => e.Resource as Condition)
            .Where(c => c is not null)
            .Select(c => c!.Code?.Text ?? c!.Code?.Coding?.FirstOrDefault()?.Display ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList() ?? [];

        bool hasDiabetes = conditionNames.Any(c =>
            c.Contains("diabetes", StringComparison.OrdinalIgnoreCase));

        bool hasHypertension = conditionNames.Any(c =>
            c.Contains("hypertension", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("high blood pressure", StringComparison.OrdinalIgnoreCase));

        // Pull observations
        var observationsBundle = await fhirClient.SearchAsync<Observation>(
            new string[] { $"patient={patientId}" }
        );

        var observations = observationsBundle?.Entry?
            .Select(e => e.Resource as Observation)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToList() ?? [];

        // Pull encounters
        var encountersBundle = await fhirClient.SearchAsync<Encounter>(
            new string[] { $"patient={patientId}" }
        );

        var encounters = encountersBundle?.Entry?
            .Select(e => e.Resource as Encounter)
            .Where(enc => enc is not null)
            .Select(enc => enc!)
            .ToList() ?? [];

        // Rule 1: diabetes + no recent A1C
        if (hasDiabetes)
        {
            var latestA1c = observations
                .Where(o =>
                    (o.Code?.Text?.Contains("A1C", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (o.Code?.Text?.Contains("hemoglobin a1c", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (o.Code?.Coding?.Any(c =>
                        (c.Display?.Contains("A1C", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (c.Display?.Contains("hemoglobin a1c", StringComparison.OrdinalIgnoreCase) ?? false)
                    ) ?? false))
                .OrderByDescending(o => GetObservationDate(o))
                .FirstOrDefault();

            if (latestA1c is null || IsOlderThanMonths(GetObservationDate(latestA1c), 6))
            {
                careGaps.Add("A1C test may be overdue.");
            }
        }

        // Rule 2: hypertension + no recent BP check
        if (hasHypertension)
        {
            var latestBp = observations
                .Where(o =>
                    (o.Code?.Text?.Contains("blood pressure", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (o.Code?.Coding?.Any(c =>
                        c.Display?.Contains("blood pressure", StringComparison.OrdinalIgnoreCase) ?? false
                    ) ?? false))
                .OrderByDescending(o => GetObservationDate(o))
                .FirstOrDefault();

            if (latestBp is null || IsOlderThanMonths(GetObservationDate(latestBp), 6))
            {
                careGaps.Add("Blood pressure follow-up may be overdue.");
            }
        }

        // Rule 3: no recent encounter in the last 12 months
        var latestEncounter = encounters
            .OrderByDescending(GetEncounterDate)
            .FirstOrDefault();

        if (latestEncounter is null || IsOlderThanMonths(GetEncounterDate(latestEncounter), 12))
        {
            careGaps.Add("Annual wellness or follow-up visit may be overdue.");
        }

        // Patient name
        var officialName = patient.Name?.FirstOrDefault();
        var fullName = officialName is null
            ? "Unknown"
            : string.Join(" ", officialName.Given ?? []).Trim() + " " + officialName.Family;

        var summary = careGaps.Count == 0
            ? $"{fullName.Trim()} has no obvious care gaps based on the available data."
            : $"{fullName.Trim()} may have {careGaps.Count} potential care gap(s) requiring follow-up.";

        var result =
            $"""
            Care Gap Analysis
            -----------------
            Patient ID: {patient.Id}
            Name: {fullName.Trim()}

            Potential Care Gaps:
            {(careGaps.Count == 0 ? "- None identified" : string.Join(Environment.NewLine, careGaps.Select(g => $"- {g}")))}

            Summary:
            {summary}
            """;

        return McpToolUtilities.CreateTextToolResponse(result);
    }

    private static DateTimeOffset? GetObservationDate(Observation observation)
    {
        if (observation.Effective is FhirDateTime fhirDateTime &&
            DateTimeOffset.TryParse(fhirDateTime.Value, out var parsedDateTime))
        {
            return parsedDateTime;
        }

        if (observation.Issued is not null)
        {
            return observation.Issued;
        }

        return null;
    }

    private static DateTimeOffset? GetEncounterDate(Encounter encounter)
    {
        if (encounter.Period?.StartElement is not null &&
            DateTimeOffset.TryParse(encounter.Period.StartElement.Value, out var parsedStart))
        {
            return parsedStart;
        }

        return null;
    }

    private static bool IsOlderThanMonths(DateTimeOffset? date, int months)
    {
        if (date is null)
        {
            return true;
        }

        return date.Value < DateTimeOffset.UtcNow.AddMonths(-months);
    }
}